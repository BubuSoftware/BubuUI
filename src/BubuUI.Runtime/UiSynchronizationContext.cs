using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace BubuUI;

/// <summary>
/// 单线程 UI 同步上下文（design/15 §15.14）。
///
/// 目的：让 <c>async void</c> 事件处理器的 await 续体回到 UI 线程，再由宿主在
/// <c>ProcessInvalidations</c> 之前排空队列并整体失效挂载子树一次。没有 Dispatcher、
/// 没有消息循环抽象、没有组件树 diff——队列是唯一允许的跨线程操作。
///
/// 用法：宿主在 UI 线程调用 <see cref="Install"/>（<c>using</c> 包裹帧循环），
/// 每个 tick 在消息泵之后调用 <see cref="Drain"/>，返回值 &gt; 0 时由宿主触发一次
/// <see cref="UiObject.InvalidateSubtree"/>。装配/还原与排空都由宿主负责，本类不持有元素树。
/// </summary>
public sealed class UiSynchronizationContext : SynchronizationContext
{
    private static readonly ConditionalWeakTable<UiSynchronizationContext, UiThreadScope> Scopes = new();
    private readonly object _gate = new();
    private readonly Queue<WorkItem> _queue = new();
    private UiObject? _boundRoot;

    /// <summary>在安装线程上创建；<see cref="UiThreadId"/> 即该线程。</summary>
    public UiSynchronizationContext()
    {
        UiThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>安装时记录的 UI 线程 id（§15.14.2；Debug 线程亲和断言与回归测试用）。</summary>
    public int UiThreadId { get; }

    /// <summary>当前线程是否就是安装线程。</summary>
    public bool IsUiThread => Environment.CurrentManagedThreadId == UiThreadId;

    /// <summary>尚未排空的工作项数（线程安全读取；测试与诊断用）。</summary>
    public int PendingCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    /// <summary>
    /// 单队列上下文：框架在捕获上下文时可能调用 CreateCopy，返回新实例会让续体
    /// 投进另一条永远不会被排空的队列。这里必须共享同一个实例。
    /// </summary>
    public override SynchronizationContext CreateCopy() => this;

    /// <summary>唯一允许的跨线程操作：线程安全入队。等待宿主下一轮 <see cref="Drain"/>。</summary>
    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        lock (_gate)
            _queue.Enqueue(new WorkItem(d, state));
    }

    /// <summary>
    /// 同线程内联执行；跨线程 = Post + 阻塞等到宿主排空该工作项。
    /// 有 WPF 式死锁面（UI 线程阻塞在依赖 UI 的 Send 上），v0 接受，见 §15.14.3。
    /// </summary>
    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (IsUiThread)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim(false);
        ExceptionDispatchInfo? failure = null;
        Post(_ =>
        {
            try { d(state); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { done.Set(); }
        }, null);

        done.Wait();
        failure?.Throw();
    }

