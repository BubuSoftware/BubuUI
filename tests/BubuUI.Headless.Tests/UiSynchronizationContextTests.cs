using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BubuUI;
using BubuUI.Elements;
using Xunit;

// xUnit1031：这些用例必须保持同步 —— 「UI 线程」就是本测试线程，且 drain 只能在该线程调用。
// 改成 async 测试会经 xunit 的 AsyncTestSyncContext 调度续体、可能切换线程，把线程亲和断言
// 变成假绿；跨线程辅助任务只做一次写入后即结束，用 Task 等待是刻意的。
#pragma warning disable xUnit1031

namespace BubuUI.Headless.Tests;

/// <summary>
/// design/15 §15.14 线程调度回归：单线程同步上下文 + 帧循环排空 + 按批自动失效。
/// 无头宿主不自带帧循环，测试用 <see cref="HeadlessRoot.InstallSynchronizationContext"/> /
/// <see cref="HeadlessRoot.DrainPostedWork"/> 显式刻画 §15.14.4 的 N / N+1 两个 tick。
/// </summary>
public class UiSynchronizationContextTests
{
    private static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(5);

    private sealed class AsyncComponent : global::BubuUI.Elements.StackPanel
    {
        private TextRun _run = null!;

        public int Value;
        public int RenderCount;
        public int ContinuationThreadId;
        public int SecondContinuationThreadId;
        public Action Handler;

        public TextBlock Output { get; } = new();
        public Button Trigger { get; private set; } = null!;
        public TextRun Run => _run;

        public AsyncComponent() => Handler = HandleYield;

        protected override void InitializeUi()
        {
            _run = Output.AddRun("0");
            Children.Add(Output);
            Trigger = new Button { Label = "go" };
            // 生成器事件包装形态：h(); Invalidate(); —— 本节刻意不改它。
            Trigger.AddHandler(Button.ClickEvent, () => { Handler(); Invalidate(); });
            Children.Add(Trigger);
        }

        protected override void Render()
        {
            RenderCount++;
            _run.Text = Value.ToString();
        }

        private async void HandleYield()
        {
            await Task.Yield();
            ContinuationThreadId = Environment.CurrentManagedThreadId;
            Value += 5;
        }

        public async void HandleBackground()
        {
            await Task.Run(() => 7);
            ContinuationThreadId = Environment.CurrentManagedThreadId;
            await Task.Delay(1);
            SecondContinuationThreadId = Environment.CurrentManagedThreadId;
            Value += 3;
        }

        public async void HandleConfigureAwaitFalse()
        {
            await Task.Delay(1).ConfigureAwait(false);
            ContinuationThreadId = Environment.CurrentManagedThreadId;
            _run.Text = "off-thread"; // 显式逃生舱：跨线程写，必须被 Debug 断言抓到
        }

        public async void HandleThrow()
        {
            await Task.Yield();
            ThrowCore();
        }

        private static void ThrowCore() => throw new InvalidOperationException("handler-boom");
    }

    private sealed class CountingComponent : global::BubuUI.Elements.StackPanel
    {
        public int RenderCount { get; private set; }
        protected override void Render() => RenderCount++;
    }

    /// <summary>子控件只需进入真实内容树，即可参与子树更新。</summary>
    private sealed class ParentWithBlockChild : global::BubuUI.Elements.StackPanel
    {
        public readonly CountingComponent Child = new();
        public int RenderCount { get; private set; }

        protected override void InitializeUi()
        {
            Children.Add(Child);
        }

        protected override void Render() => RenderCount++;
    }

    private static bool WaitForPost(UiSynchronizationContext context)
        => SpinWait.SpinUntil(() => context.PendingCount > 0, PostTimeout);

    /// <summary>真生成模板（不是手写控件）的端到端：事件包装由生成器发射。</summary>
    [Fact]
    public void GeneratedTemplate_AsyncVoidHandler_ReturnsToUiThreadAndRepaints()
    {
        var component = new AsyncProbe();
        var host = new HeadlessRoot(component);
        using var ui = host.InstallSynchronizationContext();
        host.UpdateLayout();

        var text = Assert.Single(host.Content.Descendants<TextBlock>());
        var button = Assert.Single(host.Content.Descendants<Button>());
        Assert.Equal("0", text.FullText());

        button.Click();
        host.UpdateLayout(); // tick N：包装 Invalidate 消费旧状态
        Assert.Equal("0", text.FullText());
        Assert.Equal(1, ui.Context.PendingCount);

        Assert.Equal(1, ui.Drain());
        host.UpdateLayout(); // tick N+1：自动失效 → Render 写新文字

        Assert.Equal("5", text.FullText());
        Assert.Equal(ui.Context.UiThreadId, component.ContinuationThreadId);
        Assert.Equal(5, component.count);
    }

