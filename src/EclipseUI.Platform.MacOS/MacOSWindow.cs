using System.Runtime.ExceptionServices;
using EclipseUI.Rendering;

namespace EclipseUI.Platform.MacOS;

/// <summary>SDL3 Cocoa window with a Metal NSView. Create, pump and dispose on the process main thread.</summary>
public sealed class MacOSWindow : IMacOSWindow, ISystemThemeProvider
{
    // SDL video is initialized by this window; query only on its UI thread.
    ThemeVariant ISystemThemeProvider.Variant => SdlNative.SDL_GetSystemTheme() == 2 ? ThemeVariant.Dark : ThemeVariant.Light;
    private static readonly Dictionary<uint, MacOSWindow> Windows = new();
    // Keep the delegate rooted until SDL_RemoveEventWatch has completed.
    private readonly SdlNative.EventFilter _eventWatch;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private bool _watchRegistered;
    private bool _watchingEvent;
    private ExceptionDispatchInfo? _eventWatchFailure;
    private nint _window;
    private nint _metalView;
    private uint _id;
    private bool _ownsVideo;
    private readonly bool _popup;
    private bool _modalActive;
    private bool _minimized;
    private bool _textInput;
    private bool _pendingCompositionEnd;
    private double _densityX = 1, _densityY = 1;
    private readonly MacOSComposition _composition = new();

    public MacOSWindow(string title, int width, int height) : this(title, width, height, null, true, false) { }

    internal MacOSWindow(MacOSWindow parent, bool interactive, bool modal = false) : this("", 1, 1, parent, interactive, modal) { }

    private MacOSWindow(string title, int width, int height, MacOSWindow? parent, bool interactive, bool modal)
    {
        _eventWatch = WatchEvent;
        SdlNative.VerifyMainThread();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _popup = parent is not null && !modal;
        try
        {
            SdlNative.SDL_SetMainReady();
            SdlNative.Check(SdlNative.SDL_InitSubSystem(SdlNative.InitVideo));
            _ownsVideo = true;
            _window = parent is null || modal
                ? SdlNative.SDL_CreateWindow(title, width, height, modal
                    ? 0x8 | 0x10 | 0x2000 | 0x20000000UL | 0x40000000UL : SdlNative.WindowFlags)
                : SdlNative.SDL_CreatePopupWindow(parent._window, 0, 0, width, height,
                    0x8 | 0x2000 | 0x20000000UL | 0x40000000UL | (interactive ? 0x80000UL : 0x40000UL));
            SdlNative.Check(_window != 0);
            if (modal)
            {
                // SDL popup-menu windows cannot be modal. Use a regular transparent
                // child window and let Cocoa own its native modal session.
                SdlNative.Check(SdlNative.SDL_SetWindowParent(_window, parent!._window));
                SdlNative.Check(SdlNative.SDL_SetWindowModal(_window, true));
                _modalActive = true;
            }
            VideoDriver = SdlNative.Utf8(SdlNative.SDL_GetCurrentVideoDriver());
            if (VideoDriver != "cocoa")
                throw new PlatformNotSupportedException($"SDL video driver '{VideoDriver}' is not Cocoa. macOS desktop requires the Cocoa driver.");
            _metalView = SdlNative.SDL_Metal_CreateView(_window);
            SdlNative.Check(_metalView != 0);
            _id = SdlNative.SDL_GetWindowID(_window);
            Windows.Add(_id, this);
            UpdateMetrics();
            SetClientSize(width, height);
            SdlNative.Check(SdlNative.SDL_AddEventWatch(_eventWatch, 0));
            _watchRegistered = true;
        }
        catch (DllNotFoundException error)
        {
            Dispose();
            throw new DllNotFoundException("macOS desktop requires SDL3 3.2 or later. Install it with brew install sdl3, or place libSDL3.dylib or SDL3.framework beside the application.", error);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string VideoDriver { get; } = "";
    internal uint Id => _id;
    internal nint NativeHandle => _window;
    internal double NativeUnitsPerLogicalX => ScaleFactor / _densityX;
    internal double NativeUnitsPerLogicalY => ScaleFactor / _densityY;
    internal bool IsFocused => _window != 0 && !IsClosed && SdlNative.SDL_GetKeyboardFocus() == _window;
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
            return NativeWindowHandle.FromAppKit(_metalView);
        }
    }

    public event EventHandler<MacOSResizeEventArgs>? Resized;
    public event EventHandler? PaintRequested;
    public event EventHandler<MacOSPointerEventArgs>? PointerMoved;
    public event EventHandler<MacOSPointerEventArgs>? PointerPressed;
    public event EventHandler<MacOSPointerEventArgs>? PointerReleased;
    public event EventHandler? PointerCaptureLost;
    public event EventHandler<MacOSWheelEventArgs>? PointerWheel;
    public event EventHandler<MacOSKeyEventArgs>? KeyDown;
    public event EventHandler<MacOSKeyEventArgs>? KeyUp;
    public event EventHandler<MacOSTextEventArgs>? CharacterTyped;
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
        if (!_popup && !IsClosed) SdlNative.Check(SdlNative.SDL_RaiseWindow(_window));
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
        _eventWatchFailure?.Throw();
        while (SdlNative.SDL_PollEvent(out var message))
        {
            _eventWatchFailure?.Throw();
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
        _eventWatchFailure?.Throw();
        foreach (var window in Windows.Values.ToArray()) window.FinishCompositionIfPending();
        return !IsClosed;
    }

