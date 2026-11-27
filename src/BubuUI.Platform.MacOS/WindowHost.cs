using System.Diagnostics;
using System.Runtime.ExceptionServices;
using BubuUI.Rendering;

namespace BubuUI.Platform.MacOS;

/// <summary>一帧循环的统计（时间均为托管侧耗时，不含 GPU 异步完成）。</summary>
public sealed class HostFrameEventArgs : EventArgs
{
    public HostFrameEventArgs(int iteration, bool presented, double totalMilliseconds, Size logicalSize, FrameDamage damage)
    {
        Iteration = iteration;
        Presented = presented;
        TotalMilliseconds = totalMilliseconds;
        LogicalSize = logicalSize;
        Damage = damage;
    }

    /// <summary>从 1 开始的循环序号（脏区为空时也会递增）。</summary>
    public int Iteration { get; }

    /// <summary>本帧是否真正上屏；false 表示无脏区已跳过。</summary>
    public bool Presented { get; }

    /// <summary>布局 + display list + 提交的总耗时（毫秒）。</summary>
    public double TotalMilliseconds { get; }

    public Size LogicalSize { get; }

    public FrameDamage Damage { get; }
}

public sealed class WindowHostOptions
{
    public IHostTheme? Theme { get; init; }
    public ISystemThemeProvider? SystemTheme { get; init; }

    public ITextPainter? TextPainter { get; init; }
    public ISurfaceFactory? SurfaceFactory { get; init; }
    public IPopupWindowFactory? PopupWindowFactory { get; init; }

    /// <summary>Esc 关闭窗口（应用内可关掉，自己处理键）。</summary>
    public bool CloseOnEscape { get; init; } = true;

    /// <summary>主动限帧；0 表示交给 surface（vsync）或 GPU 队列。</summary>
    public int MaxFramesPerSecond { get; init; }

    /// <summary>忽略脏区判定，每帧都上屏（性能对比 / 持续动画场景）。</summary>
    public bool AlwaysRender { get; init; }
    public bool AlwaysShowFocusVisuals { get; init; }

    /// <summary>无脏区且未限帧时的休眠时长，避免空转占满 CPU。</summary>
    public int IdleSleepMilliseconds { get; init; } = 4;
}

/// <summary>
/// 窗口宿主：把 <see cref="IMacOSWindow"/> 的消息、<see cref="ISurfacePresenter"/> 的上屏
/// 和元素树连成一条帧循环。
///
/// 职责边界：
/// <list type="bullet">
/// <item>窗口 / 表面由调用方创建并释放（宿主不接管所有权）。</item>
/// <item>布局：根元素 measure/arrange 到整个客户区（逻辑单位）。</item>
/// <item>绘制：<see cref="ElementPainter"/> 全量构建 display list（G2 阶段）。</item>
/// <item>输入：窗口事件 → <see cref="InputRouter"/>（与无头宿主同一路径）。</item>
/// <item>D101：实现 <see cref="IWindowHost"/>，<see cref="Application.SetTheme"/> 经此换主题。</item>
/// </list>
/// </summary>
public sealed class WindowHost : IDisposable, IWindowHost
{
    private readonly IMacOSWindow _window;
    private readonly ISurfacePresenter _presenter;
    private Element _root;
    private InputRouter _input;
    private readonly UiObject _uiRoot;
    private readonly WindowHostOptions _options;
    private readonly ElementPainter _painter;
    private readonly ThemeBinding _themeBinding;
    private readonly PopupWindowManager? _popups;
    private bool _disposed;
    private bool _running;
    private IClipboard? _previousClipboard;
    private UiSynchronizationContext? _activeContext;
    private bool _renderingFrame;
    private int _drainingWork;
    private bool _pendingResize;
    private bool _pendingFullFrame;
    private long _animationTime;
    private bool _animationPaused;
    private ExceptionDispatchInfo? _nativeFrameFailure;
    private readonly record struct FrameResult(bool Presented, Size Size, FrameDamage Damage, double Milliseconds);

