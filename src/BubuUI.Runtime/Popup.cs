using System.Runtime.CompilerServices;

namespace BubuUI.Elements;

public enum Placement { Bottom, Top, Left, Right, Pointer }
public enum PopupHostMode { Auto, Overlay, Window }

/// <summary>Content remains in the owner tree; desktop hosts can present it in an owned native window.</summary>
public class Popup : ContentControl
{
    protected override bool UsesTemplateLayout => false;
    // Popup is a zero-size anchor; its real content is arranged outside the anchor.
    public override Rect? GetDrawingClip() => null;
    public override Rect? GetContentClip() => (TemplateRoot ?? Content)?.FinalRect ?? new Rect(Point.Zero, Size.Empty);
    public static readonly StyledProperty<bool> IsOpenProperty = StyledProperty<bool>.Register<Popup>("IsOpen", false, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<bool> IsModalProperty = StyledProperty<bool>.Register<Popup>("IsModal", false, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<bool> StaysOpenProperty = StyledProperty<bool>.Register<Popup>("StaysOpen", false, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<Placement> PlacementProperty = StyledProperty<Placement>.Register<Popup>("Placement", Placement.Bottom, new() { Invalidate = InvalidateKind.Arrange });
    public static readonly StyledProperty<PopupHostMode> HostModeProperty = StyledProperty<PopupHostMode>.Register<Popup>("HostMode", PopupHostMode.Auto, new() { Invalidate = InvalidateKind.Measure });
    public static readonly RoutedEvent OpenedEvent = new("Opened");
    public static readonly RoutedEvent ClosedEvent = new("Closed");
    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    /// <summary>Restricts owner input and focus to this popup and its owned popup content while open.</summary>
    public bool IsModal { get => GetValue(IsModalProperty); set => SetValue(IsModalProperty, value); }
    public bool StaysOpen { get => GetValue(StaysOpenProperty); set => SetValue(StaysOpenProperty, value); }
    public Placement Placement { get => GetValue(PlacementProperty); set => SetValue(PlacementProperty, value); }
    /// <summary>Auto uses a native popup when the desktop host supports it. Overlay keeps the popup inside its owner surface.</summary>
    public PopupHostMode HostMode { get => GetValue(HostModeProperty); set => SetValue(HostModeProperty, value); }
    public bool IsWindowHosted => _windowAvailableSize is not null;
    private Size? _windowAvailableSize;
    private double _windowCornerRadius;
    internal Rect WindowBounds { get; private set; }
    internal void SetWindowCornerRadius(double radius) => _windowCornerRadius = radius;
    internal void SetWindowHosting(Size? availableSize)
    {
        if (_windowAvailableSize == availableSize) return;
        _windowAvailableSize = availableSize;
        if (availableSize is null) _windowCornerRadius = 0;
        if (IsDisposed) return;
        InvalidateMeasure();
        InvalidateRender();
    }
    // A toggle button handles dismissal on Click, after the pointer-press light-dismiss pass.
    internal Element? LightDismissExclusion { get; set; }
    private Element? _placementTarget;
    private Point _pointerPosition;
    private double _horizontalOffset, _verticalOffset;
    public Element? PlacementTarget
    {
        get => _placementTarget;
        set
        {
            if (ReferenceEquals(_placementTarget, value)) return;
            _placementTarget = value;
            InvalidateModalCache();
            FindFocusManager()?.EnsureModalFocus();
            InvalidateArrange();
        }
    }
    public Point PointerPosition { get => _pointerPosition; set { _pointerPosition = value; InvalidateArrange(); } }
    public double HorizontalOffset { get => _horizontalOffset; set { _horizontalOffset = value; InvalidateArrange(); } }
    public double VerticalOffset { get => _verticalOffset; set { _verticalOffset = value; InvalidateArrange(); } }
    private static long _nextOpenOrder;
    private static long _modalEpoch;
    private static readonly ConditionalWeakTable<Element, ModalCache> ModalCaches = new();
    private sealed class ModalCache { public long Epoch = -1; public Popup? Modal; }
    internal static void InvalidateModalCache() => Interlocked.Increment(ref _modalEpoch);
    private WeakReference<Element>? _previousFocus;
    private WeakReference<FocusManager>? _openingFocus;
    internal long OpenOrder { get; private set; }
    internal Element? PreviousFocus => _previousFocus?.TryGetTarget(out var previous) == true ? previous : null;

    public Popup()
    {
        PropertyChanged += (_, property) =>
        {
            if (property == IsModalProperty)
            {
                InvalidateModalCache();
                if (IsOpen && IsModal) OpenOrder = Interlocked.Increment(ref _nextOpenOrder);
                FindFocusManager()?.EnsureModalFocus();
                return;
            }
            if (property != IsOpenProperty) return;
            InvalidateModalCache();
            InvalidateRender();
            var focus = FindFocusManager();
            if (IsOpen)
            {
                OpenOrder = Interlocked.Increment(ref _nextOpenOrder);
                _previousFocus = focus?.Focused is { } previous ? new(previous) : null;
                _openingFocus = focus is null ? null : new(focus);
                focus?.EnsureModalFocus();
            }
            else
            {
                if (IsModal)
                {
                    // The focus manager unwinds nested modal history, including closed
                    // or detached intermediate popups whose focus can no longer be restored.
                    focus?.EnsureModalFocus();
                    _previousFocus = null;
                    _openingFocus = null;
                }
                else RestoreFocus();
                focus?.RefreshFocusPath();
            }
            RaiseEvent(new(IsOpen ? OpenedEvent : ClosedEvent));
        };
    }

    internal static Popup? TopModal(Element root)
    {
        if (root.IsDisposed) return null;
        var epoch = Volatile.Read(ref _modalEpoch);
        var cache = ModalCaches.GetValue(root, static _ => new ModalCache());
        if (cache.Epoch == epoch) return cache.Modal;
        var modals = root.Descendants().Prepend(root).OfType<Popup>()
            .Where(p => p.IsModal && p.IsOpen && !p.IsDisposed && p.IsEffectivelyVisible)
            .OrderByDescending(p => p.OpenOrder).ToArray();
        Popup? result = null;
        foreach (var candidate in modals)
        {
            if (result is null) { result = candidate; continue; }
            // Compare activation branches. A nested modal wins over its ancestor even
            // if it was configured IsOpen before the ancestor became visible.
            foreach (var activation in modals)
            {
                var candidateOwned = activation.IsInModalScope(candidate);
                var resultOwned = activation.IsInModalScope(result);
                if (candidateOwned == resultOwned) continue;
                if (candidateOwned) result = candidate;
                break;
            }
        }
        cache.Modal = result;
        cache.Epoch = epoch;
        return result;
    }

    internal bool IsInModalScope(Element? target)
    {
        for (var node = target; node is not null; node = node.Parent)
            if (ReferenceEquals(node, this)) return true;
        for (var node = target; node is not null; node = node.Parent)
            if (node is Popup { PlacementTarget: not null })
                return ContainsOwnedElement(target!, new HashSet<Popup>());
        return false;
    }

    private bool ContainsOwnedElement(Element target, HashSet<Popup> visited)
    {
        for (Element? node = target; node is not null; node = node.Parent)
        {
            if (ReferenceEquals(node, this)) return true;
            // Context menus can be declared beside their target instead of inside its tree.
            if (node is Popup popup && visited.Add(popup) && popup.PlacementTarget is { } placement &&
                ContainsOwnedElement(placement, visited)) return true;
        }
        return false;
    }

    private void RestoreFocus()
    {
        var focus = FindFocusManager();
        if (focus is null && _openingFocus?.TryGetTarget(out var opening) == true) focus = opening;
        var previous = _previousFocus;
        _previousFocus = null;
        _openingFocus = null;
        if (focus?.Focused is { } current && IsInModalScope(current))
        {
            focus.ClearFocus();
            if (previous?.TryGetTarget(out var target) == true) focus.Focus(target, FocusOrigin.Restore);
        }
        focus?.EnsureModalFocus();
    }

    protected override void OnDetached()
    {
        base.OnDetached();
        if (_openingFocus?.TryGetTarget(out var focus) == true) focus.EnsureModalFocus();
    }

    protected override void OnDispose()
    {
        InvalidateModalCache();
        if (_openingFocus?.TryGetTarget(out var focus) == true) focus.EnsureModalFocus();
        _previousFocus = null;
        _openingFocus = null;
        base.OnDispose();
    }
    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;
    protected internal override Size MeasureCore(Size available)
    {
        var root = this as Element; while (root.Parent is not null) root = root.Parent;
        var availableSize = _windowAvailableSize ?? new Size(root.FinalRect.Width > 0 ? root.FinalRect.Width : 480,
            root.FinalRect.Height > 0 ? root.FinalRect.Height : 480);
        (TemplateRoot ?? Content)?.Measure(new(Math.Min(availableSize.Width, MaxWidth), Math.Min(availableSize.Height, MaxHeight)));
        return Size.Empty;
    }
    protected internal override void ArrangeCore(Rect final)
    {
        var body = TemplateRoot ?? Content;
        if (!IsOpen || body is null) return;
        var root = this as Element;
        while (root.Parent is not null) root = root.Parent;
        var target = PlacementTarget ?? Parent;
        var origin = target is null ? Point.Zero : RootOrigin(target);
        var size = body.DesiredSize;
        var p = Placement switch
        {
            Placement.Top => new Point(origin.X, origin.Y - size.Height),
            Placement.Left => new Point(origin.X - size.Width, origin.Y),
            Placement.Right => new Point(origin.X + (target?.FinalRect.Width ?? 0), origin.Y),
            Placement.Pointer => PointerPosition,
            _ => new Point(origin.X, origin.Y + (target?.FinalRect.Height ?? 0)),
        };
        var position = new Point(p.X + HorizontalOffset, p.Y + VerticalOffset);
        if (!IsWindowHosted)
            position = new(Math.Clamp(position.X, 0, Math.Max(0, root.FinalRect.Width - size.Width)),
                Math.Clamp(position.Y, 0, Math.Max(0, root.FinalRect.Height - size.Height)));
        WindowBounds = new(position, size);
        body.Arrange(new(PointFromRoot(position), size));
    }
    internal void ArrangeWindowContent(Rect bounds)
    {
        WindowBounds = bounds;
        (TemplateRoot ?? Content)?.Arrange(new(PointFromRoot(bounds.Location), bounds.Size));
    }
    internal void UpdateWindowPlacement() => ArrangeCore(FinalRect);
    internal static Point RootOrigin(Element element)
    {
        double x = 0, y = 0;
        for (Element? e = element; e is not null; e = e.Parent) { x += e.FinalRect.X; y += e.FinalRect.Y; }
        return new(x, y);
    }
    internal bool IsLightDismissExcluded(Element? hit)
    {
        if (LightDismissExclusion is not { } exclusion) return false;
        for (var element = hit; element is not null; element = element.Parent)
            if (ReferenceEquals(element, exclusion)) return true;
        return false;
    }
    public bool ContainsRootPoint(Point point)
    {
        if (!IsOpen || (TemplateRoot ?? Content) is not { } body) return false;
        if (IsWindowHosted && !WindowBounds.Contains(point)) return false;
        var p = body.PointFromRoot(point);
        var size = body.FinalRect.Size;
        if (!new Rect(Point.Zero, size).Contains(p)) return false;
        var radius = Math.Min(_windowCornerRadius, Math.Min(size.Width, size.Height) / 2);
        if (!IsWindowHosted || radius <= 0) return true;
        var x = p.X - Math.Clamp(p.X, radius, size.Width - radius);
        var y = p.Y - Math.Clamp(p.Y, radius, size.Height - radius);
        return x * x + y * y <= radius * radius;
    }
}

public class ToolTip : Popup
{
    private Element? _target;
    public Element? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value)) return;
            if (_target is not null) { _target.RemoveHandler(PointerEnteredEvent, Enter); _target.RemoveHandler(PointerExitedEvent, Exit); }
            _target = PlacementTarget = value;
            if (value is not null) { value.AddHandler(PointerEnteredEvent, Enter); value.AddHandler(PointerExitedEvent, Exit); }
        }
    }
    public ToolTip() { StaysOpen = true; IsHitTestVisible = false; }
    private void Enter(RoutedEventArgs _) => Open();
    private void Exit(RoutedEventArgs _) => Close();
    protected override void OnDispose() { Target = null; base.OnDispose(); }
}
