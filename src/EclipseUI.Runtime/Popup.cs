namespace EclipseUI.Elements;

public enum Placement { Bottom, Top, Left, Right, Pointer }

/// <summary>Content remains in the owner tree; hosts paint/hit-test open popups above normal content.</summary>
public class Popup : ContentControl
{
    public static readonly StyledProperty<bool> IsOpenProperty = StyledProperty<bool>.Register<Popup>("IsOpen", false, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<bool> StaysOpenProperty = StyledProperty<bool>.Register<Popup>("StaysOpen", false, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<Placement> PlacementProperty = StyledProperty<Placement>.Register<Popup>("Placement", Placement.Bottom, new() { Invalidate = InvalidateKind.Arrange });
    public static readonly RoutedEvent OpenedEvent = new("Opened");
    public static readonly RoutedEvent ClosedEvent = new("Closed");
    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public bool StaysOpen { get => GetValue(StaysOpenProperty); set => SetValue(StaysOpenProperty, value); }
    public Placement Placement { get => GetValue(PlacementProperty); set => SetValue(PlacementProperty, value); }
    private Element? _placementTarget;
    private Point _pointerPosition;
    private double _horizontalOffset, _verticalOffset;
    public Element? PlacementTarget { get => _placementTarget; set { _placementTarget = value; InvalidateArrange(); } }
    public Point PointerPosition { get => _pointerPosition; set { _pointerPosition = value; InvalidateArrange(); } }
    public double HorizontalOffset { get => _horizontalOffset; set { _horizontalOffset = value; InvalidateArrange(); } }
    public double VerticalOffset { get => _verticalOffset; set { _verticalOffset = value; InvalidateArrange(); } }
    private WeakReference<Element>? _previousFocus;
    public Popup()
    {
        PropertyChanged += (_, property) =>
        {
            if (property == IsOpenProperty)
            {
                InvalidateRender();
                var root = this as Element; while (root.Parent is not null) root = root.Parent;
                var focus = root._focusManager;
                if (IsOpen) _previousFocus = focus?.Focused is { } previous ? new(previous) : null;
                else
                {
                    var previous = _previousFocus; _previousFocus = null;
                    if (focus?.Focused is { } current && Descendants().Contains(current))
                    {
                        focus.ClearFocus();
                        if (previous?.TryGetTarget(out var target) == true) focus.Focus(target, FocusOrigin.Restore);
                    }
                    focus?.RefreshFocusPath();
                }
                RaiseEvent(new(IsOpen ? OpenedEvent : ClosedEvent));
            }
        };
    }
    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;
    protected internal override Size MeasureCore(Size available)
    {
        var root = this as Element; while (root.Parent is not null) root = root.Parent;
        Content?.Measure(new(Math.Min(root.FinalRect.Width > 0 ? root.FinalRect.Width : 480, MaxWidth),
            Math.Min(root.FinalRect.Height > 0 ? root.FinalRect.Height : 480, MaxHeight)));
        return Size.Empty;
    }
    protected internal override void ArrangeCore(Rect final)
    {
        if (!IsOpen || Content is null) return;
        var root = this as Element;
        while (root.Parent is not null) root = root.Parent;
        var target = PlacementTarget ?? Parent;
        var origin = target is null ? Point.Zero : RootOrigin(target);
        var size = Content.DesiredSize;
        var p = Placement switch
        {
            Placement.Top => new Point(origin.X, origin.Y - size.Height),
            Placement.Left => new Point(origin.X - size.Width, origin.Y),
            Placement.Right => new Point(origin.X + (target?.FinalRect.Width ?? 0), origin.Y),
            Placement.Pointer => PointerPosition,
            _ => new Point(origin.X, origin.Y + (target?.FinalRect.Height ?? 0)),
        };
        var local = PointFromRoot(new(Math.Clamp(p.X + HorizontalOffset, 0, Math.Max(0, root.FinalRect.Width - size.Width)),
            Math.Clamp(p.Y + VerticalOffset, 0, Math.Max(0, root.FinalRect.Height - size.Height))));
        Content.Arrange(new(local, size));
    }
    internal static Point RootOrigin(Element element)
    {
        double x = 0, y = 0;
        for (Element? e = element; e is not null; e = e.Parent) { x += e.FinalRect.X; y += e.FinalRect.Y; }
        return new(x, y);
    }
    public bool ContainsRootPoint(Point point) => IsOpen && Content is not null &&
        new Rect(Point.Zero, Content.FinalRect.Size).Contains(Content.PointFromRoot(point));
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
