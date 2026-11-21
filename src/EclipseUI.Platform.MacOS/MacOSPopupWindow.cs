using EclipseUI.Rendering;
using System.Runtime.InteropServices;

namespace EclipseUI.Platform.MacOS;

/// <summary>Creates SDL Cocoa popup windows owned by a desktop window.</summary>
public sealed class MacOSPopupWindowFactory : IPopupWindowFactory
{
    private readonly MacOSWindow _owner;

    public MacOSPopupWindowFactory(MacOSWindow owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public Size AvailableSize
    {
        get
        {
            SdlNative.VerifyMainThread();
            SdlNative.Check(SdlNative.SDL_GetDisplayUsableBounds(SdlNative.SDL_GetDisplayForWindow(_owner.NativeHandle), out var area));
            return new(area.Width / _owner.NativeUnitsPerLogicalX, area.Height / _owner.NativeUnitsPerLogicalY);
        }
    }

    public bool IsOwnerFocused => _owner.IsFocused;
    public bool SupportsModal => true;

    public IPopupWindow Create(bool interactive, IPopupWindow? parent = null)
        => Create(interactive, false, parent);

    public IPopupWindow Create(bool interactive, bool modal, IPopupWindow? parent = null)
    {
        if (parent is not null && (parent is not MacOSPopupWindow popup || !ReferenceEquals(popup.Owner, _owner)))
            throw new ArgumentException("The parent popup must belong to this desktop window.", nameof(parent));
        return new MacOSPopupWindow(_owner, parent as MacOSPopupWindow, interactive || modal, modal);
    }
}

internal sealed class MacOSPopupWindow : IPopupWindow
{
    private readonly MacOSPopupWindow? _parent;
    private readonly MacOSWindow _nativeParent;
    private Rect _bounds;
    private bool _disposed;
    private (Rect Bounds, double Radius, int Width, int Height, double Scale)? _shape;
    internal MacOSWindow Owner { get; }
    internal MacOSWindow Window { get; }

    internal MacOSPopupWindow(MacOSWindow owner, MacOSPopupWindow? parent, bool interactive, bool modal = false)
    {
        Owner = owner;
        _parent = parent;
        IsModal = modal;
        var nativeParent = parent;
        if (modal)
            while (nativeParent is not null && !nativeParent.IsModal) nativeParent = nativeParent._parent;
        _nativeParent = nativeParent?.Window ?? owner;
        Window = new MacOSWindow(_nativeParent, interactive, modal);
        Window.PointerMoved += (_, e) => PointerMoved?.Invoke(e.LogicalPosition);
        Window.PointerPressed += (_, e) => PointerPressed?.Invoke(e.LogicalPosition, e.Modifiers, e.Button);
        Window.PointerReleased += (_, e) => PointerReleased?.Invoke(e.LogicalPosition);
        Window.PointerCaptureLost += (_, _) => PointerCaptureLost?.Invoke();
        Window.PointerWheel += (_, e) => PointerWheel?.Invoke(e.LogicalPosition, e.Delta, e.Modifiers);
        Window.KeyDown += (_, e) => KeyDown?.Invoke(e.Key, e.Modifiers, e.IsRepeat);
        Window.KeyUp += (_, e) => KeyUp?.Invoke(e.Key, e.Modifiers);
        Window.CharacterTyped += (_, e) => TextInput?.Invoke(e.Text);
        Window.TextComposition += (_, e) => TextComposition?.Invoke(e.Text, e.Stage);
        Window.FocusLost += (_, _) => FocusLost?.Invoke();
        Window.PaintRequested += (_, _) => PaintRequested?.Invoke();
        Window.Resized += (_, _) => PaintRequested?.Invoke();
        Window.Closing += (_, _) => Closed?.Invoke();
    }

    public NativeWindowHandle SurfaceHandle => Window.SurfaceHandle with { IsTransparent = true };
    public bool IsTransparent => true;
    public int ClientWidth => Window.ClientWidth;
    public int ClientHeight => Window.ClientHeight;
    public double ScaleFactor => Window.ScaleFactor;
    public bool IsClosed => Window.IsClosed;
    public bool IsFocused => Window.IsFocused;
    public bool IsModal { get; }

