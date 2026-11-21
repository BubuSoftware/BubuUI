using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EclipseUI.Platform.Windows;

/// <summary>窗口尺寸变化（设备像素 + 缩放）。</summary>
public sealed class Win32ResizeEventArgs : EventArgs
{
    public Win32ResizeEventArgs(int width, int height, double scale)
    {
        Width = width;
        Height = height;
        Scale = scale;
    }

    /// <summary>客户区宽度（设备像素）。</summary>
    public int Width { get; }

    /// <summary>客户区高度（设备像素）。</summary>
    public int Height { get; }

    /// <summary>缩放因子（DPI / 96）。</summary>
    public double Scale { get; }

    /// <summary>客户区尺寸（设备无关单位）。</summary>
    public Size LogicalSize => new(Width / Scale, Height / Scale);
}

public sealed class Win32PointerEventArgs : EventArgs
{
    public Win32PointerEventArgs(Point position, Point logicalPosition, bool isRightButton)
    {
        Position = position;
        LogicalPosition = logicalPosition;
        IsRightButton = isRightButton;
    }

    /// <summary>客户区坐标（设备像素）。</summary>
    public Point Position { get; }

    /// <summary>客户区坐标（设备无关单位，供元素命中测试使用）。</summary>
    public Point LogicalPosition { get; }

    public bool IsRightButton { get; }
}

public sealed class Win32WheelEventArgs : EventArgs
{
    public Win32WheelEventArgs(Point position, Point logicalPosition, double delta)
    {
        Position = position;
        LogicalPosition = logicalPosition;
        Delta = delta;
    }

    public Point Position { get; }

    public Point LogicalPosition { get; }

    /// <summary>滚动量，正数表示向上（1 格 = 120）。</summary>
    public double Delta { get; }
}

public sealed class Win32KeyEventArgs : EventArgs
{
    public Win32KeyEventArgs(Key key, int virtualKey, bool shift, bool control, bool alt, bool repeat)
    {
        Key = key;
        VirtualKey = virtualKey;
        Shift = shift;
        Control = control;
        Alt = alt;
        IsRepeat = repeat;
    }

    public Key Key { get; }

    /// <summary>原始 Win32 虚拟键码，用于尚未建模的按键。</summary>
    public int VirtualKey { get; }

    public bool Shift { get; }

    public bool Control { get; }

    public bool Alt { get; }

    public bool IsRepeat { get; }
}

public sealed class Win32TextEventArgs : EventArgs
{
    public Win32TextEventArgs(char character) : this(character.ToString()) { }
    public Win32TextEventArgs(string text) => Text = text;
    public string Text { get; }
    public char Character => Text[0];
}

