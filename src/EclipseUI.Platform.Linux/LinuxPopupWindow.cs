using EclipseUI.Rendering;
using System.Runtime.InteropServices;

namespace EclipseUI.Platform.Linux;

/// <summary>Owned SDL popup windows, including the compositor's native popup positioning on Wayland.</summary>
public sealed class LinuxPopupWindowFactory(LinuxWindow owner) : IPopupWindowFactory
{
    public bool IsOwnerFocused => owner.IsFocused;
    public bool SupportsModal => true;

    public Size AvailableSize
    {
        get
        {
            var display = SdlNative.SDL_GetDisplayForWindow(owner.SdlHandle);
            if (!SdlNative.SDL_GetDisplayUsableBounds(display, out var bounds))
                return owner.LogicalClientSize;
            return new(bounds.Width / owner.WindowUnitsPerLogicalX, bounds.Height / owner.WindowUnitsPerLogicalY);
        }
    }

    public IPopupWindow Create(bool interactive, IPopupWindow? parent = null)
        => Create(interactive, false, parent);

    public IPopupWindow Create(bool interactive, bool modal, IPopupWindow? parent = null)
    {
        if (parent is not null && (parent is not LinuxPopupWindow popup || !ReferenceEquals(popup.Owner, owner)))
            throw new ArgumentException("The parent popup must belong to this desktop window.", nameof(parent));
        return new LinuxPopupWindow(owner, parent as LinuxPopupWindow, interactive || modal, modal);
    }
}

internal sealed class LinuxPopupWindow : IPopupWindow
{
    private readonly LinuxWindow _owner;
    private readonly LinuxPopupWindow? _parent;
    private readonly LinuxWindow _window;
    private Rect _bounds;
    private bool _disposed;
    private bool _shown;
    private (int X, int Y, int W, int H)? _requestedBounds;
    private (int Width, int Height, Rect Content, double Radius, double UnitsX, double UnitsY)? _shape;
    private bool _shapeUnavailable;

    internal LinuxWindow Owner => _owner;

    internal LinuxPopupWindow(LinuxWindow owner, LinuxPopupWindow? parent, bool interactive, bool modal = false)
    {
        _owner = owner;
        _parent = parent;
        IsModal = modal;
        var nativeParent = parent;
        if (modal)
            while (nativeParent is { IsModal: false }) nativeParent = nativeParent._parent;
        // SDL forbids making a popup-menu modal or using one as a toplevel's parent.
        // Modal content uses a regular borderless child, owned by the nearest modal/root.
        _window = new LinuxWindow(nativeParent?._window ?? owner, interactive, modal);
        _window.PointerMoved += (_, e) => PointerMoved?.Invoke(e.LogicalPosition);
        _window.PointerPressed += (_, e) => PointerPressed?.Invoke(e.LogicalPosition, e.Modifiers, e.Button);
        _window.PointerReleased += (_, e) => PointerReleased?.Invoke(e.LogicalPosition);
        _window.PointerCaptureLost += (_, _) => PointerCaptureLost?.Invoke();
        _window.PointerWheel += (_, e) => PointerWheel?.Invoke(e.LogicalPosition, e.Delta, e.Modifiers);
        _window.KeyDown += (_, e) => KeyDown?.Invoke(e.Key, e.Modifiers, e.IsRepeat);
        _window.KeyUp += (_, e) => KeyUp?.Invoke(e.Key, e.Modifiers);
        _window.CharacterTyped += (_, e) => TextInput?.Invoke(e.Text);
        _window.TextComposition += (_, e) => TextComposition?.Invoke(e.Text, e.Stage);
        _window.FocusLost += (_, _) => FocusLost?.Invoke();
        _window.PaintRequested += (_, _) => PaintRequested?.Invoke();
        _window.Resized += (_, _) => PaintRequested?.Invoke();
        _window.Closing += (_, _) => Closed?.Invoke();
    }