    public Rect SetBounds(Rect bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SdlNative.Check(SdlNative.SDL_GetWindowPosition(Owner.NativeHandle, out var ownerX, out var ownerY));
        var ux = Owner.NativeUnitsPerLogicalX;
        var uy = Owner.NativeUnitsPerLogicalY;
        var width = Math.Max(1, (int)Math.Round(bounds.Width * Window.NativeUnitsPerLogicalX));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * Window.NativeUnitsPerLogicalY));
        var x = ownerX + (int)Math.Round(bounds.X * ux);
        var y = ownerY + (int)Math.Round(bounds.Y * uy);
        SdlNative.Check(SdlNative.SDL_GetDisplayUsableBounds(SdlNative.SDL_GetDisplayForWindow(Owner.NativeHandle), out var area));
        width = Math.Min(width, area.Width);
        height = Math.Min(height, area.Height);
        x = Math.Clamp(x, area.X, area.X + Math.Max(0, area.Width - width));
        y = Math.Clamp(y, area.Y, area.Y + Math.Max(0, area.Height - height));

        // SDL popup menus are parent-relative; regular modal child windows use screen coordinates.
        var parentX = ownerX + (int)Math.Round((_parent?._bounds.X ?? 0) * ux);
        var parentY = ownerY + (int)Math.Round((_parent?._bounds.Y ?? 0) * uy);
        SdlNative.Check(SdlNative.SDL_GetWindowSize(Window.NativeHandle, out var currentWidth, out var currentHeight));
        if (currentWidth != width || currentHeight != height)
            SdlNative.Check(SdlNative.SDL_SetWindowSize(Window.NativeHandle, width, height));
        SdlNative.Check(SdlNative.SDL_GetWindowPosition(Window.NativeHandle, out var currentX, out var currentY));
        var requestedX = IsModal ? x : x - parentX;
        var requestedY = IsModal ? y : y - parentY;
        if (currentX != requestedX || currentY != requestedY)
            SdlNative.Check(SdlNative.SDL_SetWindowPosition(Window.NativeHandle, requestedX, requestedY));
        Window.RefreshMetrics();
        SdlNative.Check(SdlNative.SDL_GetWindowPosition(Window.NativeHandle, out var actualX, out var actualY));
        return _bounds = new((actualX + (IsModal ? 0 : parentX) - ownerX) / ux,
            (actualY + (IsModal ? 0 : parentY) - ownerY) / uy,
            Window.LogicalClientSize.Width, Window.LogicalClientSize.Height);
    }

    public void Show() => Window.Show();
    public void Activate() { if (IsModal && !IsClosed) Window.Activate(); }
    public void SetShape(Rect contentBounds, double cornerRadius)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var radius = Math.Clamp(cornerRadius, 0, Math.Min(contentBounds.Width, contentBounds.Height) / 2);
        var key = (contentBounds, radius, ClientWidth, ClientHeight, ScaleFactor);
        if (_shape == key) return;
        var pixels = new byte[checked(ClientWidth * ClientHeight * 4)];
        for (var y = 0; y < ClientHeight; y++)
        {
            var py = (y + 0.5) / ScaleFactor;
            if (py < contentBounds.Top || py >= contentBounds.Bottom) continue;
            for (var x = 0; x < ClientWidth; x++)
            {
                var px = (x + 0.5) / ScaleFactor;
                if (px < contentBounds.Left || px >= contentBounds.Right) continue;
                var dx = Math.Max(0, Math.Max(contentBounds.Left + radius - px, px - (contentBounds.Right - radius)));
                var dy = Math.Max(0, Math.Max(contentBounds.Top + radius - py, py - (contentBounds.Bottom - radius)));
                if (dx * dx + dy * dy <= radius * radius) pixels[(y * ClientWidth + x) * 4 + 3] = 255;
            }
        }
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            // SDL_PIXELFORMAT_RGBA32 is ABGR8888 on little-endian macOS.
            var surface = SdlNative.SDL_CreateSurfaceFrom(ClientWidth, ClientHeight, 0x16762004, pinned.AddrOfPinnedObject(), ClientWidth * 4);
            SdlNative.Check(surface != 0);
            try { SdlNative.Check(SdlNative.SDL_SetWindowShape(Window.NativeHandle, surface)); }
            finally { SdlNative.SDL_DestroySurface(surface); }
            _shape = key;
        }
        finally { pinned.Free(); }
    }
    public void SetTextInputEnabled(bool enabled) => Window.SetTextInputEnabled(enabled);
    public void SetImeCaret(Rect caret) => Window.SetImeCaret(caret);
    public void Dispose()
    {
        if (_disposed) return;
        var restoreFocus = IsModal && Window.IsFocused && !_nativeParent.IsClosed;
        _disposed = true;
        try { Window.Dispose(); }
        finally { if (restoreFocus && !_nativeParent.IsClosed) _nativeParent.Activate(); }
    }

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
}