/// <summary>
/// 纯 Win32 窗口宿主：注册窗口类、消息循环、DPI 感知、指针与键盘事件。
/// 不做任何 UI 假设，渲染与输入路由由上层（<c>RenderSpike</c> 或未来的 Application）决定。
/// 所有坐标为设备像素；<see cref="ScaleFactor"/> 用于换算到设备无关单位。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32Window : IDisposable
{
    private const string DefaultClassName = "EclipseUI.Win32Window";

    private static readonly object ClassLock = new();
    private static readonly Dictionary<nint, Win32Window> Windows = new();
    private static Win32Native.WndProc? StaticWndProc;
    private static ushort ClassAtom;
    private static int DpiAwarenessSet;
    private static int TimerPeriodUsers;

    private readonly nint _hwnd;
    private bool _disposed;
    private bool _trackingLeave;
    private bool _initialized;
    private const int SizeMoveTimerId = 0xE100;
    private nint _sizeMoveTimer;

    private readonly bool _quitOnDestroy;

    public Win32Window(string title, int width, int height, bool quitOnDestroy = true)
    {
        _quitOnDestroy = quitOnDestroy;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Win32Window requires Windows.");

        EnsureDpiAwareness();
        EnsureClassRegistered();
        EnsureTimerResolution();

        var style = Win32Native.WS_OVERLAPPEDWINDOW;
        var rect = new Win32Native.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        Win32Native.AdjustWindowRectExForDpi(ref rect, style, false, 0, 96);

        _hwnd = Win32Native.CreateWindowExW(
            0,
            DefaultClassName,
            title,
            style,
            Win32Native.CW_USEDEFAULT,
            Win32Native.CW_USEDEFAULT,
            rect.Width,
            rect.Height,
            nint.Zero,
            nint.Zero,
            Win32Native.GetModuleHandleW(null),
            nint.Zero);

        if (_hwnd == nint.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        lock (ClassLock)
        {
            Windows[_hwnd] = this;
        }

        _initialized = true;
        UpdateScale();
        RefreshClientSize();
    }

    /// <summary>指针/键盘事件是否启用；关闭后窗口仍可绘制（用于纯渲染 spike）。</summary>
    public bool InputEnabled { get; set; } = true;

    public nint Handle => _hwnd;

    public bool IsClosed { get; private set; }
    /// <summary>Windows is dispatching its nested move/size message loop on the UI thread.</summary>
    public bool IsInSizeMove { get; private set; }

    /// <summary>缩放因子（DPI / 96）。窗口创建时与 WM_DPICHANGED 时更新。</summary>
    public double ScaleFactor { get; private set; } = 1.0;

    public int ClientWidth { get; private set; }

    public int ClientHeight { get; private set; }

    public Size LogicalClientSize => new(ClientWidth / ScaleFactor, ClientHeight / ScaleFactor);

    public event EventHandler<Win32ResizeEventArgs>? Resized;

    public event EventHandler<Win32PointerEventArgs>? PointerMoved;

    public event EventHandler<Win32PointerEventArgs>? PointerPressed;
    public event EventHandler? PointerCaptureLost;

    public event EventHandler<Win32PointerEventArgs>? PointerReleased;

    public event EventHandler<Win32WheelEventArgs>? PointerWheel;

    public event EventHandler? PointerLeft;

    public event EventHandler? Focused;

    public event EventHandler? FocusLost;

    public event EventHandler<Win32KeyEventArgs>? KeyDown;

    public event EventHandler<Win32KeyEventArgs>? KeyUp;

    public event EventHandler<Win32TextEventArgs>? CharacterTyped;

    /// <summary>WM_PAINT：上层可选择立即重绘或仅在下一帧处理。</summary>
    public event EventHandler? PaintRequested;
    /// <summary>UI-thread frame opportunity while the normal caller's message pump is blocked by dragging.</summary>
    public event EventHandler? LiveResizeFrameRequested;

    /// <summary>WM_CLOSE 已被处理，窗口即将销毁。</summary>
    public event EventHandler? Closing;

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Win32Native.ShowWindow(_hwnd, Win32Native.SW_SHOWNORMAL);
        Win32Native.UpdateWindow(_hwnd);
        Win32Native.SetForegroundWindow(_hwnd);
        Win32Native.SetFocus(_hwnd);
        RefreshClientSize();

        if (!Win32Native.IsWindowVisible(_hwnd))
            throw new InvalidOperationException($"ShowWindow failed: {Marshal.GetLastWin32Error()}");
    }

    public void SetTitle(string title) => Win32Native.SetWindowTextW(_hwnd, title);

    /// <summary>请求关闭窗口（走正常 WM_CLOSE → WM_DESTROY → WM_QUIT 流程）。</summary>
    public void Close()
    {
        if (_disposed || IsClosed) return;
        Win32Native.PostMessageW(_hwnd, Win32Native.WM_CLOSE, nint.Zero, nint.Zero);
    }

    /// <summary>
    /// 向客户区投递一次完整的左键按下/释放（设备像素坐标）。
    /// 走与真实鼠标完全相同的消息路径，供自动化冒烟测试使用。
    /// </summary>
    public void PostPointerClick(Point position, bool rightButton = false)
    {
        PostPointerPress(position, rightButton);
        PostPointerRelease(position, rightButton);
    }

    /// <summary>Posts a held button press in device pixels, for native drag/hold checks.</summary>
    public void PostPointerPress(Point position, bool rightButton = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Win32Native.PostMessageW(_hwnd,
            (uint)(rightButton ? Win32Native.WM_RBUTTONDOWN : Win32Native.WM_LBUTTONDOWN),
            rightButton ? 2 : 1, PointerLParam(position));
    }

    public void PostPointerMove(Point position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Win32Native.PostMessageW(_hwnd, Win32Native.WM_MOUSEMOVE, 0, PointerLParam(position));
    }

    public void PostPointerRelease(Point position, bool rightButton = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Win32Native.PostMessageW(_hwnd,
            (uint)(rightButton ? Win32Native.WM_RBUTTONUP : Win32Native.WM_LBUTTONUP),
            0, PointerLParam(position));
    }

    private static nint PointerLParam(Point position) =>
        (nint)(((int)Math.Round(position.Y) << 16) | ((int)Math.Round(position.X) & 0xFFFF));

    /// <summary>按设备无关尺寸请求客户区大小（内部换算 DPI 与边框），用于 resize 冒烟测试。</summary>
    public void SetClientSize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var style = Win32Native.WS_OVERLAPPEDWINDOW;
        var rect = new Win32Native.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
        Win32Native.AdjustWindowRectExForDpi(ref rect, style, false, 0, (uint)Math.Round(96 * ScaleFactor));
        Win32Native.SetWindowPos(
            _hwnd, nint.Zero,
            0, 0,
            rect.Width, rect.Height,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>客户区在屏幕坐标系中的矩形（设备像素），用于屏幕截图与定位弹层。</summary>
    public Rect ClientBoundsOnScreen()
    {
        var origin = new Win32Native.POINT(0, 0);
        Win32Native.ClientToScreen(_hwnd, ref origin);
        return new Rect(origin.X, origin.Y, ClientWidth, ClientHeight);
    }

    /// <summary>请求下一次 <see cref="PaintRequested"/>。</summary>
    public void Invalidate() => Win32Native.InvalidateRect(_hwnd, nint.Zero, false);

    /// <summary>
    /// 抽取并分发队列中的消息。返回 false 表示窗口已关闭。
    /// 非阻塞；调用方自行控制帧节奏（surface Present 通常已按 vsync 限速）。
    /// </summary>
    public bool PumpMessages()
    {
        while (Win32Native.PeekMessageW(out var message, nint.Zero, 0, 0, Win32Native.PM_REMOVE))
        {
            if (message.message == 0x0012 /* WM_QUIT */)
            {
                IsClosed = true;
                return false;
            }

            Win32Native.TranslateMessage(ref message);
            Win32Native.DispatchMessageW(ref message);
        }

        return !IsClosed;
    }

    /// <summary>阻塞直到下一条消息；返回 false 表示窗口已关闭。</summary>
    public bool WaitForMessage()
    {
        var result = Win32Native.GetMessageW(out var message, nint.Zero, 0, 0);
        if (result == 0 || result == -1)
        {
            IsClosed = true;
            return false;
        }

        Win32Native.TranslateMessage(ref message);
        Win32Native.DispatchMessageW(ref message);
        return !IsClosed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EndSizeMove();

        if (!IsClosed)
        {
            IsClosed = true;
            Win32Native.DestroyWindow(_hwnd);
        }

        ReleaseTimerResolution();

        lock (ClassLock)
        {
            Windows.Remove(_hwnd);
        }
    }

    private void RefreshClientSize()
    {
        if (!Win32Native.GetClientRect(_hwnd, out var rect)) return;
        ClientWidth = Math.Max(rect.Width, 0);
        ClientHeight = Math.Max(rect.Height, 0);
    }

    private void EndSizeMove()
    {
        if (_sizeMoveTimer != 0) Win32Native.KillTimer(_hwnd, _sizeMoveTimer);
        _sizeMoveTimer = 0;
        IsInSizeMove = false;
    }

    private void UpdateScale()
    {
        var dpi = Win32Native.GetDpiForWindow(_hwnd);
        ScaleFactor = dpi == 0 ? 1.0 : dpi / 96.0;
    }

    private static void EnsureDpiAwareness()
    {
        if (Interlocked.Exchange(ref DpiAwarenessSet, 1) == 1) return;
        // 进程级设置；若宿主已设置过则忽略失败。
        Win32Native.SetProcessDpiAwarenessContext(Win32Native.DpiAwarenessPerMonitorV2);
    }

    /// <summary>
    /// 窗口存续期间把系统定时器分辨率提到 1ms：<see cref="Thread.Sleep"/> 的实际粒度
    /// 等于系统定时器分辨率（默认 ~15.6ms），软限速器会整量子超调。引用计数保证
    /// 多窗口场景 begin/end 成对；失败（如宿主已锁定分辨率）只影响精度，不影响正确性。
    /// </summary>
    private static void EnsureTimerResolution()
    {
        if (Interlocked.Increment(ref TimerPeriodUsers) != 1) return;
        if (Win32Native.timeBeginPeriod(Win32Native.TimerPeriodMilliseconds) == Win32Native.TimerErrorNoCanDo)
            Interlocked.Decrement(ref TimerPeriodUsers); // 请求被拒：撤销登记，后续窗口会重试
    }

    private static void ReleaseTimerResolution()
    {
        var remaining = Interlocked.Decrement(ref TimerPeriodUsers);
        if (remaining < 0)
        {
            Interlocked.Increment(ref TimerPeriodUsers); // 无登记可释放：撤销递减，保持计数自洽
            return;
        }

        if (remaining == 0)
            Win32Native.timeEndPeriod(Win32Native.TimerPeriodMilliseconds);
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (ClassAtom != 0) return;

            StaticWndProc = StaticWindowProc;
            nint largeIcon = 0, smallIcon = 0;
            if (Environment.ProcessPath is { } executable)
                Win32Native.ExtractIconExW(executable, 0, out largeIcon, out smallIcon, 1);

            var windowClass = new Win32Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Win32Native.WNDCLASSEX>(),
                style = Win32Native.CS_HREDRAW | Win32Native.CS_VREDRAW,
                lpfnWndProc = StaticWndProc,
                hInstance = Win32Native.GetModuleHandleW(null),
                hIcon = largeIcon,
                hIconSm = smallIcon,
                hCursor = Win32Native.LoadCursorW(nint.Zero, Win32Native.IDC_ARROW),
                lpszClassName = DefaultClassName,
            };

            ClassAtom = Win32Native.RegisterClassExW(ref windowClass);
            if (ClassAtom == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (largeIcon != 0) Win32Native.DestroyIcon(largeIcon);
                if (smallIcon != 0) Win32Native.DestroyIcon(smallIcon);
                throw new InvalidOperationException($"RegisterClassEx failed: {error}");
            }
            // The registered class and its icons remain alive until the process exits.
        }
    }

    private static nint StaticWindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        Win32Window? window;
        lock (ClassLock)
        {
            if (!Windows.TryGetValue(hwnd, out window)) window = null;
        }

        if (window is not null && window._initialized)
            return window.WindowProc(message, wParam, lParam);

        return Win32Native.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private nint WindowProc(uint message, nint wParam, nint lParam)
    {
        // Keep the native candidate UI, while the focused TextBox paints composition text.
        if (message == 0x0281) return Win32Native.DefWindowProcW(_hwnd, message, wParam, (nint)(lParam.ToInt64() & ~0x80000000L));
        if (ProcessIme(message, lParam)) return 0;
        switch (message)
        {
            case Win32Native.WM_ENTERSIZEMOVE:
                if (!IsInSizeMove)
                {
                    IsInSizeMove = true;
                    _sizeMoveTimer = Win32Native.SetTimer(_hwnd, SizeMoveTimerId, 16, 0);
                }
                return 0;

            case Win32Native.WM_EXITSIZEMOVE:
                EndSizeMove();
                PaintRequested?.Invoke(this, EventArgs.Empty);
                return 0;

            case Win32Native.WM_TIMER when IsInSizeMove && _sizeMoveTimer != 0 && wParam == _sizeMoveTimer:
                LiveResizeFrameRequested?.Invoke(this, EventArgs.Empty);
                return 0;

            case Win32Native.WM_SIZE:
                {
                    var previousWidth = ClientWidth;
                    var previousHeight = ClientHeight;
                    var width = LowWord(lParam);
                    var height = HighWord(lParam);
                    ClientWidth = Math.Max(width, 0);
                    ClientHeight = Math.Max(height, 0);

                    if (ClientWidth != previousWidth || ClientHeight != previousHeight)
                        Resized?.Invoke(this, new Win32ResizeEventArgs(ClientWidth, ClientHeight, ScaleFactor));

                    return 0;
                }

            case Win32Native.WM_DPICHANGED:
                {
                    var dpi = LowWord(wParam);
                    ScaleFactor = dpi / 96.0;

                    // lParam 指向系统建议的新窗口矩形。
                    var suggested = Marshal.PtrToStructure<Win32Native.RECT>(lParam);
                    Win32Native.SetWindowPos(
                        _hwnd, nint.Zero,
                        suggested.Left, suggested.Top,
                        suggested.Width, suggested.Height,
                        SWP_NOZORDER | SWP_NOACTIVATE);

                    RefreshClientSize();
                    Resized?.Invoke(this, new Win32ResizeEventArgs(ClientWidth, ClientHeight, ScaleFactor));
                    return 0;
                }

            case Win32Native.WM_ERASEBKGND:
                // 全部由 GPU 覆盖，避免闪烁。
                return 1;

            case Win32Native.WM_PAINT:
                {
                    var hdc = Win32Native.BeginPaint(_hwnd, out var paint);
                    try
                    {
                        if (PaintRequested is null)
                        {
                            // 没有处理器时用背景色填充，避免残留旧像素。
                            FillBackground(hdc, paint.rcPaint);
                        }
                        else
                        {
                            PaintRequested.Invoke(this, EventArgs.Empty);
                        }
                    }
                    finally
                    {
                        Win32Native.EndPaint(_hwnd, ref paint);
                    }
                    return 0;
                }

            case Win32Native.WM_MOUSEMOVE:
                {
                    if (InputEnabled)
                    {
                        var position = PointFromLParam(lParam);
                        PointerMoved?.Invoke(this, new Win32PointerEventArgs(position, ToLogical(position), false));

                        if (!_trackingLeave)
                        {
                            var track = new Win32Native.TRACKMOUSEEVENT
                            {
                                cbSize = (uint)Marshal.SizeOf<Win32Native.TRACKMOUSEEVENT>(),
                                dwFlags = Win32Native.TME_LEAVE,
                                hwndTrack = _hwnd,
                            };
                            _trackingLeave = Win32Native.TrackMouseEvent(ref track);
                        }
                    }
                    return 0;
                }

            case Win32Native.WM_MOUSELEAVE:
                _trackingLeave = false;
                if (InputEnabled) PointerLeft?.Invoke(this, EventArgs.Empty);
                return 0;

            case Win32Native.WM_LBUTTONDOWN:
            case Win32Native.WM_RBUTTONDOWN:
                {
                    if (InputEnabled)
                    {
                        Win32Native.SetFocus(_hwnd);
                        // 捕获指针：拖出窗口后仍能收到移动/释放，控件不会“卡在按下态”。
                        Win32Native.SetCapture(_hwnd);
                        var position = PointFromLParam(lParam);
                        PointerPressed?.Invoke(this, new Win32PointerEventArgs(
                            position, ToLogical(position), message == Win32Native.WM_RBUTTONDOWN));
                    }
                    return 0;
                }

            case Win32Native.WM_LBUTTONUP:
            case Win32Native.WM_RBUTTONUP:
                {
                    if (InputEnabled)
                    {
                        var position = PointFromLParam(lParam);
                        // ReleaseCapture synchronously sends WM_CAPTURECHANGED. Deliver the
                        // normal release first, so cancellation cannot suppress a valid click.
                        try { PointerReleased?.Invoke(this, new Win32PointerEventArgs(
                            position, ToLogical(position), message == Win32Native.WM_RBUTTONUP)); }
                        finally { Win32Native.ReleaseCapture(); }
                    }
                    return 0;
                }

            case 0x0215 /* WM_CAPTURECHANGED */:
                if (lParam != _hwnd) PointerCaptureLost?.Invoke(this, EventArgs.Empty);
                return 0;
            case 0x001F /* WM_CANCELMODE */:
                Win32Native.ReleaseCapture();
                PointerCaptureLost?.Invoke(this, EventArgs.Empty);
                return 0;

            case Win32Native.WM_MOUSEWHEEL:
                {
                    if (InputEnabled)
                    {
                        var delta = (short)HighWord(wParam);
                        // 屏幕坐标 → 客户区坐标
                        var screen = new Win32Native.POINT(LowWordSigned(lParam), HighWordSigned(lParam));
                        Win32Native.ScreenToClient(_hwnd, ref screen);
                        var position = new Point(screen.X, screen.Y);
                        PointerWheel?.Invoke(this, new Win32WheelEventArgs(position, ToLogical(position), delta));
                    }
                    return 0;
                }

            case Win32Native.WM_KEYDOWN:
            case 0x0104 /* WM_SYSKEYDOWN */:
                {
                    if (InputEnabled)
                    {
                        var repeat = (lParam & (1 << 30)) != 0;
                        KeyDown?.Invoke(this, CreateKeyArgs((int)wParam, repeat));
                    }
                    return 0;
                }

            case Win32Native.WM_KEYUP:
            case 0x0105 /* WM_SYSKEYUP */:
                {
                    if (InputEnabled) KeyUp?.Invoke(this, CreateKeyArgs((int)wParam, false));
                    return 0;
                }

            case Win32Native.WM_CHAR:
                {
                    if (InputEnabled)
                    {
                        var code = (int)wParam;
                        if (code is > 0 and < 0x10000) EmitCharacter((char)code);
                    }
                    return 0;
                }

            case Win32Native.WM_SETFOCUS:
                Focused?.Invoke(this, EventArgs.Empty);
                return 0;

            case Win32Native.WM_KILLFOCUS:
                FocusLost?.Invoke(this, EventArgs.Empty);
                return 0;

            case Win32Native.WM_CLOSE:
                Closing?.Invoke(this, EventArgs.Empty);
                IsClosed = true;
                Win32Native.DestroyWindow(_hwnd);
                return 0;

            case Win32Native.WM_DESTROY:
                EndSizeMove();
                IsClosed = true;
                lock (ClassLock) Windows.Remove(_hwnd);
                if (_quitOnDestroy) Win32Native.PostQuitMessage(0);
                return 0;
        }

        return Win32Native.DefWindowProcW(_hwnd, message, wParam, lParam);
    }

    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_NOMOVE = 0x0002;

    private Win32KeyEventArgs CreateKeyArgs(int virtualKey, bool repeat)
    {
        var shift = (Win32Native.GetKeyState(0x10) & 0x8000) != 0;
        var control = (Win32Native.GetKeyState(0x11) & 0x8000) != 0;
        var alt = (Win32Native.GetKeyState(0x12) & 0x8000) != 0;
        return new Win32KeyEventArgs(MapKey(virtualKey), virtualKey, shift, control, alt, repeat);
    }

    private Point ToLogical(Point position) => new(position.X / ScaleFactor, position.Y / ScaleFactor);

    private static void FillBackground(nint hdc, Win32Native.RECT rect)
    {
        var brush = Win32Native.CreateSolidBrush(0x00101010);
        if (brush == nint.Zero) return;
        try
        {
            Win32Native.FillRect(hdc, ref rect, brush);
        }
        finally
        {
            Win32Native.DeleteObject(brush);
        }
    }

    private static Point PointFromLParam(nint lParam)
        => new(LowWordSigned(lParam), HighWordSigned(lParam));

    private static int LowWord(nint value) => (int)(value.ToInt64() & 0xFFFF);

    private static int HighWord(nint value) => (int)((value.ToInt64() >> 16) & 0xFFFF);

    private static int LowWordSigned(nint value) => (short)(value.ToInt64() & 0xFFFF);

    private static int HighWordSigned(nint value) => (short)((value.ToInt64() >> 16) & 0xFFFF);

    /// <summary>Win32 虚拟键码 → 运行时 <see cref="Key"/>。未建模的键返回 <see cref="Key.None"/>。</summary>
    public static Key MapKey(int virtualKey) => virtualKey switch
    {
        0x08 => Key.Backspace,
        0x09 => Key.Tab,
        0x0D => Key.Enter,
        0x1B => Key.Escape,
        0x20 => Key.Space,
        0x25 => Key.Left,
        0x26 => Key.Up,
        0x27 => Key.Right,
        0x28 => Key.Down,
        0x2E => Key.Delete,
        0x21 => Key.PageUp,
        0x22 => Key.PageDown,
        0x23 => Key.End,
        0x24 => Key.Home,
        0x71 => Key.F2,
        >= 0x41 and <= 0x5A => (Key)virtualKey,
        _ => Key.None,
    };
}
