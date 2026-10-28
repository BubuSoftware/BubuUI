using System.Diagnostics;
using System.Runtime.Versioning;
using EclipseUI.Rendering;

namespace EclipseUI.Platform.Windows;

/// <summary>一帧完成后的统计（时间均为托管侧耗时，不含 GPU 异步完成）。</summary>
public sealed class HostFrameEventArgs : EventArgs
{
    public HostFrameEventArgs(int frame, double totalMilliseconds, Size logicalSize)
    {
        Frame = frame;
        TotalMilliseconds = totalMilliseconds;
        LogicalSize = logicalSize;
    }

    /// <summary>从 1 开始的帧序号。</summary>
    public int Frame { get; }

    /// <summary>布局 + display list + 提交的总耗时（毫秒）。</summary>
    public double TotalMilliseconds { get; }

    public Size LogicalSize { get; }
}

public sealed class WindowHostOptions
{
    public ElementTheme? Theme { get; init; }

    public ITextPainter? TextPainter { get; init; }

    /// <summary>Esc 关闭窗口（应用内可关掉，自己处理键）。</summary>
    public bool CloseOnEscape { get; init; } = true;

    /// <summary>主动限帧；0 表示交给 surface（vsync）或 GPU 队列。</summary>
    public int MaxFramesPerSecond { get; init; }
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
    private readonly WindowHostOptions _options;
    private readonly ElementPainter _painter;
    private bool _disposed;

    public WindowHost(
        Win32Window window,
        ISurfacePresenter presenter,
        Element root,
        WindowHostOptions? options = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _options = options ?? new WindowHostOptions();

        _painter = new ElementPainter(_options.Theme, _options.TextPainter);
        Input = new InputRouter(root);

        _window.Resized += OnResized;
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

    /// <summary>本次 Run 已渲染的帧数。</summary>
    public int Frames { get; private set; }

    /// <summary>最近一帧的耗时（毫秒）。</summary>
    public double LastFrameMilliseconds { get; private set; }

    /// <summary>指数滑动平均帧时间（毫秒）。</summary>
    public double AverageFrameMilliseconds { get; private set; }

    /// <summary>窗口即将关闭。</summary>
    public bool IsExiting { get; private set; }

    public event EventHandler<HostFrameEventArgs>? FrameRendered;

    /// <summary>
    /// 运行消息 + 帧循环。返回已渲染帧数；窗口关闭或达到 <paramref name="maxFrames"/> 时返回。
    /// </summary>
    public int Run(int? maxFrames = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _window.Show();
        SyncPresenterSize();

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

            _root.Measure(size);
            _root.Arrange(new Rect(Point.Zero, size));

            var list = _painter.Build(_root, size, _window.ScaleFactor, Input.Focus.Focused);
            _presenter.Render(list);

            var frames = ++Frames;
            LastFrameMilliseconds = Elapsed(start);
            AverageFrameMilliseconds = frames == 1
                ? LastFrameMilliseconds
                : AverageFrameMilliseconds + (LastFrameMilliseconds - AverageFrameMilliseconds) * 0.05;

            FrameRendered?.Invoke(this, new HostFrameEventArgs(frames, LastFrameMilliseconds, size));

            if (frameInterval > 0)
            {
                // Windows 的 sleep 粒度较大（~15ms），剩余部分用自旋补足。
                var remaining = frameInterval - clock.Elapsed.TotalMilliseconds;
                if (remaining > 1.0) Thread.Sleep((int)remaining);
                while (clock.Elapsed.TotalMilliseconds < frameInterval) Thread.SpinWait(64);
            }
            clock.Restart();

            if (maxFrames is { } limit && frames >= limit) break;
        }

        return Frames;
    }

    /// <summary>下一次循环重新同步表面尺寸（例如窗口在 Show 之前创建）。</summary>
    public void SyncPresenterSize()
        => _presenter.Resize(Math.Max(_window.ClientWidth, 1), Math.Max(_window.ClientHeight, 1));

    private void OnResized(object? sender, Win32ResizeEventArgs e)
        => _presenter.Resize(Math.Max(e.Width, 1), Math.Max(e.Height, 1));

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
        _window.PointerMoved -= OnPointerMoved;
        _window.PointerPressed -= OnPointerPressed;
        _window.PointerReleased -= OnPointerReleased;
        _window.KeyDown -= OnKeyDown;
        _window.Closing -= OnClosing;
    }
}