    public WindowHost(
        IMacOSWindow window,
        ISurfacePresenter presenter,
        Element root,
        WindowHostOptions? options = null,
        UiObject? uiRoot = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _uiRoot = uiRoot ?? root.SchedulingRoot;
        _uiRoot.EnsureInitialized();
        root.EnsureInitialized();
        _options = options ?? new WindowHostOptions();

        _themeBinding = new ThemeBinding(_options.Theme ?? ElementTheme.Light, _options.SystemTheme ?? window as ISystemThemeProvider);
        _painter = new ElementPainter(_themeBinding.Current, _options.TextPainter);
        UpdateTheme(root);
        _input = new InputRouter(root);
        _input.Focus.AlwaysShowFocusVisuals = _options.AlwaysShowFocusVisuals;
        if (_options.SurfaceFactory is { } surfaces && _options.PopupWindowFactory is { } popups)
            _popups = new PopupWindowManager(popups, surfaces, _options.TextPainter);

        _window.Resized += OnResized;
        _window.PaintRequested += OnPaintRequested;
        _window.PointerMoved += OnPointerMoved;
        _window.PointerPressed += OnPointerPressed;
        _window.PointerCaptureLost += OnPointerCaptureLost;
        _window.PointerReleased += OnPointerReleased;
        _window.PointerWheel += OnPointerWheel;
        _window.KeyDown += OnKeyDown;
        _window.KeyUp += OnKeyUp;
        _window.CharacterTyped += OnCharacterTyped;
        _window.TextComposition += OnTextComposition;
        _window.FocusLost += OnWindowFocusLost;
        _window.Closing += OnClosing;
    }

    public IMacOSWindow Window => _window;

    public ISurfacePresenter Presenter => _presenter;

    public Element Root => SyncRoot();

    public ElementPainter Painter => _painter;

    /// <summary>
    /// 运行时切换主题（S-A）：换 token 表 → 段缓存与脏区历史整体失效，
    /// 下一帧整帧重绘上屏；皮肤默认尺寸发生变化时也使布局失效。
    /// </summary>
    public void SetTheme(ElementTheme theme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _themeBinding.SetSource(theme);
        UpdateTheme(Root);
        _painter.ResetDamage(); // 当前帧可能已构建过：再保证下一帧整帧
    }

    /// <summary>Replace a palette or a follow-system source on the UI thread.</summary>
    public void SetTheme(IHostTheme theme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _themeBinding.SetSource(theme);
        UpdateTheme(Root);
    }

    private void UpdateTheme(Element root)
    {
        _themeBinding.Update(_painter, root);
        // Follow the resolved palette, including custom and dynamically changing sources.
        _window.SetDarkTitleBar(!_themeBinding.Current.IsLight);
    }

    public InputRouter Input
    {
        get { SyncRoot(); return _input; }
    }

    /// <summary>本次 Run 的调度 tick 数，包含空闲帧及子窗口循环代为调度的帧。</summary>
    public int Ticks { get; private set; }

    /// <summary>本次 Run 实际 Present 的帧数。</summary>
    public int RenderedFrames { get; private set; }

    /// <summary>本次 Run 因无脏区而跳过的帧数。</summary>
    public int SkippedFrames { get; private set; }

    /// <summary>最近一次实际渲染的脏区。</summary>
    public FrameDamage LastDamage { get; private set; } = new();

    /// <summary>最近一次实际渲染的耗时（毫秒）；跳过帧不更新。</summary>
    public double LastFrameMilliseconds { get; private set; }

    /// <summary>指数滑动平均帧时间（毫秒，仅统计实际渲染的帧）。</summary>
    public double AverageFrameMilliseconds { get; private set; }

    /// <summary>窗口即将关闭。</summary>
    public bool IsExiting { get; private set; }

    /// <summary>Whether this host can no longer own an application window.</summary>
    public bool IsClosed => _disposed || _uiRoot.IsDisposed || _window.IsClosed || IsExiting;

    /// <summary>每个循环 tick 触发一次；<see cref="HostFrameEventArgs.Presented"/> 区分是否上屏。</summary>
    public event EventHandler<HostFrameEventArgs>? FrameTicked;

    /// <summary>
    /// 运行消息 + 帧循环。返回实际渲染帧数；窗口关闭或达到 <paramref name="maxFrames"/> 个 tick 时返回。
    /// </summary>
    public int Run(int? maxFrames = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) throw new InvalidOperationException("The window host is already running.");