    // ---- §15.14.7：async void + Task.Yield 的两帧时序 --------------------------------

    [Fact]
    public void AsyncVoid_Yield_ReturnsToUiThread_AndRepaintsOnNextTick()
    {
        var component = new AsyncComponent();
        var host = new HeadlessRoot(component);
        using var ui = host.InstallSynchronizationContext();

        component.Trigger.Click(); // h() 到 await 返回，续体 Post 入队；包装 Invalidate 消费旧状态
        Assert.Equal(1, ui.Context.PendingCount);
        Assert.Equal(0, component.Value);

        // tick N：渲染旧状态 "0"
        host.UpdateLayout();
        Assert.Equal("0", component.Run.Text);
        Assert.Equal(1, component.RenderCount);
        var versionBefore = component.Output.RenderVersion;

        // tick N+1：drain → 续体在 UI 线程写 5 → 按批整体失效 → Render → 段重建
        Assert.Equal(1, ui.Drain());
        host.UpdateLayout();

        Assert.Equal(ui.Context.UiThreadId, component.ContinuationThreadId);
        Assert.Equal(5, component.Value);
        Assert.Equal("5", component.Run.Text);
        Assert.Equal(2, component.RenderCount);
        Assert.True(component.Output.RenderVersion > versionBefore,
            "TextBlock 段必须因 run 文字变化重建（D113 教训：只断言模型会一起绿）");
        Assert.Equal(0, ui.Context.PendingCount);
    }

    [Fact]
    public void MultipleAwaits_AllContinuationsReturnToTheUiThread()
    {
        var component = new AsyncComponent();
        component.Handler = component.HandleBackground;
        var host = new HeadlessRoot(component);
        using var ui = host.InstallSynchronizationContext();

        component.Trigger.Click();
        Assert.True(WaitForPost(ui.Context), "Task.Run 的续体未投递到 UI 上下文");
        Assert.Equal(1, ui.Drain());

        Assert.True(WaitForPost(ui.Context), "Task.Delay 的续体未投递到 UI 上下文");
        Assert.Equal(1, ui.Drain());
        host.UpdateLayout();

        Assert.Equal(ui.Context.UiThreadId, component.ContinuationThreadId);
        Assert.Equal(ui.Context.UiThreadId, component.SecondContinuationThreadId);
        Assert.Equal(3, component.Value);
        Assert.Equal("3", component.Run.Text);
    }

    [Fact]
    public void SynchronousHandler_WithInstalledContext_StillRendersInSameTick()
    {
        var component = new AsyncComponent();
        component.Handler = () => component.Value += 2;
        var host = new HeadlessRoot(component);
        using var ui = host.InstallSynchronizationContext();

        component.Trigger.Click();
        host.UpdateLayout();

        Assert.Equal("2", component.Run.Text);
        Assert.Equal(0, ui.Context.PendingCount);
        Assert.Equal(1, component.RenderCount);
    }

    // ---- §15.14.7：自投递续体不饿死帧循环 ------------------------------------------

    [Fact]
    public void SelfPostingContinuation_RunsOneBatchPerTick()
    {
        var previous = SynchronizationContext.Current;
        using var scope = UiSynchronizationContext.Install();
        var context = scope.Context;
        var ticks = 0;
        Action? self = null;
        self = () =>
        {
            ticks++;
            if (ticks < 5) context.Post(_ => self!(), null);
        };

        context.Post(_ => self(), null);
        for (var i = 1; i <= 5; i++)
        {
            Assert.Equal(1, context.Drain()); // 快照批：本回调新 Post 的留到下一 tick
            Assert.Equal(i, ticks);
            Assert.Equal(i < 5 ? 1 : 0, context.PendingCount);
        }
        Assert.Equal(0, context.Drain());
        Assert.Equal(5, ticks);
        Assert.Same(context, SynchronizationContext.Current); // scope 内仍是本上下文
        scope.Dispose();
        Assert.Same(previous, SynchronizationContext.Current);
    }

