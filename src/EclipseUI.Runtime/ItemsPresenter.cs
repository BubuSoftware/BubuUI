namespace EclipseUI.Elements;

/// <summary>Projects a panel's behavior-owned items into a template without transferring their lifetime.</summary>
public class ItemsPresenter : Element
{
    private Panel? _owner;
    private bool _active;
    public bool UseOwnerLayout { get; set; } = true;
    public Panel? Owner
    {
        get => _owner;
        set
        {
            if (ReferenceEquals(_owner, value)) return;
            ReleaseContent();
            _owner = value;
            if (value is not null && !value.IsBuildingTemplate && IsAttached) ActivateContent();
            InvalidateMeasure();
        }
    }
    public override IReadOnlyList<Element> VisualChildren => _active && _owner is not null ? _owner.Children : Array.Empty<Element>();
    public override Rect? GetContentClip()
    {
        if (!UseOwnerLayout || _owner?.GetContentClip() is not { } clip) return base.GetContentClip();
        var presenterOrigin = PointFromRoot(Point.Zero);
        var ownerOrigin = _owner.PointFromRoot(Point.Zero);
        return new(clip.X + presenterOrigin.X - ownerOrigin.X, clip.Y + presenterOrigin.Y - ownerOrigin.Y,
            clip.Width, clip.Height);
    }
    internal void ActivateContent()
    {
        if (_active || _owner is null) return;
        _owner.Children.ProjectTo(this);
        _active = true;
        InvalidateMeasure();
    }
    internal void ReleaseContent()
    {
        if (!_active || _owner is null) return;
        _active = false;
        _owner.Children.ProjectTo(null);
    }
    protected override void OnAttached() { base.OnAttached(); if (_owner?.IsBuildingTemplate != true) ActivateContent(); }
    protected internal override Size MeasureCore(Size available)
    {
        if (_owner is null) return Size.Empty;
        if (UseOwnerLayout) return _owner.MeasureContent(available);
        double width = 0, height = 0;
        foreach (var child in _owner.Children)
        {
            child.Measure(available);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new(width, height);
    }
    protected internal override void ArrangeCore(Rect final)
    {
        if (_owner is null) return;
        if (UseOwnerLayout) _owner.ArrangeContent(new(Point.Zero, final.Size));
        else foreach (var child in _owner.Children) child.Arrange(new(Point.Zero, final.Size));
    }
    public override void Dispose() { ReleaseContent(); base.Dispose(); }
}
