using System;

namespace EclipseUI;

/// <summary>无头布局宿主：不开窗口跑 measure/arrange 全链路（测试与工具链用）。</summary>
public sealed class HeadlessRoot : IDisposable
{
    private Element _content;
    private InputRouter _input;
    private readonly UiObject _uiRoot;
    private UiSynchronizationContext? _uiContext;
    private readonly List<UiScope> _uiScopes = new();
    private bool _disposed;

    public HeadlessRoot(UiObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.EnsureInitialized();
        _uiRoot = root;
        _content = root as Element ?? root.ContentElement
            ?? throw new InvalidOperationException("The UI root has no element content.");
        _input = new InputRouter(_content);
        try
        {
            root.AttachToHost(this);
            SyncContent();
        }
        catch (Exception error)
        {
            try { root.DetachFromHost(this); }
            catch (Exception cleanup)
            {
                throw new AggregateException("Host attachment and cleanup failed.", error, cleanup);
            }
            throw;
        }
    }

    public Size AvailableSize { get; set; } = new Size(1280, 800);

    public Element Content => SyncContent();

    /// <summary>指针/键盘路由（与真实窗口宿主共用）。</summary>
    public InputRouter Input
    {
        get { SyncContent(); return _input; }
    }

    public FocusManager Focus => Input.Focus;

    /// <summary>当前安装的 UI 同步上下文（未安装时为 null）。</summary>
    public UiSynchronizationContext? SynchronizationContext => _uiContext;

    /// <summary>
    /// 在当前线程安装单线程 UI 同步上下文，并绑定到宿主根元素（design/15 §15.14.2 装配点）。
    /// 无头宿主不开帧循环，排空由驱动者显式调用 <see cref="DrainPostedWork"/>；
    /// <see cref="UpdateLayout"/> 不会自动 drain，测试据此把 tick 边界钉成 §15.14.4 的 N / N+1。
    /// </summary>
    public UiScope InstallSynchronizationContext()
    {
        var scope = UiSynchronizationContext.Install(SyncContent());
        _uiContext = scope.Context;
        var result = new UiScope(this, scope);
        _uiScopes.Add(result);
        return result;
    }

    /// <summary>
    /// 显式帧循环 drain：在 UI 线程执行当批 Post 的续体；只要执行过回调，就按批
    /// 整体失效挂载子树一次（不是每回调一次）。返回执行的工作项数。
    /// </summary>
    public int DrainPostedWork()
    {
        ThrowIfDisposed();
        if (_uiContext is null)
            return 0;
        var ran = _uiContext.Drain();
        if (ran > 0)
        {
            _uiRoot.InvalidateSubtree();
        }
        return ran;
    }

    /// <summary>安装范围：退出时还原上下文并清掉宿主引用，避免污染后续测试。</summary>
    public sealed class UiScope : IDisposable
    {
        private readonly HeadlessRoot _host;
        private readonly UiSynchronizationContext.UiThreadScope _scope;
        private bool _disposed;

        internal UiScope(HeadlessRoot host, UiSynchronizationContext.UiThreadScope scope)
        {
            _host = host;
            _scope = scope;
        }

        public UiSynchronizationContext Context => _scope.Context;

        /// <summary>等价于宿主帧循环里的 drain 步骤。</summary>
        public int Drain()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _host.DrainPostedWork();
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!Context.IsUiThread)
                throw new UiThreadViolationException("HeadlessRoot.UiScope.Dispose", Context.UiThreadId,
                    Environment.CurrentManagedThreadId);
            _disposed = true;
            _host._uiScopes.Remove(this);
            _host._uiContext = _host._uiScopes.LastOrDefault()?.Context;
            _scope.Dispose();
        }
    }

    public void UpdateLayout()
    {
        // 卸载/摘除后焦点元素可能已离树：布局前修剪，避免 Tab 从游离元素起算。
        Focus.ClearFocusIfDetached();
        _uiRoot.ProcessInvalidations();
        var content = SyncContent();
        content.Measure(AvailableSize);
        content.Arrange(new Rect(Point.Zero, content.DesiredSize));
    }

    /// <summary>Advances the shared animation clock without implicitly running layout or posted work.</summary>
    public void AdvanceAnimations(TimeSpan elapsed)
    {
        ThrowIfDisposed();
        UiThreadGuard.AssertUiThread(_uiRoot, nameof(AdvanceAnimations));
        FrameAnimations.Advance(SyncContent(), elapsed);
    }

    /// <summary>滚轮输入（无头测试与 Win32 宿主共用同一 InputRouter 路径）。</summary>
    public void PointerWheel(Point point, double delta) => Input.PointerWheel(point, delta);

    // ---- 输入（M4 无头形态：与平台宿主共用同一 hit test / 路由路径）----
    // 捕获语义：按下即捕获命中元素，释放路由给捕获元素（拖出后释放不会丢状态，
    // 也不会误触其他控件）；释放完成后捕获解除。

    public Element? HitTest(Point point) => Input.HitTest(point);

    public void PointerPress(Point point) => Input.PointerPress(point);

    public void PointerRelease(Point point) => Input.PointerRelease(point);

    public void PointerMove(Point point) => Input.PointerMove(point);

    /// <summary>按键。Tab 由焦点系统拦截做遍历；其余 KeyDown 从焦点元素（或根）路由。</summary>
    public void KeyPress(Key key) => Input.KeyPress(key);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed || _uiRoot.IsDisposed, this);

    private Element SyncContent()
    {
        ThrowIfDisposed();
        for (var round = 0; round < 100; round++)
        {
            ThrowIfDisposed();
            var content = _uiRoot as Element ?? _uiRoot.ContentElement
                ?? throw new InvalidOperationException("The UI root has no element content.");
            if (ReferenceEquals(content, _content)) return _content;
            var previousInput = _input;
            _content = content;
            _input = new InputRouter(content);
            previousInput.Reset();
        }
        throw new InvalidOperationException("UI content replacement exceeded 100 rounds while resetting input.");
    }

    /// <summary>Ends the host attachment without disposing the caller-owned UI tree.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        UiThreadGuard.AssertUiThread(_uiRoot, "HeadlessRoot.Dispose");
        foreach (var scope in _uiScopes)
            if (!scope.Context.IsUiThread)
                throw new UiThreadViolationException("HeadlessRoot.Dispose", scope.Context.UiThreadId,
                    Environment.CurrentManagedThreadId);
        _disposed = true;
        List<Exception>? failures = null;
        try { _uiRoot.DetachFromHost(this); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        try { _input.Reset(); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        foreach (var scope in _uiScopes.ToArray().Reverse())
        {
            try { scope.Dispose(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures is not null) throw new AggregateException("Host cleanup failed.", failures);
    }
}
