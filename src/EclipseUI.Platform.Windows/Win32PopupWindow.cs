using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EclipseUI.Rendering;

namespace EclipseUI.Platform.Windows;

/// <summary>Creates borderless tool windows owned by the application's Win32 window.</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32PopupWindowFactory(Win32Window owner) : IPopupWindowFactory
{
    private readonly Win32Window _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public bool IsOwnerFocused => !_owner.IsClosed && Win32Native.GetFocus() == _owner.Handle;
    public bool SupportsModal => true;

    public Size AvailableSize
    {
        get
        {
            var work = WorkArea(Win32Native.MonitorFromWindow(_owner.Handle, 2));
            return new(work.Width / _owner.ScaleFactor, work.Height / _owner.ScaleFactor);
        }
    }

    public IPopupWindow Create(bool interactive, IPopupWindow? parent = null)
        => Create(interactive, false, parent);

    public IPopupWindow Create(bool interactive, bool modal, IPopupWindow? parent = null)
    {
        if (parent is not null && (parent is not Win32PopupWindow popup || !ReferenceEquals(popup.Owner, _owner)))
            throw new ArgumentException("The parent popup must belong to this desktop window.", nameof(parent));
        return new Win32PopupWindow(_owner, parent is Win32PopupWindow nativeParent ? nativeParent.Window : _owner,
            interactive || modal, modal);
    }

    internal static Win32Native.RECT WorkArea(nint monitor)
    {
        var info = new Win32Native.MONITORINFO { Size = (uint)Marshal.SizeOf<Win32Native.MONITORINFO>() };
        if (!Win32Native.GetMonitorInfoW(monitor, ref info))
            throw new InvalidOperationException($"GetMonitorInfo failed: {Marshal.GetLastWin32Error()}");
        return info.Work;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class Win32PopupWindow : IPopupWindow
{
    private readonly Win32Window _owner;
    private readonly Win32Window _nativeOwner;
    private bool _disposed;
    private bool _restoreFocusOnClose;
    private List<IDisposable>? _modalBlocks;
    private (int Left, int Top, int Right, int Bottom, int Diameter)? _shape;
    internal Win32Window Window { get; }
    internal Win32Window Owner => _owner;

    internal Win32PopupWindow(Win32Window owner, Win32Window nativeOwner, bool interactive, bool modal = false)
    {
        _owner = owner;
        _nativeOwner = nativeOwner;
        IsModal = modal;
        Window = new Win32Window(nativeOwner, interactive);
        Window.PointerMoved += (_, e) => PointerMoved?.Invoke(e.LogicalPosition);
        Window.PointerPressed += (_, e) => PointerPressed?.Invoke(e.LogicalPosition, Win32Window.CurrentModifiers,
            e.IsRightButton ? PointerButton.Right : PointerButton.Left);
        Window.PointerReleased += (_, e) => PointerReleased?.Invoke(e.LogicalPosition);
        Window.PointerLeft += (_, _) => PointerMoved?.Invoke(new(-1, -1));
        Window.PointerCaptureLost += (_, _) => PointerCaptureLost?.Invoke();
        Window.PointerWheel += (_, e) => PointerWheel?.Invoke(e.LogicalPosition, e.Delta, Win32Window.CurrentModifiers);
        Window.KeyDown += (_, e) => KeyDown?.Invoke(e.Key, Modifiers(e), e.IsRepeat);
        Window.KeyUp += (_, e) => KeyUp?.Invoke(e.Key, Modifiers(e));
        Window.CharacterTyped += (_, e) =>
        {
            if (!Win32Window.CurrentModifiers.HasFlag(KeyModifiers.Control)) TextInput?.Invoke(e.Text);
        };
        Window.TextComposition += (_, e) => TextComposition?.Invoke(e.Text, e.Stage);
        Window.FocusLost += (_, _) => FocusLost?.Invoke();
        Window.PaintRequested += (_, _) => PaintRequested?.Invoke();
        Window.Resized += (_, _) => PaintRequested?.Invoke();
        Window.Closing += (_, _) =>
        {
            _restoreFocusOnClose |= IsModal && Win32Native.GetFocus() == Window.Handle;
            Closed?.Invoke();
        };
    }

    public NativeWindowHandle SurfaceHandle => NativeWindowHandle.FromWin32(Window.Handle);
    public int ClientWidth => Window.ClientWidth;
    public int ClientHeight => Window.ClientHeight;
    public double ScaleFactor => Window.ScaleFactor;
    public bool IsClosed => _disposed || Window.IsClosed;
    public bool IsFocused => !IsClosed && Win32Native.GetFocus() == Window.Handle;
    public bool IsModal { get; }

    public Rect SetBounds(Rect bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var origin = _owner.ClientBoundsOnScreen().Location;
        var ownerScale = _owner.ScaleFactor;
        var requestedX = checked((int)Math.Round(origin.X + bounds.X * ownerScale));
        var requestedY = checked((int)Math.Round(origin.Y + bounds.Y * ownerScale));
        var x = requestedX;
        var y = requestedY;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var width = Math.Max(1, checked((int)Math.Ceiling(bounds.Width * ScaleFactor)));
            var height = Math.Max(1, checked((int)Math.Ceiling(bounds.Height * ScaleFactor)));
            var desired = new Win32Native.RECT { Left = requestedX, Top = requestedY, Right = requestedX + width, Bottom = requestedY + height };
            var work = Win32PopupWindowFactory.WorkArea(Win32Native.MonitorFromRect(in desired, 2));
            width = Math.Min(width, work.Width);
            height = Math.Min(height, work.Height);
            x = Math.Clamp(requestedX, work.Left, work.Right - width);
            y = Math.Clamp(requestedY, work.Top, work.Bottom - height);
            var previousScale = ScaleFactor;
            var current = Window.ClientBoundsOnScreen();
            if (current.X == x && current.Y == y && current.Width == width && current.Height == height) break;
            if (!Win32Native.SetWindowPos(Window.Handle, 0, x, y, width, height, 0x0004 | 0x0010))
                throw new InvalidOperationException($"SetWindowPos failed: {Marshal.GetLastWin32Error()}");
            if (previousScale == ScaleFactor) break;
        }
        return new((x - origin.X) / ownerScale, (y - origin.Y) / ownerScale,
            ClientWidth / ScaleFactor, ClientHeight / ScaleFactor);
    }

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsModal && _modalBlocks is null)
        {
            _modalBlocks = [];
            try
            {
                for (Win32Window? owner = _nativeOwner; owner is not null; owner = owner.Owner)
                    if (!owner.IsClosed) _modalBlocks.Add(owner.BlockModalInput());
            }
            catch (Exception error)
            {
                try { ReleaseModalBlocks(); }
                catch (Exception cleanup) { throw new AggregateException("Modal owner setup and cleanup failed.", error, cleanup); }
                throw;
            }
        }
        try
        {
            Window.Show();
            if (IsModal) Activate();
        }
        catch (Exception error)
        {
            try { ReleaseModalBlocks(); }
            catch (Exception cleanup) { throw new AggregateException("Modal window show and cleanup failed.", error, cleanup); }
            throw;
        }
    }

    public void Activate()
    {
        if (IsClosed) return;
        Win32Native.SetForegroundWindow(Window.Handle);
        Win32Native.SetFocus(Window.Handle);
    }

    private void ReleaseModalBlocks()
    {
        var blocks = _modalBlocks;
        _modalBlocks = null;
        if (blocks is null) return;
        List<Exception>? failures = null;
        foreach (var block in blocks.AsEnumerable().Reverse())
            try { block.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null) throw new AggregateException("Modal owner restoration failed.", failures);
    }
    public void SetShape(Rect contentBounds, double cornerRadius)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(contentBounds.X) || !double.IsFinite(contentBounds.Y) ||
            !double.IsFinite(contentBounds.Width) || !double.IsFinite(contentBounds.Height) ||
            contentBounds.Width < 0 || contentBounds.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(contentBounds));
        if (!double.IsFinite(cornerRadius) || cornerRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(cornerRadius));

        // Borderless popup window and client origins coincide. Clip the opaque swapchain
        // at the native window too, so rounded corners expose the actual windows below
        // and do not intercept pointer input outside the popup's visible outline.
        var left = Math.Clamp(checked((int)Math.Floor(contentBounds.Left * ScaleFactor)), 0, ClientWidth);
        var top = Math.Clamp(checked((int)Math.Floor(contentBounds.Top * ScaleFactor)), 0, ClientHeight);
        var right = Math.Clamp(checked((int)Math.Ceiling(contentBounds.Right * ScaleFactor)), left, ClientWidth);
        var bottom = Math.Clamp(checked((int)Math.Ceiling(contentBounds.Bottom * ScaleFactor)), top, ClientHeight);
        var radius = Math.Min(cornerRadius, Math.Min(contentBounds.Width, contentBounds.Height) / 2);
        var diameter = Math.Min(Math.Min(right - left, bottom - top), checked((int)Math.Ceiling(radius * ScaleFactor * 2)));
        var shape = (left, top, right, bottom, diameter);
        if (_shape == shape) return;
        var region = diameter > 0
            ? Win32Native.CreateRoundRectRgn(left, top, right, bottom, diameter, diameter)
            : Win32Native.CreateRectRgn(left, top, right, bottom);
        if (region == 0) throw new InvalidOperationException($"Creating the popup shape failed: {Marshal.GetLastWin32Error()}");
        try
        {
            if (Win32Native.SetWindowRgn(Window.Handle, region, true) == 0)
                throw new InvalidOperationException($"SetWindowRgn failed: {Marshal.GetLastWin32Error()}");
            // SetWindowRgn takes ownership on success; Windows deletes the old region
            // when the shape changes and releases the final region with the HWND.
            region = 0;
            _shape = shape;
        }
        finally { if (region != 0) Win32Native.DeleteObject(region); }
    }
    public void SetTextInputEnabled(bool enabled) => Window.SetTextInputEnabled(enabled);
    public void SetImeCaret(Rect caret) => Window.SetImeCaret(caret);

    private static KeyModifiers Modifiers(Win32KeyEventArgs e)
        => (e.Shift ? KeyModifiers.Shift : 0) | (e.Control ? KeyModifiers.Control : 0) | (e.Alt ? KeyModifiers.Alt : 0);

    public event Action<Point>? PointerMoved;
    public event Action<Point, KeyModifiers, PointerButton>? PointerPressed;
    public event Action<Point>? PointerReleased;
    public event Action? PointerCaptureLost;
    public event Action<Point, double, KeyModifiers>? PointerWheel;
    public event Action<Key, KeyModifiers, bool>? KeyDown;
    public event Action<Key, KeyModifiers>? KeyUp;
    public event Action<string>? TextInput;
    public event Action<string, CompositionStage>? TextComposition;
    public event Action? FocusLost;
    public event Action? PaintRequested;
    public event Action? Closed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Closing an active popup returns native keyboard focus to its owner.
        var restoreFocus = _restoreFocusOnClose || Win32Native.GetFocus() == Window.Handle;
        List<Exception>? failures = null;
        try { Window.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
        try { ReleaseModalBlocks(); } catch (Exception error) { (failures ??= []).Add(error); }
        try
        {
            if (restoreFocus && !_nativeOwner.IsClosed && Win32Native.IsWindowEnabled(_nativeOwner.Handle))
                Win32Native.SetFocus(_nativeOwner.Handle);
        }
        catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null) throw new AggregateException("Popup window cleanup failed.", failures);
    }
}