    public NativeWindowHandle SurfaceHandle => _window.SurfaceHandle with { IsTransparent = true };
    public int ClientWidth => _window.ClientWidth;
    public int ClientHeight => _window.ClientHeight;
    public double ScaleFactor => _window.ScaleFactor;
    public bool IsClosed => _disposed || _window.IsClosed;
    public bool IsFocused => !IsClosed && _window.IsFocused;
    public bool IsTransparent => true;
    public bool IsModal { get; }

    public Rect SetBounds(Rect bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsModal) return SetModalBounds(bounds);
        var parent = _parent?._window ?? _owner;
        _window.RefreshMetrics();
        var origin = _parent?._bounds.Location ?? Point.Zero;
        var unitX = parent.WindowUnitsPerLogicalX;
        var unitY = parent.WindowUnitsPerLogicalY;
        var x = (int)Math.Round((bounds.X - origin.X) * unitX);
        var y = (int)Math.Round((bounds.Y - origin.Y) * unitY);
        var width = Math.Max(1, checked((int)Math.Ceiling(bounds.Width * _window.WindowUnitsPerLogicalX)));
        var height = Math.Max(1, checked((int)Math.Ceiling(bounds.Height * _window.WindowUnitsPerLogicalY)));

        // SDL popups use parent-relative coordinates. Wayland owns their final position;
        // X11 also lets us keep menus out of panels/taskbars through the usable bounds.
        var display = SdlNative.SDL_GetDisplayForWindow(_owner.SdlHandle);
        if (_owner.VideoDriver != "wayland" && SdlNative.SDL_GetDisplayUsableBounds(display, out var work)
            && SdlNative.SDL_GetWindowPosition(_owner.SdlHandle, out var ownerX, out var ownerY))
        {
            var rootUnitX = _owner.WindowUnitsPerLogicalX;
            var rootUnitY = _owner.WindowUnitsPerLogicalY;
            var parentX = ownerX + (int)Math.Round(origin.X * rootUnitX);
            var parentY = ownerY + (int)Math.Round(origin.Y * rootUnitY);
            width = Math.Min(width, Math.Max(1, work.Width));
            height = Math.Min(height, Math.Max(1, work.Height));
            x = Math.Clamp(parentX + x, work.X, work.X + Math.Max(0, work.Width - width)) - parentX;
            y = Math.Clamp(parentY + y, work.Y, work.Y + Math.Max(0, work.Height - height)) - parentY;
        }