    /// <summary>
    /// 在 UI 线程按「快照长度」执行当批工作项并返回执行数；回调内新 Post 的项留到下一批，
    /// 因此自投递续体不会饿死帧循环。每个回调单独 try/catch，批量结束后以
    /// <see cref="AggregateException"/> 在 UI 线程 fail-fast（§15.14.2 第 4 条）。
    /// </summary>
    public int Drain()
    {
        if (!IsUiThread)
            throw new InvalidOperationException(
                "UiSynchronizationContext.Drain must run on the UI thread that installed it.");

        WorkItem[] batch;
        lock (_gate)
        {
            if (_queue.Count == 0)
                return 0;
            batch = _queue.ToArray();
            _queue.Clear();
        }

        // 回调内再 await（Task.Yield 等）必须还能捕获到同一个上下文。
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            List<Exception>? failures = null;
            foreach (var work in batch)
            {
                try { work.Callback(work.State); }
                catch (Exception ex) { (failures ??= new List<Exception>()).Add(ex); }
            }
            if (failures is not null)
                throw new AggregateException("UI-thread callback(s) failed.", failures);
            return batch.Length;
        }
        finally
        {
            Restore(previous);
        }
    }

    /// <summary>
    /// 在当前（UI）线程安装新上下文，可选绑定到宿主根元素供 Debug 线程亲和断言使用。
    /// 释放返回的 scope 会还原原上下文并解除绑定；帧循环必须用 <c>using</c> 包裹。
    /// </summary>
    public static UiThreadScope Install(Element? root = null)
    {
        var previous = Current;
        var context = new UiSynchronizationContext();
        SetSynchronizationContext(context);
        try
        {
            if (root is not null)
                context.AttachTo(root);
            var scope = new UiThreadScope(context, previous);
            Scopes.Add(context, scope);
            return scope;
        }
        catch
        {
            Restore(previous);
            throw;
        }
    }

    /// <summary>把上下文绑定到宿主根元素；断言沿元素父链查找（元素在块内动态创建也能覆盖）。</summary>
    public void AttachTo(Element root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _boundRoot?.ClearUiContext(this);
        _boundRoot = root.SchedulingRoot;
        _boundRoot.BindUiContext(this);
    }

    private void Detach()
    {
        _boundRoot?.ClearUiContext(this);
        _boundRoot = null;
    }

    private static void Restore(SynchronizationContext? previous)
    {
        while (previous is UiSynchronizationContext context
            && Scopes.TryGetValue(context, out var scope) && scope.IsDisposed)
            previous = scope.Previous;
        SetSynchronizationContext(previous);
        if (previous is UiSynchronizationContext restored)
            restored._boundRoot?.BindUiContext(restored);
    }

    /// <summary>安装范围：退出时还原 <see cref="SynchronizationContext"/> 并解除根绑定。</summary>
    public sealed class UiThreadScope : IDisposable
    {
        private readonly SynchronizationContext? _previous;
        private bool _disposed;

        internal UiThreadScope(UiSynchronizationContext context, SynchronizationContext? previous)
        {
            Context = context;
            _previous = previous;
        }

        public UiSynchronizationContext Context { get; }
        internal bool IsDisposed => _disposed;
        internal SynchronizationContext? Previous => _previous;

        /// <summary>等价于帧循环里的 drain 步骤（不含自动失效，那是宿主的职责）。</summary>
        public int Drain()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Context.Drain();
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!Context.IsUiThread)
                throw new UiThreadViolationException("UiThreadScope.Dispose", Context.UiThreadId,
                    Environment.CurrentManagedThreadId);
            _disposed = true;
            Context.Detach();
            // An outer host can end while another scope is current. Do not replace that
            // scope, and skip ended outer scopes when an inner scope eventually exits.
            if (ReferenceEquals(Current, Context))
                Restore(_previous);
        }
    }

    private readonly record struct WorkItem(SendOrPostCallback Callback, object? State);
}

/// <summary>跨线程写 UI 状态（§15.14.3 的 Debug 线程亲和断言）。仅在 DEBUG 构建产生调用点。</summary>
public sealed class UiThreadViolationException : InvalidOperationException
{
    public UiThreadViolationException(string operation, int expectedThreadId, int actualThreadId)
        : base($"{operation} must run on the UI thread ({expectedThreadId}); " +
               $"current thread is {actualThreadId}. Await continuations must return through " +
               "UiSynchronizationContext; ConfigureAwait(false) and raw thread-pool writes are undefined behavior.")
    {
        ExpectedThreadId = expectedThreadId;
        ActualThreadId = actualThreadId;
    }

    public int ExpectedThreadId { get; }
    public int ActualThreadId { get; }
}

/// <summary>
/// Debug 线程亲和守卫。以「可捕获异常」实现断言而不是 <see cref="Debug.Assert"/>：
/// 后者在 .NET 测试宿主里会直接终止进程，无法写回归；Release 下调用点被
/// <see cref="ConditionalAttribute"/> 整体消除，热路径零成本。
///
/// 只在「元素所属的宿主安装了 <see cref="UiSynchronizationContext"/>」时才判定——
/// 没装上下文的无头用法沿用 §15.2.5 的单线程约定（是约定，不是机制）。
/// </summary>
public static class UiThreadGuard
{
    [Conditional("DEBUG")]
    public static void AssertUiThread(UiObject? element, string operation)
    {
        var context = element?.FindUiContext();
        if (context is null || context.IsUiThread)
            return;
        throw new UiThreadViolationException(operation, context.UiThreadId, Environment.CurrentManagedThreadId);
    }
}
