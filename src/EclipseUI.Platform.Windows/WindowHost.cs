using System.Diagnostics;
using System.Runtime.Versioning;
using EclipseUI.Rendering;

namespace EclipseUI.Platform.Windows;

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
    public ElementTheme? Theme { get; init; }

    public ITextPainter? TextPainter { get; init; }

    /// <summary>Esc 关闭窗口（应用内可关掉，自己处理键）。</summary>
    public bool CloseOnEscape { get; init; } = true;

    /// <summary>主动限帧；0 表示交给 surface（vsync）或 GPU 队列。</summary>
    public int MaxFramesPerSecond { get; init; }

    /// <summary>忽略脏区判定，每帧都上屏（性能对比 / 持续动画场景）。</summary>
    public bool AlwaysRender { get; init; }

    /// <summary>无脏区且未限帧时的休眠时长，避免空转占满 CPU。</summary>
    public int IdleSleepMilliseconds { get; init; } = 4;
}

/// <summary>
/// 窗口宿主：把 <see cref="Win32Window"/> 的消息、<see cref="ISurfacePresenter"/> 的上屏
/// 和元素树连成一条帧循环。
///
/// 职责边界：
/// <list type="bullet">
/// <item>窗口 / 表面由调用方创建并释放（宿主不接管所有权）。</item>
/// <item>布局：根元素 measure/arrange 到整个客户区（逻辑单位）。</item>
/// <item>绘制：<see cref="ElementPainter"/> 全量构建 display list（G2 阶段）。</item>
/// <item>输入：窗口事件 → <see cref="InputRouter"/>（与无头宿主同一路径）。</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowHost : IDisposable
{
    private readonly Win32Window _window;
    private readonly ISurfacePresenter _presenter;
    private readonly Element _root;
    private readonly Component? _rootComponent;
    private readonly WindowHostOptions _options;
    private readonly ElementPainter _painter;
    private bool _disposed;

    public WindowHost(
        Win32Window window,
        ISurfacePresenter presenter,
        Element root,
        WindowHostOptions? options = null,
        Component? rootComponent = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _rootComponent = rootComponent;
        _options = options ?? new WindowHostOptions();

        _painter = new ElementPainter(_options.Theme, _options.TextPainter);
        Input = new InputRouter(root);

        _window.Resized += OnResized;
        _window.PaintRequested += OnPaintRequested;
        _window.PointerMoved += OnPointerMoved;
        _window.PointerPressed += OnPointerPressed;
        _window.PointerReleased += OnPointerReleased;
        _window.KeyDown += OnKeyDown;
        _window.Closing += OnClosing;
    }

    public Win32Window Window => _window;

    public ISurfacePresenter Presenter => _presenter;

    public Element Root => _root;

    public ElementPainter Painter => _painter;

    public InputRouter Input { get; }

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

        while (_window.PumpMessages())
        {
            if (_window.IsClosed || IsExiting) break;

            var size = _window.LogicalClientSize;
            if (size.IsEmpty)
            {
                // 最小化：没有可绘制的客户区，等消息但不要空转。
                Thread.Sleep(8);
                continue;
            }

            var start = Stopwatch.GetTimestamp();

            _rootComponent?.ProcessInvalidations();
            _root.Measure(size);
            _root.Arrange(new Rect(Point.Zero, size));

            var frame = _painter.BuildFrame(_root, size, _window.ScaleFactor, Input.Focus.Focused);
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
                // Windows 的 sleep 粒度较大（~15ms），剩余部分用自旋补足。
                var remaining = frameInterval - clock.Elapsed.TotalMilliseconds;
                if (remaining > 1.0) Thread.Sleep((int)remaining);
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

    private void OnResized(object? sender, Win32ResizeEventArgs e)
    {
        _presenter.Resize(Math.Max(e.Width, 1), Math.Max(e.Height, 1));
        _painter.ResetDamage();
    }

    private void OnPaintRequested(object? sender, EventArgs e) => _painter.ResetDamage();

    private void OnPointerMoved(object? sender, Win32PointerEventArgs e)
        => Input.PointerMove(e.LogicalPosition);

    private void OnPointerPressed(object? sender, Win32PointerEventArgs e)
        => Input.PointerPress(e.LogicalPosition);

    private void OnPointerReleased(object? sender, Win32PointerEventArgs e)
        => Input.PointerRelease(e.LogicalPosition);

    private void OnKeyDown(object? sender, Win32KeyEventArgs e)
    {
        if (_options.CloseOnEscape && e.Key == Key.Escape)
        {
            _window.Close();
            return;
        }

        if (e.Key != Key.None)
            Input.KeyPress(e.Key);
    }

    private void OnClosing(object? sender, EventArgs e) => IsExiting = true;

    private static double Elapsed(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _window.Resized -= OnResized;
        _window.PaintRequested -= OnPaintRequested;
        _window.PointerMoved -= OnPointerMoved;
        _window.PointerPressed -= OnPointerPressed;
        _window.PointerReleased -= OnPointerReleased;
        _window.KeyDown -= OnKeyDown;
        _window.Closing -= OnClosing;
    }
}
