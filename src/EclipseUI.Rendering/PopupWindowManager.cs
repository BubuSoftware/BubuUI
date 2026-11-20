using EclipseUI.Elements;

namespace EclipseUI.Rendering;

/// <summary>Owns popup surfaces, sharing the owner's element tree, input router, theme and frame clock.</summary>
public sealed class PopupWindowManager : IDisposable
{
    private readonly IPopupWindowFactory _windows;
    private readonly ISurfaceFactory _surfaces;
    private readonly ITextPainter? _text;
    private readonly Dictionary<Popup, Entry> _entries = new();
    private readonly HashSet<Popup> _configured = new();
    private readonly HashSet<Popup> _overlayFallback = new();
    private Element? _root;
    private InputRouter? _input;
    private bool _focusLossPending;
    private bool _disposed;

    public PopupWindowManager(IPopupWindowFactory windows, ISurfaceFactory surfaces, ITextPainter? textPainter = null)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
        _text = textPainter;
    }

    public void PrepareLayout(Element root, InputRouter input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_root is not null && !ReferenceEquals(_root, root)) Clear();
        _root = root;
        _input = input;
        var popups = root.Descendants().Prepend(root).OfType<Popup>().ToArray();
        var current = popups.ToHashSet();
        foreach (var previous in _configured.Where(p => !current.Contains(p)).ToArray())
        {
            previous.SetWindowHosting(null);
            _configured.Remove(previous);
            _overlayFallback.Remove(previous);
        }
        var available = _windows.AvailableSize;
        if (available.IsEmpty || !double.IsFinite(available.Width) || !double.IsFinite(available.Height))
            throw new InvalidOperationException("The native popup factory must provide a finite, positive display size.");
        foreach (var popup in popups)
        {
            popup.SetWindowHosting(popup.HostMode == PopupHostMode.Overlay || _overlayFallback.Contains(popup) ? null : available);
            _configured.Add(popup);
        }
    }

    /// <summary>Defers focus loss until the native queue has finished transferring focus between owned windows.</summary>
    public bool HandleOwnerFocusLost(InputRouter input)
    {
        if (_entries.Count == 0) return false;
        input.CancelPointerCapture();
        _focusLossPending = true;
        return true;
    }

    public bool OwnsEditor(TextBox? editor)
    {
        // Keyboard focus can stay in a native menu while Tab moves the logical focus.
        // The native input owner must keep composing for that shared logical target.
        return editor is not null && _entries.Values.Any(e => e.Active && e.Window.IsFocused);
    }

    /// <summary>Updates native geometry and paints changed popup surfaces after the owner layout.</summary>
    public bool Render(Element root, InputRouter input, ElementTheme theme, bool alwaysRender = false)
    {
        PrepareLayout(root, input); // Templates can have created popups during the owner's first measure.
        if (_focusLossPending)
        {
            _focusLossPending = false;
            if (!_windows.IsOwnerFocused && !_entries.Values.Any(e => e.Window.IsFocused && !e.Window.IsClosed))
            {
                foreach (var popup in _configured.Reverse().Where(p => p.IsOpen && !p.StaysOpen).ToArray()) popup.Close();
                input.Reset();
            }
        }
        var popups = root.Descendants().Prepend(root).OfType<Popup>()
            .Where(p => p.IsWindowHosted && p.IsEffectivelyVisible && !p.IsDisposed).ToArray();
        var open = popups.ToHashSet();
        Popup? NativeParent(Popup popup)
        {
            for (var ancestor = popup.Parent; ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor is Popup parent && open.Contains(parent)) return parent;
            return null;
        }
        foreach (var entry in _entries.Values.Reverse().ToArray())
            if (!open.Contains(entry.Popup) || entry.Window.IsClosed || entry.Interactive != entry.Popup.IsHitTestVisible ||
                !ReferenceEquals(entry.ParentEntry?.Popup, NativeParent(entry.Popup)))
                Remove(entry.Popup);

        var presented = false;
        foreach (var popup in popups)
        {
            popup.Measure(root.FinalRect.Size);
            popup.UpdateWindowPlacement();
            if (popup.WindowBounds.Size.IsEmpty)
            {
                Remove(popup);
                continue;
            }
            Rect actual;
            if (!_entries.TryGetValue(popup, out var entry))
            {
                var parentPopup = NativeParent(popup);
                var parentEntry = parentPopup is not null ? _entries.GetValueOrDefault(parentPopup) : null;
                var native = _windows.Create(popup.IsHitTestVisible, parentEntry?.Window);
                try
                {
                    actual = ConfigureBounds(native, popup);
                    entry = new Entry(this, popup, native, parentEntry, theme, _text);
                    entry.Bounds = actual;
                    _entries.Add(popup, entry);
                    native.Show();
                    entry.Presenter = _surfaces.Create(native.SurfaceHandle, Math.Max(native.ClientWidth, 1), Math.Max(native.ClientHeight, 1));
                    if (native.IsTransparent && !entry.Presenter.SupportsTransparency)
                    {
                        _overlayFallback.Add(popup);
                        Remove(popup);
                        popup.SetWindowHosting(null);
                        popup.Measure(root.FinalRect.Size);
                        popup.UpdateWindowPlacement();
                        continue;
                    }
                }
                catch
                {
                    if (_entries.ContainsKey(popup)) Remove(popup);
                    else native.Dispose();
                    throw;
                }
            }
            else actual = ConfigureBounds(entry.Window, popup);
            if (!entry.Active) continue;
            entry.Bounds = actual;
            entry.Painter.ReplaceTheme(theme);
            entry.Presenter!.Resize(Math.Max(entry.Window.ClientWidth, 1), Math.Max(entry.Window.ClientHeight, 1));
            UpdateTextInput(entry, input);
            var frame = entry.Painter.BuildPopupFrame(popup, actual, entry.Window.ScaleFactor, entry.Window.IsTransparent);
            if (alwaysRender || entry.Dirty || !frame.Damage.IsEmpty)
            {
                entry.Presenter.Render(frame, entry.Window.ScaleFactor);
                entry.Dirty = false;
                presented = true;
            }
        }
        return presented;
    }

    private static Rect ConfigureBounds(IPopupWindow native, Popup popup)
    {
        var content = popup.WindowBounds;
        var visual = native.IsTransparent ? ElementPainter.GetPopupVisualBounds(popup) : content;
        var left = content.Left - visual.Left;
        var top = content.Top - visual.Top;
        var right = visual.Right - content.Right;
        var bottom = visual.Bottom - content.Bottom;
        var actual = native.SetBounds(visual);
        // At a display edge, sacrifice shadow space before squeezing the popup body.
        var horizontalSpace = Math.Max(0, actual.Width - content.Width);
        if (left + right > horizontalSpace)
        {
            var ratio = horizontalSpace / (left + right);
            left *= ratio; right *= ratio;
        }
        var verticalSpace = Math.Max(0, actual.Height - content.Height);
        if (top + bottom > verticalSpace)
        {
            var ratio = verticalSpace / (top + bottom);
            top *= ratio; bottom *= ratio;
        }
        var arranged = new Rect(actual.X + left, actual.Y + top,
            Math.Max(0, actual.Width - left - right), Math.Max(0, actual.Height - top - bottom));
        popup.ArrangeWindowContent(arranged);
        var radius = Visual.GetCornerRadius(popup) ?? popup.CornerRadius;
        if (!double.IsFinite(radius) || radius < 0) radius = 0;
        radius = Math.Min(radius, Math.Min(arranged.Width, arranged.Height) / 2);
        popup.SetWindowCornerRadius(radius);
        native.SetShape(new(left, top, arranged.Width, arranged.Height), radius);
        return actual;
    }

    private static void UpdateTextInput(Entry entry, InputRouter input)
    {
        var editor = input.Focus.Focused as TextBox;
        entry.Window.SetTextInputEnabled(entry.Window.IsFocused && editor is { IsReadOnly: false });
        if (!entry.Window.IsFocused || editor is null) return;
        var caret = editor.CaretRect;
        var zero = editor.PointFromRoot(Point.Zero);
        entry.Window.SetImeCaret(new(caret.X - zero.X - entry.Bounds.X, caret.Y - zero.Y - entry.Bounds.Y, caret.Width, caret.Height));
    }

    private void Remove(Popup popup)
    {
        if (!_entries.TryGetValue(popup, out var entry)) return;
        List<Exception>? failures = null;
        foreach (var child in _entries.Values.Where(e => ReferenceEquals(e.ParentEntry, entry)).Reverse().ToArray())
            try { Remove(child.Popup); } catch (Exception error) { (failures ??= new()).Add(error); }
        for (var captured = _input?.Captured; captured is not null; captured = captured.Parent)
            if (ReferenceEquals(captured, popup))
            {
                try { _input!.CancelPointerCapture(); } catch (Exception error) { (failures ??= new()).Add(error); }
                break;
            }
        _entries.Remove(popup);
        try { entry.Dispose(); } catch (Exception error) { (failures ??= new()).Add(error); }
        if (failures is not null) throw new AggregateException("Popup hierarchy cleanup failed.", failures);
    }

    public void Clear()
    {
        List<Exception>? failures = null;
        foreach (var popup in _entries.Keys.Reverse().ToArray())
            try { Remove(popup); } catch (Exception error) { (failures ??= new()).Add(error); }
        foreach (var popup in _configured)
            try { popup.SetWindowHosting(null); } catch (Exception error) { (failures ??= new()).Add(error); }
        _configured.Clear();
        _overlayFallback.Clear();
        _root = null;
        _input = null;
        _focusLossPending = false;
        if (failures is not null) throw new AggregateException("Popup surface cleanup failed.", failures);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }

    private sealed class Entry : IDisposable
    {
        private bool _disposed;
        public Popup Popup { get; }
        public IPopupWindow Window { get; }
        public Entry? ParentEntry { get; }
        public bool Interactive { get; }
        public ISurfacePresenter? Presenter { get; set; }
        public ElementPainter Painter { get; }
        public Rect Bounds { get; set; }
        public bool Dirty { get; set; } = true;
        public bool Active => !_disposed && !Popup.IsDisposed && Popup.IsEffectivelyVisible && !Window.IsClosed;
        private Point ToRoot(Point point) => new(point.X + Bounds.X, point.Y + Bounds.Y);

        public Entry(PopupWindowManager owner, Popup popup, IPopupWindow window, Entry? parent, ElementTheme theme, ITextPainter? text)
        {
            Popup = popup;
            Window = window;
            ParentEntry = parent;
            Interactive = popup.IsHitTestVisible;
            Bounds = popup.WindowBounds;
            Painter = new(theme, text);
            window.PointerMoved += p => { if (Active) owner._input?.PointerMove(ToRoot(p), popup); };
            window.PointerPressed += (p, m, b) => { if (Active) owner._input?.PointerPress(ToRoot(p), m, b, popup); };
            window.PointerReleased += p => { if (Active) owner._input?.PointerRelease(ToRoot(p), popup); };
            window.PointerCaptureLost += () => { if (Active) owner._input?.CancelPointerCapture(); };
            window.PointerWheel += (p, d, m) => { if (Active) owner._input?.PointerWheel(ToRoot(p), d, m, popup); };
            window.KeyDown += (k, m, r) => { if (Active) owner._input?.KeyPress(k, m, r); };
            window.KeyUp += (k, m) => { if (Active) owner._input?.KeyRelease(k, m); };
            window.TextInput += textValue => { if (Active) owner._input?.TextInput(textValue); };
            window.TextComposition += (textValue, stage) => { if (Active) owner._input?.TextComposition(textValue, stage); };
            window.FocusLost += () => { if (Active) owner._focusLossPending = true; };
            window.PaintRequested += () => { if (Active) Dirty = true; };
            window.Closed += () => { if (!_disposed && !popup.IsDisposed) popup.Close(); };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            List<Exception>? failures = null;
            try { Window.SetTextInputEnabled(false); } catch (Exception error) { (failures ??= new()).Add(error); }
            try { Presenter?.Dispose(); } catch (Exception error) { (failures ??= new()).Add(error); }
            try { Window.Dispose(); } catch (Exception error) { (failures ??= new()).Add(error); }
            if (failures is not null) throw new AggregateException("Popup window cleanup failed.", failures);
        }
    }
}