        var request = (x, y, width, height);
        if (_requestedBounds != request)
        {
            SdlNative.Check(SdlNative.SDL_SetWindowSize(_window.SdlHandle, width, height));
            SdlNative.Check(SdlNative.SDL_SetWindowPosition(_window.SdlHandle, x, y));
            _requestedBounds = request;
        }
        _window.RefreshMetrics();
        if (SdlNative.SDL_GetWindowPosition(_window.SdlHandle, out var actualX, out var actualY))
        {
            x = actualX;
            y = actualY;
        }
        _bounds = new(origin.X + x / unitX, origin.Y + y / unitY,
            ClientWidth / ScaleFactor, ClientHeight / ScaleFactor);
        return _bounds;
    }

    private Rect SetModalBounds(Rect bounds)
    {
        _window.RefreshMetrics();
        var width = Math.Max(1, checked((int)Math.Ceiling(bounds.Width * _window.WindowUnitsPerLogicalX)));
        var height = Math.Max(1, checked((int)Math.Ceiling(bounds.Height * _window.WindowUnitsPerLogicalY)));
        var unitX = _owner.WindowUnitsPerLogicalX;
        var unitY = _owner.WindowUnitsPerLogicalY;
        var x = (int)Math.Round(bounds.X * unitX);
        var y = (int)Math.Round(bounds.Y * unitY);
        var originX = 0;
        var originY = 0;
        var hasGlobalPosition = _owner.VideoDriver != "wayland" &&
            SdlNative.SDL_GetWindowPosition(_owner.SdlHandle, out originX, out originY);
        if (hasGlobalPosition)
        {
            x += originX;
            y += originY;
            if (SdlNative.SDL_GetDisplayUsableBounds(SdlNative.SDL_GetDisplayForWindow(_owner.SdlHandle), out var work))
            {
                width = Math.Min(width, Math.Max(1, work.Width));
                height = Math.Min(height, Math.Max(1, work.Height));
                x = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - width));
                y = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - height));
            }
        }
        var request = (x, y, width, height);
        if (_requestedBounds != request)
        {
            SdlNative.Check(SdlNative.SDL_SetWindowSize(_window.SdlHandle, width, height));
            // Unlike SDL popup menus, regular child windows use global coordinates.
            // Wayland chooses the dialog position; retain logical bounds for input mapping.
            if (hasGlobalPosition) SdlNative.Check(SdlNative.SDL_SetWindowPosition(_window.SdlHandle, x, y));
            _requestedBounds = request;
        }
        _window.RefreshMetrics();
        var position = bounds.Location;
        if (hasGlobalPosition && SdlNative.SDL_GetWindowPosition(_window.SdlHandle, out var actualX, out var actualY))
            position = new((actualX - originX) / unitX, (actualY - originY) / unitY);
        return _bounds = new(position, new Size(ClientWidth / ScaleFactor, ClientHeight / ScaleFactor));
    }

    public void Show()
    {
        if (_shown) return;
        _shown = true;
        _window.Show();
        if (IsModal) Activate();
    }

    public void Activate()
    {
        if (!IsClosed && IsModal) _window.Activate();
    }

    public void SetTextInputEnabled(bool enabled) => _window.SetTextInputEnabled(enabled);
    public void SetImeCaret(Rect caret) => _window.SetImeCaret(caret);

    public void SetShape(Rect contentBounds, double cornerRadius)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // SDL 3.2's X11 driver updates ShapeInput without cutting off rendered shadows.
        // Its Wayland driver does not implement UpdateWindowShape; alpha still composes
        // correctly there, but the compositor continues to use a rectangular input area.
        if (_shapeUnavailable || _window.VideoDriver != "x11") return;
        SdlNative.Check(SdlNative.SDL_GetWindowSize(_window.SdlHandle, out var width, out var height));
        var geometry = (width, height, contentBounds, cornerRadius, _window.WindowUnitsPerLogicalX, _window.WindowUnitsPerLogicalY);
        if (_shape == geometry || width <= 0 || height <= 0) return;
        var pixels = LinuxPopupShape.CreateMask(width, height, contentBounds, cornerRadius,
            _window.WindowUnitsPerLogicalX, _window.WindowUnitsPerLogicalY);
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        nint surface = 0;
        try
        {
            // Both supported Linux architectures are little endian: RGBA byte order is ABGR8888.
            surface = SdlNative.SDL_CreateSurfaceFrom(width, height, SdlNative.PixelFormatAbgr8888,
                pinned.AddrOfPinnedObject(), checked(width * 4));
            SdlNative.Check(surface != 0);
            if (SdlNative.SDL_SetWindowShape(_window.SdlHandle, surface)) _shape = geometry;
            else _shapeUnavailable = true; // SDL/X11 may be built without the XShape extension.
        }
        finally
        {
            if (surface != 0) SdlNative.SDL_DestroySurface(surface);
            pinned.Free();
        }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Dispose();
    }
}

internal static class LinuxPopupShape
{
    internal static byte[] CreateMask(int width, int height, Rect content, double radius, double unitsX, double unitsY)
    {
        var pixels = new byte[checked(width * height * 4)];
        var r = Math.Clamp(radius, 0, Math.Max(0, Math.Min(content.Width, content.Height) / 2));
        for (var y = 0; y < height; y++)
        {
            var logicalY = (y + 0.5) / unitsY;
            for (var x = 0; x < width; x++)
            {
                var logicalX = (x + 0.5) / unitsX;
                if (!content.Contains(new(logicalX, logicalY))) continue;
                var centerX = Math.Clamp(logicalX, content.Left + r, content.Right - r);
                var centerY = Math.Clamp(logicalY, content.Top + r, content.Bottom - r);
                var dx = logicalX - centerX;
                var dy = logicalY - centerY;
                if (dx * dx + dy * dy <= r * r) pixels[(y * width + x) * 4 + 3] = 255;
            }
        }
        return pixels;
    }
}