    private bool WatchEvent(nint userdata, ref SdlNative.Event message)
    {
        // Cocoa's nested tracking loop sends expose events before SDL_PollEvent returns.
        // Other threads may push SDL events: leave those for normal UI-thread dispatch.
        if (Environment.CurrentManagedThreadId != _uiThreadId ||
            message.Type != SdlNative.WindowExposed || message.WindowId != _id ||
            IsClosed || _window == 0 || _watchingEvent || _eventWatchFailure is not null) return true;
        _watchingEvent = true;
        try
        {
            var previous = (ClientWidth, ClientHeight, ScaleFactor);
            UpdateMetrics();
            // A changed size already requests a frame through Resized.
            if (!IsClosed && previous == (ClientWidth, ClientHeight, ScaleFactor))
                PaintRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            // Never unwind a managed exception through SDL/AppKit.
            _eventWatchFailure = ExceptionDispatchInfo.Capture(error);
        }
        finally { _watchingEvent = false; }
        return true;
    }

    private void Dispatch(SdlNative.Event message)
    {
        if (message.Type is not (SdlNative.TextInput or SdlNative.TextEditing))
            FinishCompositionIfPending();
        if (IsClosed) return;
        Point Position(float x, float y) => MacOSInput.ToLogical(x, y, _densityX, _densityY, ScaleFactor);
        KeyModifiers Modifiers() => MacOSInput.Modifiers(SdlNative.SDL_GetModState());
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
                _pendingCompositionEnd = false;
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
                if (MacOSInput.Button(message.Button.Button) is not { } button) break;
                var pointer = new MacOSPointerEventArgs(Position(message.Button.X, message.Button.Y), Modifiers(), button);
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
                    MacOSInput.WheelDelta(message.Wheel.Y, message.Wheel.Direction), Modifiers()));
                break;
            case SdlNative.KeyDown:
            case SdlNative.KeyUp:
                // Editing/navigation keys belong to the IME while a preedit is active.
                if (_composition.IsActive) break;
                var key = new MacOSKeyEventArgs(MacOSInput.Key(message.Key.Key), MacOSInput.Modifiers(message.Key.Mod), message.Key.Repeat != 0);
                if (message.Type == SdlNative.KeyDown) KeyDown?.Invoke(this, key);
                else KeyUp?.Invoke(this, key);
                break;
            case SdlNative.TextEditing:
                var preedit = SdlNative.Utf8(message.Text.Text);
                // Cocoa unmarks before inserting committed text. Defer an empty
                // preedit until the queue is drained so that commit ends the
                // same composition rather than cancelling and reinserting it.
                _pendingCompositionEnd = preedit.Length == 0 && _composition.IsActive;
                if (!_pendingCompositionEnd) _composition.Edit(preedit, EmitComposition);
                break;
            case SdlNative.TextInput:
                _pendingCompositionEnd = false;
                var text = SdlNative.Utf8(message.Text.Text);
                // SDL sends committed UTF-8 text separately from Cocoa marked text.
                // Option/dead-key characters must not be filtered as shortcuts.
                if (!_composition.Commit(text, EmitComposition))
                    CharacterTyped?.Invoke(this, new(text));
                break;
        }
    }

    private void FinishCompositionIfPending()
    {
        if (!_pendingCompositionEnd) return;
        _pendingCompositionEnd = false;
        _composition.Cancel(EmitComposition);
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
        SdlNative.Check(enabled ? SdlNative.SDL_StartTextInput(_window) : SdlNative.SDL_StopTextInput(_window));
        _textInput = enabled;
        if (!enabled)
        {
            _pendingCompositionEnd = false;
            _composition.Cancel(EmitComposition);
        }
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
        if (!IsClosed) SdlNative.Check(SdlNative.SDL_SetTextInputArea(_window, in rect, 0));
    }

    private void VerifyAccess()
    {
        SdlNative.VerifyMainThread();
        ObjectDisposedException.ThrowIf(_window == 0, this);
    }

    public void Dispose()
    {
        SdlNative.VerifyMainThread();
        Exception? modalFailure = null;
        if (_modalActive && _window != 0)
        {
            try { SdlNative.Check(SdlNative.SDL_SetWindowModal(_window, false)); }
            catch (Exception error) { modalFailure = error; }
            finally { _modalActive = false; }
        }
        IsClosed = true;
        if (_watchRegistered)
        {
            SdlNative.SDL_RemoveEventWatch(_eventWatch, 0);
            _watchRegistered = false;
        }
        Windows.Remove(_id);
        if (_metalView != 0)
        {
            SdlNative.SDL_Metal_DestroyView(_metalView);
            _metalView = 0;
        }
        if (_window != 0)
        {
            SdlNative.SDL_DestroyWindow(_window);
            _window = 0;
        }
        if (_ownsVideo)
        {
            SdlNative.SDL_QuitSubSystem(SdlNative.InitVideo);
            _ownsVideo = false;
        }
        if (modalFailure is not null) throw new AggregateException("Native modal cleanup failed.", modalFailure);
    }
}

internal sealed class SdlClipboard : IClipboard
{
    public string GetText()
    {
        SdlNative.VerifyMainThread();
        var pointer = SdlNative.SDL_GetClipboardText();
        try { return SdlNative.Utf8(pointer); }
        finally { if (pointer != 0) SdlNative.SDL_free(pointer); }
    }

    public void SetText(string text)
    {
        SdlNative.VerifyMainThread();
        SdlNative.Check(SdlNative.SDL_SetClipboardText(text));
    }
}

/// <summary>SDL commits text once, separately from preedit updates; never also emit a character event.</summary>
internal sealed class MacOSComposition
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
