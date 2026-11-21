using EclipseUI.Rendering;

namespace EclipseUI.Platform.Linux;

/// <summary>SDL3 window backed by X11 or Wayland. Create, pump and dispose on the main thread.</summary>
public sealed class LinuxWindow : ILinuxWindow, ISystemThemeProvider
{
    // Query only after SDL video initialization, on the window's UI thread.
    ThemeVariant ISystemThemeProvider.Variant => SdlNative.SDL_GetSystemTheme() == 2 ? ThemeVariant.Dark : ThemeVariant.Light;
    private static readonly Dictionary<uint, LinuxWindow> Windows = new();
    private static int VideoThread;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private nint _window;
    private uint _id;
    private bool _ownsVideo;
    private readonly bool _modal;
    private bool _modalActive;
    private bool _minimized;
    private bool _textInput;
    private bool _basicTextInput;
    private double _densityX = 1, _densityY = 1;
    private readonly LinuxComposition _composition = new();

    public LinuxWindow(string title, int width, int height) : this(title, width, height, null, true, false) { }

    internal LinuxWindow(LinuxWindow parent, bool interactive, bool modal = false) : this("", 1, 1, parent, interactive, modal) { }

    private LinuxWindow(string title, int width, int height, LinuxWindow? parent, bool interactive, bool modal)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("LinuxWindow requires Linux.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (Windows.Count != 0 && VideoThread != _thread)
            throw new InvalidOperationException("All SDL windows must use the same main thread.");
        _modal = modal;
        try
        {
            SdlNative.Check(SdlNative.SDL_InitSubSystem(SdlNative.InitVideo));
            _ownsVideo = true;
            VideoThread = _thread;
            _window = parent is null || modal
                ? SdlNative.SDL_CreateWindow(title, width, height, modal
                    ? 0x8 | 0x10 | 0x2000 | 0x10000000 | SdlNative.Transparent
                    : SdlNative.WindowFlags)
                : SdlNative.SDL_CreatePopupWindow(parent._window, 0, 0, width, height,
                    0x8 | 0x2000 | 0x10000000 | SdlNative.Transparent | (interactive ? SdlNative.PopupMenu : SdlNative.Tooltip));
            SdlNative.Check(_window != 0);
            if (modal)
            {
                SdlNative.Check(SdlNative.SDL_SetWindowParent(_window, parent!._window));
                SdlNative.Check(SdlNative.SDL_SetWindowModal(_window, true));
                _modalActive = true;
            }
            VideoDriver = SdlNative.Utf8(SdlNative.SDL_GetCurrentVideoDriver());
            _id = SdlNative.SDL_GetWindowID(_window);
            Windows.Add(_id, this);
            UpdateMetrics();
            SetClientSize(width, height);
        }
        catch (DllNotFoundException error)
        {
            Dispose();
            throw new DllNotFoundException("Linux desktop requires SDL3 (libSDL3.so.0, version 3.2 or later). Install your distribution's SDL3 runtime.", error);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string VideoDriver { get; } = "";
    internal uint Id => _id;
    internal bool UsesBasicTextInput => _basicTextInput;
    internal nint SdlHandle => _window;
    internal double WindowUnitsPerLogicalX => ScaleFactor / _densityX;
    internal double WindowUnitsPerLogicalY => ScaleFactor / _densityY;
    internal bool IsFocused => !IsClosed && _window != 0 && (SdlNative.SDL_GetWindowFlags(_window) & SdlNative.InputFocus) != 0;

    internal void RefreshMetrics() { VerifyAccess(); UpdateMetrics(); }
    public int ClientWidth { get; private set; }
    public int ClientHeight { get; private set; }
    public double ScaleFactor { get; private set; } = 1;
    public Size LogicalClientSize => _minimized ? Size.Empty : new(ClientWidth / ScaleFactor, ClientHeight / ScaleFactor);
    public bool IsClosed { get; private set; }
    public IClipboard Clipboard { get; } = new SdlClipboard();

    public NativeWindowHandle SurfaceHandle
    {
        get
        {
            VerifyAccess();
            var properties = SdlNative.SDL_GetWindowProperties(_window);
            return VideoDriver switch
            {
                "x11" => NativeWindowHandle.FromXlib(
                    (nint)SdlNative.SDL_GetNumberProperty(properties, "SDL.window.x11.window", 0),
                    SdlNative.SDL_GetPointerProperty(properties, "SDL.window.x11.display", 0),
                    (int)SdlNative.SDL_GetNumberProperty(properties, "SDL.window.x11.screen", 0)),
                "wayland" => NativeWindowHandle.FromWayland(
                    SdlNative.SDL_GetPointerProperty(properties, "SDL.window.wayland.surface", 0),
                    SdlNative.SDL_GetPointerProperty(properties, "SDL.window.wayland.display", 0)),
                _ => throw new PlatformNotSupportedException($"SDL video driver '{VideoDriver}' has no supported GPU surface. Use x11 or wayland."),
            };
        }
    }

    public event EventHandler<LinuxResizeEventArgs>? Resized;
    public event EventHandler? PaintRequested;
    public event EventHandler<LinuxPointerEventArgs>? PointerMoved;
    public event EventHandler<LinuxPointerEventArgs>? PointerPressed;
    public event EventHandler<LinuxPointerEventArgs>? PointerReleased;
    public event EventHandler? PointerCaptureLost;
    public event EventHandler<LinuxWheelEventArgs>? PointerWheel;
    public event EventHandler<LinuxKeyEventArgs>? KeyDown;
    public event EventHandler<LinuxKeyEventArgs>? KeyUp;
    public event EventHandler<LinuxTextEventArgs>? CharacterTyped;
    public event EventHandler<TextCompositionEventArgs>? TextComposition;
    public event EventHandler? FocusLost;
    public event EventHandler? Closing;

    public void Show()
    {
        VerifyAccess();
        if (IsClosed) return;
        SdlNative.Check(SdlNative.SDL_ShowWindow(_window));
        UpdateMetrics();
    }

    internal void Activate()
    {
        VerifyAccess();
        // Compositors can refuse focus activation without invalidating modality.
        // Raising is best-effort, matching Win32's foreground-window policy.
        if (!IsClosed && _modal) SdlNative.SDL_RaiseWindow(_window);
    }

    public void Close()
    {
        VerifyAccess();
        if (IsClosed) return;
        IsClosed = true;
        SdlNative.SDL_HideWindow(_window);
        // The GPU surface still borrows this window; destroy it only after presenter disposal.
        Closing?.Invoke(this, EventArgs.Empty);
    }

    public void SetTitle(string title)
    {
        VerifyAccess();
        if (!IsClosed) SdlNative.Check(SdlNative.SDL_SetWindowTitle(_window, title));
    }

    public void SetClientSize(double width, double height)
    {
        VerifyAccess();
        if (IsClosed) return;
        var w = width > 0 ? checked((int)Math.Round(width * ScaleFactor / _densityX)) : (int)Math.Round(ClientWidth / _densityX);
        var h = height > 0 ? checked((int)Math.Round(height * ScaleFactor / _densityY)) : (int)Math.Round(ClientHeight / _densityY);
        SdlNative.Check(SdlNative.SDL_SetWindowSize(_window, Math.Max(w, 1), Math.Max(h, 1)));
        UpdateMetrics();
    }

    public bool PumpMessages()
    {
        VerifyAccess();
        while (SdlNative.SDL_PollEvent(out var message))
        {
            if (message.Type == SdlNative.Quit)
            {
                foreach (var window in Windows.Values.ToArray()) window.Close();
            }
            else if (Windows.TryGetValue(message.WindowId, out var window) && !window.IsClosed)
            {
                window.Dispatch(message);
            }
            if (_window == 0 || IsClosed) break;
        }
        return !IsClosed;
    }

    private void Dispatch(SdlNative.Event message)
    {
        Point Position(float x, float y) => LinuxInput.ToLogical(x, y, _densityX, _densityY, ScaleFactor);
        KeyModifiers Modifiers() => LinuxInput.Modifiers(SdlNative.SDL_GetModState());
        switch (message.Type)
        {
            case SdlNative.WindowCloseRequested: Close(); break;
            case SdlNative.WindowExposed: PaintRequested?.Invoke(this, EventArgs.Empty); break;
            case SdlNative.WindowMinimized: _minimized = true; break;
            case SdlNative.WindowRestored:
                _minimized = false;
                UpdateMetrics();
                PaintRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SdlNative.WindowResized:
            case SdlNative.WindowPixelSizeChanged:
            case SdlNative.WindowDisplayChanged:
            case SdlNative.WindowDisplayScaleChanged:
                UpdateMetrics();
                break;
            case SdlNative.WindowFocusLost:
                SdlNative.SDL_CaptureMouse(false);
                _composition.Cancel(EmitComposition);
                PointerCaptureLost?.Invoke(this, EventArgs.Empty);
                FocusLost?.Invoke(this, EventArgs.Empty);
                break;
            case SdlNative.WindowMouseLeave:
                // Capture keeps drags alive outside the client area; an uncaptured move clears hover.
                PointerMoved?.Invoke(this, new(new Point(-1, -1), Modifiers()));
                break;
            case SdlNative.MouseMotion:
                PointerMoved?.Invoke(this, new(Position(message.Motion.X, message.Motion.Y), Modifiers()));
                break;
            case SdlNative.MouseButtonDown:
            case SdlNative.MouseButtonUp:
                if (LinuxInput.Button(message.Button.Button) is not { } button) break;
                var pointer = new LinuxPointerEventArgs(Position(message.Button.X, message.Button.Y), Modifiers(), button);
                if (message.Type == SdlNative.MouseButtonDown)
                {
                    SdlNative.SDL_CaptureMouse(true);
                    PointerPressed?.Invoke(this, pointer);
                }
                else
                {
                    SdlNative.SDL_CaptureMouse(false);
                    PointerReleased?.Invoke(this, pointer);
                }
                break;
            case SdlNative.MouseWheel:
                PointerWheel?.Invoke(this, new(Position(message.Wheel.MouseX, message.Wheel.MouseY),
                    LinuxInput.WheelDelta(message.Wheel.Y, message.Wheel.Direction), Modifiers()));
                break;
            case SdlNative.KeyDown:
            case SdlNative.KeyUp:
                // Editing/navigation keys belong to the IME while a preedit is active.
                if (_composition.IsActive) break;
                var key = new LinuxKeyEventArgs(LinuxInput.Key(message.Key.Key), LinuxInput.Modifiers(message.Key.Mod), message.Key.Repeat != 0);
                if (message.Type == SdlNative.KeyDown)
                {
                    KeyDown?.Invoke(this, key);
                    if (_textInput && _basicTextInput && !IsClosed)
                    {
                        var code = message.Key.Scancode == 0 ? message.Key.Key
                            : SdlNative.SDL_GetKeyFromScancode(message.Key.Scancode, message.Key.Mod, false);
                        if (LinuxInput.BasicText(code, message.Key.Mod) is { } basicText)
                            CharacterTyped?.Invoke(this, new(basicText));
                    }
                }
                else KeyUp?.Invoke(this, key);
                break;
            case SdlNative.TextEditing:
                _composition.Edit(SdlNative.Utf8(message.Text.Text), EmitComposition);
                break;
            case SdlNative.TextInput:
                var text = SdlNative.Utf8(message.Text.Text);
                // SDL only sends committed text here. Keep AltGr text, even on layouts
                // where the native modifier state includes Control + Alt.
                if (!_composition.Commit(text, EmitComposition))
                    CharacterTyped?.Invoke(this, new(text));
                break;
        }
    }

    private void EmitComposition(TextCompositionEventArgs args) => TextComposition?.Invoke(this, args);

    private void UpdateMetrics()
    {
        SdlNative.Check(SdlNative.SDL_GetWindowSize(_window, out var width, out var height));
        SdlNative.Check(SdlNative.SDL_GetWindowSizeInPixels(_window, out var pixelWidth, out var pixelHeight));
        var scale = SdlNative.SDL_GetWindowDisplayScale(_window);
        var changed = ClientWidth != pixelWidth || ClientHeight != pixelHeight || ScaleFactor != scale;
        ClientWidth = pixelWidth;
        ClientHeight = pixelHeight;
        ScaleFactor = float.IsFinite(scale) && scale > 0 ? scale : 1;
        _densityX = width > 0 && pixelWidth > 0 ? pixelWidth / (double)width : 1;
        _densityY = height > 0 && pixelHeight > 0 ? pixelHeight / (double)height : 1;
        if (changed) Resized?.Invoke(this, new(pixelWidth, pixelHeight, ScaleFactor));
    }

    public void SetTextInputEnabled(bool enabled)
    {
        VerifyAccess();
        if (enabled == _textInput || IsClosed) return;
        if (!_basicTextInput && !(enabled ? SdlNative.SDL_StartTextInput(_window) : SdlNative.SDL_StopTextInput(_window)))
        {
            var error = SdlNative.Utf8(SdlNative.SDL_GetError());
            // Older SDL rejects StartTextInput altogether when a Wayland compositor
            // (including WSLg) lacks text-input-v3. It leaves SDL text input inactive,
            // so ignoring the error alone would silently discard normal typing.
            if (!enabled || VideoDriver != "wayland" || !error.Contains("zwp_text_input_v3", StringComparison.Ordinal))
                throw new InvalidOperationException("SDL3: " + error);
            _basicTextInput = true;
            Console.WriteLine("text: Wayland text-input-v3 unavailable; using basic keyboard input. Use X11 for native IME support.");
        }
        _textInput = enabled;
        if (!enabled) _composition.Cancel(EmitComposition);
    }

    public void SetImeCaret(Rect logicalCaret)
    {
        VerifyAccess();
        var rect = new SdlNative.Rect
        {
            X = (int)Math.Round(logicalCaret.X * ScaleFactor / _densityX),
            Y = (int)Math.Round(logicalCaret.Y * ScaleFactor / _densityY),
            Width = Math.Max(1, (int)Math.Ceiling(logicalCaret.Width * ScaleFactor / _densityX)),
            Height = Math.Max(1, (int)Math.Ceiling(logicalCaret.Height * ScaleFactor / _densityY)),
        };
        SdlNative.SDL_SetTextInputArea(_window, in rect, 0);
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Linux window operations must use the main UI thread.");
        ObjectDisposedException.ThrowIf(_window == 0, this);
    }

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Linux window disposal must use the main UI thread.");
        IsClosed = true;
        Windows.Remove(_id);
        List<Exception>? failures = null;
        if (_window != 0)
        {
            // DestroyWindow is still required when clearing modality is unsupported
            // or fails; SDL releases the driver's modal state while destroying it.
            try { if (_modalActive) SdlNative.Check(SdlNative.SDL_SetWindowModal(_window, false)); }
            catch (Exception error) { (failures ??= []).Add(error); }
            finally
            {
                _modalActive = false;
                try { SdlNative.SDL_DestroyWindow(_window); }
                catch (Exception error) { (failures ??= []).Add(error); }
                finally { _window = 0; }
            }
        }
        if (_ownsVideo)
        {
            try { SdlNative.SDL_QuitSubSystem(SdlNative.InitVideo); }
            catch (Exception error) { (failures ??= []).Add(error); }
            finally { _ownsVideo = false; }
        }
        if (failures is not null) throw new AggregateException("Linux window cleanup failed.", failures);
    }
}

internal sealed class SdlClipboard : IClipboard
{
    public string GetText()
    {
        var pointer = SdlNative.SDL_GetClipboardText();
        try { return SdlNative.Utf8(pointer); }
        finally { if (pointer != 0) SdlNative.SDL_free(pointer); }
    }

    public void SetText(string text) => SdlNative.Check(SdlNative.SDL_SetClipboardText(text));
}

/// <summary>SDL commits text once, separately from preedit updates; never also emit a character event.</summary>
internal sealed class LinuxComposition
{
    internal bool IsActive { get; private set; }
    internal void Edit(string text, Action<TextCompositionEventArgs> emit)
    {
        if (text.Length == 0) { Cancel(emit); return; }
        if (!IsActive) { IsActive = true; emit(new("", CompositionStage.Started)); }
        emit(new(text, CompositionStage.Updated));
    }
    internal bool Commit(string text, Action<TextCompositionEventArgs> emit)
    {
        if (!IsActive) return false;
        IsActive = false;
        emit(new(text, CompositionStage.Committed));
        return true;
    }
    internal void Cancel(Action<TextCompositionEventArgs> emit)
    {
        if (!IsActive) return;
        IsActive = false;
        emit(new("", CompositionStage.Cancelled));
    }
}
