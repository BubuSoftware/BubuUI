using System.Diagnostics;
using EclipseUI.Rendering;
using EclipseUI.Themes.Fluent;

namespace EclipseUI.Platform.Linux;

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
/// 窗口宿主：把 <see cref="ILinuxWindow"/> 的消息、<see cref="ISurfacePresenter"/> 的上屏
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
    private readonly ILinuxWindow _window;
    private readonly ISurfacePresenter _presenter;
    private Element _root;
    private InputRouter _input;
    private readonly UiObject _uiRoot;
    private readonly WindowHostOptions _options;
    private readonly ElementPainter _painter;
    private readonly ThemeBinding _themeBinding;
    private bool _disposed;
    private bool _running;
    private IClipboard? _previousClipboard;

    public WindowHost(
        ILinuxWindow window,
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

        _themeBinding = new ThemeBinding(_options.Theme ?? FluentTheme.System, _options.SystemTheme ?? window as ISystemThemeProvider);
        _painter = new ElementPainter(_themeBinding.Current, _options.TextPainter);
        _themeBinding.Update(_painter, root);
        _input = new InputRouter(root);
        _input.Focus.AlwaysShowFocusVisuals = _options.AlwaysShowFocusVisuals;

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

    public ILinuxWindow Window => _window;

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
        _themeBinding.Update(_painter, Root);
        _painter.ResetDamage(); // 当前帧可能已构建过：再保证下一帧整帧
    }

    /// <summary>Replace a palette or a follow-system source on the UI thread.</summary>
    public void SetTheme(IHostTheme theme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _themeBinding.SetSource(theme);
        _themeBinding.Update(_painter, Root);
    }

    public InputRouter Input
    {
        get { SyncRoot(); return _input; }
    }

    /// <summary>本次 Run 的循环次数（包含跳过上屏的空闲帧）。</summary>
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
            _running = false;
            RestoreClipboard();
            try { _uiRoot.DetachFromHost(this); }
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

        var frameInterval = _options.MaxFramesPerSecond > 0
            ? 1000.0 / _options.MaxFramesPerSecond
            : 0;
        var clock = Stopwatch.StartNew();
        var animationTime = Stopwatch.GetTimestamp();

        while (!_disposed && !_uiRoot.IsDisposed && _window.PumpMessages())
        {
            if (_disposed || _uiRoot.IsDisposed || _window.IsClosed || IsExiting) break;
            _themeBinding.Update(_painter, Root);

            // tick 顺序：PumpMessages → Drain →（自动失效）→ ProcessInvalidations → 布局 → present。
            // drain 按快照批执行，回调里新 Post 的续体下一 tick 再跑；只要跑过回调就按批
            // 整体失效当前 UI 子树一次，续体可能修改任意后代的普通 CLR 状态。
            // 放在最小化分支之前：最小化时也不能把后台续体饿死。
            if (context.Drain() > 0)
            {
                _uiRoot.InvalidateSubtree();
            }
            if (_disposed || _uiRoot.IsDisposed || _window.IsClosed || IsExiting) break;

            var size = _window.LogicalClientSize;
            if (size.IsEmpty)
            {
                // 最小化：没有可绘制的客户区，等消息但不要空转。
                Thread.Sleep(8);
                continue;
            }

            var start = Stopwatch.GetTimestamp();

            // 卸载/摘除后焦点元素可能已离树：布局前修剪（G4/K33）。
            Input.Focus.ClearFocusIfDetached();
            _uiRoot.ProcessInvalidations();
            if (_disposed || _uiRoot.IsDisposed) break;
            var root = SyncRoot();
            _painter.Prepare(root);
            var animationNow = Stopwatch.GetTimestamp();
            FrameAnimations.Advance(root, TimeSpan.FromSeconds((animationNow - animationTime) / (double)Stopwatch.Frequency));
            animationTime = animationNow;
            root.Measure(size);
            root.Arrange(new Rect(Point.Zero, size));

            UpdateTextInput();
            var frame = _painter.BuildFrame(root, size, _window.ScaleFactor);
            var presented = _options.AlwaysRender || !frame.Damage.IsEmpty;
            if (presented)
            {
                _presenter.Render(frame.List);
                RenderedFrames++;
                LastDamage = frame.Damage;
                LastFrameMilliseconds = Elapsed(start);
                AverageFrameMilliseconds = RenderedFrames == 1
                    ? LastFrameMilliseconds
                    : AverageFrameMilliseconds + (LastFrameMilliseconds - AverageFrameMilliseconds) * 0.05;
            }
            else
            {
                SkippedFrames++;
            }

            Ticks++;
            FrameTicked?.Invoke(this, new HostFrameEventArgs(
                Ticks, presented, Elapsed(start), size, frame.Damage));

            if (frameInterval > 0)
            {
                var remaining = frameInterval - clock.Elapsed.TotalMilliseconds;
                if (remaining > 2.0) Thread.Sleep((int)(remaining - 1.0));
                while (clock.Elapsed.TotalMilliseconds < frameInterval) Thread.SpinWait(64);
            }
            else if (!presented && _options.IdleSleepMilliseconds > 0)
            {
                // 无脏区时跳过上屏，sleep 避免空转；消息仍在下一轮及时处理。
                Thread.Sleep(_options.IdleSleepMilliseconds);
            }
            clock.Restart();

            if (maxFrames is { } limit && Ticks >= limit) break;
        }

        return RenderedFrames;
    }

    /// <summary>下一次循环重新同步表面尺寸（例如窗口在 Show 之前创建）。</summary>
    public void SyncPresenterSize()
        => _presenter.Resize(Math.Max(_window.ClientWidth, 1), Math.Max(_window.ClientHeight, 1));

    private void OnResized(object? sender, LinuxResizeEventArgs e)
    {
        _presenter.Resize(Math.Max(e.Width, 1), Math.Max(e.Height, 1));
        _painter.ResetDamage();
    }

    private void OnPaintRequested(object? sender, EventArgs e) => _painter.ResetDamage();

    private void OnPointerMoved(object? sender, LinuxPointerEventArgs e)
        => Input.PointerMove(e.LogicalPosition);

    private void OnPointerPressed(object? sender, LinuxPointerEventArgs e)
        => Input.PointerPress(e.LogicalPosition, e.Modifiers, e.Button);

    private void OnPointerReleased(object? sender, LinuxPointerEventArgs e)
        => Input.PointerRelease(e.LogicalPosition);
    private void OnPointerCaptureLost(object? sender, EventArgs e) => Input.CancelPointerCapture();

    private void OnPointerWheel(object? sender, LinuxWheelEventArgs e)
        => Input.PointerWheel(e.LogicalPosition, e.Delta, e.Modifiers);

    private void OnKeyDown(object? sender, LinuxKeyEventArgs e)
    {
        if (e.Key == Key.None) return;
        using var scope = Input.Focus.EnterInput(FocusOrigin.Keyboard, Modifiers(e));
        Input.Focus.NotifyInput(FocusOrigin.Keyboard);
        if (e.Key == Key.Escape && Input.TryDismissPopup()) return;
        if (_options.CloseOnEscape && e.Key == Key.Escape)
        {
            _window.Close();
            return;
        }

        if (e.Key != Key.None)
            Input.KeyPress(e.Key, Modifiers(e), e.IsRepeat);
    }

    private static KeyModifiers Modifiers(LinuxKeyEventArgs e) => e.Modifiers;
    private void OnKeyUp(object? sender, LinuxKeyEventArgs e) => Input.KeyRelease(e.Key, Modifiers(e));
    private void OnCharacterTyped(object? sender, LinuxTextEventArgs e)
    {
        Input.TextInput(e.Text);
    }
    private void OnTextComposition(object? sender, TextCompositionEventArgs e)
        => Input.TextComposition(e.Text, e.Stage);

    private void UpdateTextInput()
    {
        var editor = Input.Focus.Focused as EclipseUI.Elements.TextBox;
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

    private void OnWindowFocusLost(object? sender, EventArgs e) => Input.Reset();

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
        try { _uiRoot.DetachFromHost(this); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        try { _input.Reset(); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        if (failures is not null) throw new AggregateException("Window host cleanup failed.", failures);
    }
}