    // ---- §15.14.7：异常收回 UI 线程 --------------------------------------------------

    [Fact]
    public void ThrowingCallback_CollectsIntoAggregateOnUiThread_AndDrainsRestOfBatch()
    {
        using var scope = UiSynchronizationContext.Install();
        var context = scope.Context;
        var secondRan = false;
        context.Post(_ => throw new InvalidOperationException("boom"), null);
        context.Post(_ => secondRan = true, null);

        var error = Assert.Throws<AggregateException>(() => context.Drain());
        Assert.Contains(error.InnerExceptions, e => e.Message == "boom");
        Assert.True(secondRan, "一个回调失败不应吞掉同批其余回调");
        Assert.Equal(0, context.PendingCount);
        Assert.Equal(context.UiThreadId, Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void AsyncVoidException_IsPostedBackAndFailsFastOnUiThread()
    {
        var component = new AsyncComponent();
        component.Handler = component.HandleThrow;
        var host = new HeadlessRoot(component);
        using var ui = host.InstallSynchronizationContext();

        component.Trigger.Click();
        Assert.Equal(1, ui.Context.PendingCount);
        Assert.Equal(1, ui.Context.Drain());              // 续体抛出，async void builder 把异常 Post 回来
        Assert.Equal(1, ui.Context.PendingCount);

        var error = Assert.Throws<AggregateException>(() => ui.Drain());
        Assert.Contains("handler-boom", error.ToString());
        Assert.Contains(nameof(AsyncComponent), error.ToString());
        Assert.Equal(ui.Context.UiThreadId, Environment.CurrentManagedThreadId);
    }

    // ---- §15.14.7：Debug 线程亲和 / ConfigureAwait(false) ----------------------------

#if DEBUG
    [Fact]
    public void CrossThreadSetValue_IsFlaggedByTheUiThreadGuard()
    {
        var component = new AsyncComponent();
        var host = new HeadlessRoot(component);
        using var ui = host.InstallSynchronizationContext();

        // A dedicated worker prevents a blocking wait from inlining the task onto the UI thread.
        var error = Task.Factory.StartNew(() =>
        {
            try { component.Run.Text = "off-thread"; return (Exception?)null; }
            catch (Exception ex) { return ex; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();

        var violation = Assert.IsType<UiThreadViolationException>(error);
        Assert.Equal(ui.Context.UiThreadId, violation.ExpectedThreadId);
        Assert.NotEqual(ui.Context.UiThreadId, violation.ActualThreadId);
    }

    [Fact]
    public void ConfigureAwaitFalse_WriteOffUiThread_IsReportedBackThroughTheContext()
    {
        var component = new AsyncComponent();
        component.Handler = component.HandleConfigureAwaitFalse;
        var host = new HeadlessRoot(component);
        using var ui = host.InstallSynchronizationContext();

        component.Trigger.Click();
        // ConfigureAwait(false) 的续体不经 Post、直接落线程池；写状态触发断言后，
        // async void builder 把异常 Post 回 UI 上下文 —— 队列里第一批就是异常回调。
        Assert.True(WaitForPost(ui.Context), "处理器异常未回到 UI 上下文");
        Assert.NotEqual(ui.Context.UiThreadId, component.ContinuationThreadId);

        var error = Assert.Throws<AggregateException>(() => ui.Drain());
        Assert.Contains(error.Flatten().InnerExceptions, e => e is UiThreadViolationException);
        Assert.Equal(ui.Context.UiThreadId, Environment.CurrentManagedThreadId);
    }
#endif

    // ---- §15.14.7：失效成本 = 挂载组件数（一次 drain 一次整体失效）--------------------

    [Fact]
    public void DrainBatch_InvalidatesTheWholeMountedSubtreeOnce_IncludingBlockChildren()
    {
        var parent = new ParentWithBlockChild();
        var host = new HeadlessRoot(parent);
        using var ui = host.InstallSynchronizationContext();

        ui.Context.Post(_ => { }, null);
        Assert.Equal(1, ui.Drain());
        host.UpdateLayout();

        Assert.Equal(1, parent.RenderCount);
        Assert.Equal(1, parent.Child.RenderCount); // 内容树内的子控件不能漏

        Assert.Equal(0, ui.Drain());
        host.UpdateLayout();
        Assert.Equal(1, parent.RenderCount);
        Assert.Equal(1, parent.Child.RenderCount); // 空批不重复失效
    }

    // ---- Post / Send / Install 契约 --------------------------------------------------

    [Fact]
    public void Post_FromBackgroundThreads_IsTheOnlyCrossThreadOperation()
    {
        using var scope = UiSynchronizationContext.Install();
        var context = scope.Context;
        var threadIds = new ConcurrentBag<int>();

        Parallel.For(0, 500, i => context.Post(_ => threadIds.Add(Environment.CurrentManagedThreadId), i));

        Assert.Equal(500, context.PendingCount);
        Assert.Equal(500, context.Drain());
        Assert.Equal(500, threadIds.Count);
        Assert.All(threadIds, id => Assert.Equal(context.UiThreadId, id));
        Assert.Equal(0, context.PendingCount);
    }

    [Fact]
    public void Send_IsInlineOnUiThread_AndWaitsForDrainFromOtherThreads()
    {
        using var scope = UiSynchronizationContext.Install();
        var context = scope.Context;
        var inlineThread = 0;
        context.Send(_ => inlineThread = Environment.CurrentManagedThreadId, null);
        Assert.Equal(context.UiThreadId, inlineThread);
        Assert.Equal(0, context.PendingCount);

        var sendThread = 0;
        var task = Task.Run(() => context.Send(_ => sendThread = Environment.CurrentManagedThreadId, null));
        Assert.True(SpinWait.SpinUntil(() => context.PendingCount == 1, PostTimeout));
        Assert.False(task.IsCompleted, "跨线程 Send 必须等到宿主排空");
        Assert.Equal(1, context.Drain());
        task.GetAwaiter().GetResult();
        Assert.Equal(context.UiThreadId, sendThread);
    }

    [Fact]
    public void Send_PropagatesCallbackFailureToTheCaller()
    {
        using var scope = UiSynchronizationContext.Install();
        var context = scope.Context;
        var task = Task.Run(() =>
        {
            try { context.Send(_ => throw new InvalidOperationException("send-boom"), null); return (Exception?)null; }
            catch (Exception ex) { return ex; }
        });
        Assert.True(SpinWait.SpinUntil(() => context.PendingCount == 1, PostTimeout));
        Assert.Equal(1, context.Drain());
        Assert.Equal("send-boom", Assert.IsType<InvalidOperationException>(task.GetAwaiter().GetResult()).Message);
    }

    [Fact]
    public void Drain_RejectsNonUiThread_AndScopeRestoresThePreviousContext()
    {
        var previous = SynchronizationContext.Current;
        var scope = UiSynchronizationContext.Install();
        var context = scope.Context;
        Assert.Same(context, SynchronizationContext.Current);
        Assert.Equal(Environment.CurrentManagedThreadId, context.UiThreadId);

        var error = Task.Factory.StartNew(() =>
        {
            try { context.Drain(); return (Exception?)null; }
            catch (Exception ex) { return ex; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
        Assert.IsType<InvalidOperationException>(error);

        scope.Dispose();
        Assert.Same(previous, SynchronizationContext.Current);
    }

    [Fact]
    public void Install_BindsTheHostRoot_AndDetachesOnDispose()
    {
        var previous = SynchronizationContext.Current;
        var root = new StackPanel();
        var child = new Button { Label = "x" };
        root.Children.Add(child);
        var scope = UiSynchronizationContext.Install(root);
        Assert.Same(scope.Context, SynchronizationContext.Current);

        Exception? OffThreadWrite() => Task.Factory.StartNew(() =>
        {
            try { child.Label = "y"; return (Exception?)null; }
            catch (Exception ex) { return ex; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();

#if DEBUG
        // 绑定到根元素后，子元素的 SetValue 也受线程亲和守卫覆盖（沿父链查找）。
        Assert.IsType<UiThreadViolationException>(OffThreadWrite());
        Assert.Equal("x", child.Label);
#endif
        scope.Dispose();
        Assert.Same(previous, SynchronizationContext.Current);
        Assert.Null(OffThreadWrite()); // 解绑后回到「单线程是约定不是机制」的现行口径
        Assert.Equal("y", child.Label);
    }
}