        // design/15 §15.14.2：帧循环在 UI 线程装单线程同步上下文，退出时还原。
        // async void 事件处理器的 await 续体从此 Post 回来，而不是落线程池跨线程写状态。
        using var uiScope = UiSynchronizationContext.Install(SyncRoot());
        _previousClipboard = Clipboard.Current;
        Clipboard.Current = _window.Clipboard;
        _running = true;
        Exception? failure = null;
        try
        {
            _uiRoot.AttachToHost(this);
            return RunFrames(uiScope.Context, maxFrames);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            _activeContext = null;
            _running = false;
            RestoreClipboard();
            try
            {
                try { _popups?.Clear(); }
                finally { _uiRoot.DetachFromHost(this); }
            }
            catch (Exception cleanup) when (failure is not null)
            {
                throw new AggregateException("Window loop and detachment failed.", failure, cleanup);
            }
        }
    }

    private int RunFrames(UiSynchronizationContext context, int? maxFrames)
    {
        SyncRoot();
        _window.Show();
        SyncPresenterSize();
        _painter.ResetDamage();
        Ticks = 0;
        RenderedFrames = 0;
        SkippedFrames = 0;
        AverageFrameMilliseconds = 0;
        _nativeFrameFailure = null;
        _pendingFullFrame = true;
        _activeContext = context;

        var frameInterval = _options.MaxFramesPerSecond > 0
            ? 1000.0 / _options.MaxFramesPerSecond
            : 0;
        var clock = Stopwatch.StartNew();
        _animationTime = Stopwatch.GetTimestamp();
        _animationPaused = false;
        using var frames = new WindowFrameScheduler(() => TickFrame(context, maxFrames), context,
            Clipboard.Current, _options.MaxFramesPerSecond, _options.IdleSleepMilliseconds);

        while (!_disposed && !_uiRoot.IsDisposed && _window.PumpMessages())
        {
            _nativeFrameFailure?.Throw();
            if (_disposed || _uiRoot.IsDisposed || _window.IsClosed || IsExiting) break;
            frames.PumpOthers();
            frames.MarkFrame();
            var result = TickFrame(context, maxFrames);
            if (result is not { } frame)
            {
                if (maxFrames is { } reached && Ticks >= reached) break;
                frames.Wait(8);
                continue;
            }

            if (frameInterval > 0)
            {
                frames.Wait(frameInterval - clock.Elapsed.TotalMilliseconds);
            }
            else if (!frame.Presented && _options.IdleSleepMilliseconds > 0)
            {
                // 无脏区时跳过上屏，sleep 避免空转；消息仍在下一轮及时处理。
                frames.Wait(_options.IdleSleepMilliseconds);
            }
            clock.Restart();

            if (maxFrames is { } limit && Ticks >= limit) break;
        }

        _nativeFrameFailure?.Throw();
        return RenderedFrames;
    }

    private FrameResult? TickFrame(UiSynchronizationContext context, int? maxFrames)
    {
        _nativeFrameFailure?.Throw();
        if (maxFrames is { } limit && Ticks >= limit) return null;
        var result = RenderFrame(context);
        // A posted callback may have run a child loop which already advanced this host.
        if (maxFrames is { } reached && Ticks >= reached) return null;
        if (result is { } frame)
        {
            Ticks++;
            FrameTicked?.Invoke(this, new HostFrameEventArgs(
                Ticks, frame.Presented, frame.Milliseconds, frame.Size, frame.Damage));
        }
        _nativeFrameFailure?.Throw();
        return result;
    }

    private bool CanRender => _running && !_disposed && !_uiRoot.IsDisposed && !_window.IsClosed && !IsExiting;

    // The normal loop and nested Cocoa live-resize loop use the same UI-thread frame path.
    // Callbacks never resize the surface while a frame still owns its texture/resources.
    private FrameResult? RenderFrame(UiSynchronizationContext context, bool drainPostedWork = true)
    {
        if (_renderingFrame || !CanRender) return null;
        if (drainPostedWork || _drainingWork == 0)
        {
            _drainingWork++;
            try { if (context.Drain() > 0 && CanRender) _uiRoot.InvalidateSubtree(); }
            finally { _drainingWork--; }
        }
        if (!CanRender) return null;
        _renderingFrame = true;
        FrameResult? result = null;
        try
        {
            // A layout callback or Present can synchronously dispatch another resize.
            // Coalesce it and retry with the latest size after the current frame unwinds.
            for (var attempt = 0; attempt < 3 && CanRender; attempt++)
            {
                _pendingResize = false;
                if (!CanRender) return result;
                UpdateTheme(Root);
                Input.Focus.ClearFocusIfDetached();
                _uiRoot.ProcessInvalidations();
                if (!CanRender) return result;
                var root = SyncRoot();
                _painter.Prepare(root);
                var size = _window.LogicalClientSize;
                if (attempt == 0)
                {
                    var now = Stopwatch.GetTimestamp();
                    FrameAnimations.SetPaused(root, size.IsEmpty);
                    FrameAnimations.Advance(root, TimeSpan.FromSeconds((now - _animationTime) / (double)Stopwatch.Frequency),
                        !size.IsEmpty && !_animationPaused);
                    _animationPaused = size.IsEmpty;
                    _animationTime = now;
                }
                var width = _window.ClientWidth;
                var height = _window.ClientHeight;
                var scale = _window.ScaleFactor;
                if (size.IsEmpty || !CanRender) return result;
                var start = Stopwatch.GetTimestamp();
                _popups?.PrepareLayout(root, Input);
                root.Measure(size);
                root.Arrange(new Rect(Point.Zero, size));
                if (!CanRender) return result;
                if (width != _window.ClientWidth || height != _window.ClientHeight || scale != _window.ScaleFactor) continue;
                _pendingResize = false; // Layout already used the latest native size.
                _presenter.Resize(width, height);
                if (_pendingResize) continue;
                if (_pendingFullFrame) { _painter.ResetDamage(); _pendingFullFrame = false; }
                _popups?.Render(root, Input, _painter.Theme, _options.AlwaysRender);
                UpdateTextInput();
                if (!CanRender) return result;
                var frame = _painter.BuildFrame(root, size, scale);
                if (_pendingResize) continue;
                var presented = _options.AlwaysRender || !frame.Damage.IsEmpty;
                if (presented)
                {
                    _presenter.Render(frame, scale);
                    RenderedFrames++;
                    LastDamage = frame.Damage;
                    LastFrameMilliseconds = Elapsed(start);
                    AverageFrameMilliseconds = RenderedFrames == 1 ? LastFrameMilliseconds
                        : AverageFrameMilliseconds + (LastFrameMilliseconds - AverageFrameMilliseconds) * 0.05;
                }
                else SkippedFrames++;
                result = new(presented, size, frame.Damage, Elapsed(start));
                if (!_pendingResize) return result;
            }
            return result;
        }
        finally { _renderingFrame = false; }
    }

    private void RenderNativeFrame()
    {
        if (_activeContext is null || _renderingFrame || !CanRender || _nativeFrameFailure is not null) return;
        // Never let managed layout/render exceptions cross the reverse P/Invoke SDL callback.
        // The outer managed loop rethrows with the original stack and performs normal cleanup.
        var previous = SynchronizationContext.Current;
        var previousClipboard = Clipboard.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(_activeContext);
            Clipboard.Current = _window.Clipboard;
            RenderFrame(_activeContext, drainPostedWork: false);
        }
        catch (Exception error) { _nativeFrameFailure = ExceptionDispatchInfo.Capture(error); }
        finally
        {
            Clipboard.Current = previousClipboard;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>下一次循环重新同步表面尺寸（例如窗口在 Show 之前创建）。</summary>
    public void SyncPresenterSize()
    {
        if (_renderingFrame) { _pendingResize = _pendingFullFrame = true; return; }
        _presenter.Resize(Math.Max(_window.ClientWidth, 1), Math.Max(_window.ClientHeight, 1));
    }

    private void OnResized(object? sender, MacOSResizeEventArgs e)
    {
        _pendingResize = _pendingFullFrame = true;
        RenderNativeFrame();
    }

    private void OnPaintRequested(object? sender, EventArgs e)
    {
        _pendingFullFrame = true;
        RenderNativeFrame();
    }

    private void OnPointerMoved(object? sender, MacOSPointerEventArgs e)
        => Input.PointerMove(e.LogicalPosition);

    private void OnPointerPressed(object? sender, MacOSPointerEventArgs e)
        => Input.PointerPress(e.LogicalPosition, e.Modifiers, e.Button);

    private void OnPointerReleased(object? sender, MacOSPointerEventArgs e)
        => Input.PointerRelease(e.LogicalPosition);
    private void OnPointerCaptureLost(object? sender, EventArgs e) => Input.CancelPointerCapture();

    private void OnPointerWheel(object? sender, MacOSWheelEventArgs e)
        => Input.PointerWheel(e.LogicalPosition, e.Delta, e.Modifiers);

    private void OnKeyDown(object? sender, MacOSKeyEventArgs e)
    {
        if (e.Key == Key.None) return;
        using var scope = Input.Focus.EnterInput(FocusOrigin.Keyboard, Modifiers(e));
        Input.Focus.NotifyInput(FocusOrigin.Keyboard);
        if (e.Key == Key.Escape && Input.TryDismissPopup(e.IsRepeat)) return;
        if (_options.CloseOnEscape && e.Key == Key.Escape)
        {
            _window.Close();
            return;
        }

        if (e.Key != Key.None)
            Input.KeyPress(e.Key, Modifiers(e), e.IsRepeat);
    }

    private static KeyModifiers Modifiers(MacOSKeyEventArgs e) => e.Modifiers;
    private void OnKeyUp(object? sender, MacOSKeyEventArgs e) => Input.KeyRelease(e.Key, Modifiers(e));
    private void OnCharacterTyped(object? sender, MacOSTextEventArgs e)
    {
        Input.TextInput(e.Text);
    }
    private void OnTextComposition(object? sender, TextCompositionEventArgs e)
        => Input.TextComposition(e.Text, e.Stage);

    private void UpdateTextInput()
    {
        var editor = Input.Focus.Focused as BubuUI.Elements.TextBox;
        if (_popups?.OwnsEditor(editor) == true) editor = null;
        _window.SetTextInputEnabled(editor is { IsReadOnly: false });
        if (editor is null) return;
        var caret = editor.CaretRect;
        var zero = editor.PointFromRoot(Point.Zero);
        _window.SetImeCaret(new(caret.X - zero.X, caret.Y - zero.Y, caret.Width, caret.Height));
    }

    private void RestoreClipboard()
    {
        if (_previousClipboard is null) return;
        if (ReferenceEquals(Clipboard.Current, _window.Clipboard)) Clipboard.Current = _previousClipboard;
        _previousClipboard = null;
    }

    private void OnWindowFocusLost(object? sender, EventArgs e)
    {
        if (_popups?.HandleOwnerFocusLost(Input) == true) return;
        Input.Reset();
    }

    private void OnClosing(object? sender, EventArgs e) => IsExiting = true;

    private static double Elapsed(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    private Element SyncRoot()
    {
        ObjectDisposedException.ThrowIf(_disposed || _uiRoot.IsDisposed, this);
        for (var round = 0; round < 100; round++)
        {
            ObjectDisposedException.ThrowIf(_disposed || _uiRoot.IsDisposed, this);
            var root = _uiRoot as Element ?? _uiRoot.ContentElement
                ?? throw new InvalidOperationException("The UI root has no element content.");
            if (ReferenceEquals(root, _root)) return _root;
            var previousInput = _input;
            _root = root;
            _input = new InputRouter(root);
            _input.Focus.AlwaysShowFocusVisuals = _options.AlwaysShowFocusVisuals;
            _painter.ResetSegments();
            _painter.ResetDamage();
            previousInput.Reset();
        }
        throw new InvalidOperationException("UI content replacement exceeded 100 rounds while resetting input.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        UiThreadGuard.AssertUiThread(_uiRoot, "WindowHost.Dispose");
        _disposed = true;
        RestoreClipboard();

        _window.Resized -= OnResized;
        _window.PaintRequested -= OnPaintRequested;
        _window.PointerMoved -= OnPointerMoved;
        _window.PointerPressed -= OnPointerPressed;
        _window.PointerCaptureLost -= OnPointerCaptureLost;
        _window.PointerReleased -= OnPointerReleased;
        _window.PointerWheel -= OnPointerWheel;
        _window.KeyDown -= OnKeyDown;
        _window.KeyUp -= OnKeyUp;
        _window.CharacterTyped -= OnCharacterTyped;
        _window.TextComposition -= OnTextComposition;
        _window.FocusLost -= OnWindowFocusLost;
        _window.Closing -= OnClosing;
        List<Exception>? failures = null;
        try { _popups?.Dispose(); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        try { _uiRoot.DetachFromHost(this); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        try { _input.Reset(); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        if (failures is not null) throw new AggregateException("Window host cleanup failed.", failures);
    }
}
