using System.Collections;

namespace EclipseUI.Elements;

public class TreeViewItem : StackPanel
{
    public override Rect? GetDrawingClip() => new Rect(0, 0, FinalRect.Width, Math.Min(28, FinalRect.Height));
    public override Rect? GetContentClip() => new Rect(20, 28, Math.Max(0, FinalRect.Width - 20), Math.Max(0, FinalRect.Height - 28));
    public override FocusVisualGeometry GetFocusVisualGeometry() => new(this, new(4, 4, Math.Max(0, FinalRect.Width - 8), 20), 3);
    public static readonly StyledProperty<string> HeaderProperty = StyledProperty<string>.Register<TreeViewItem>("Header", "", new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<bool> IsExpandedProperty = StyledProperty<bool>.Register<TreeViewItem>("IsExpanded", false, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<bool> IsSelectedProperty = StyledProperty<bool>.Register<TreeViewItem>("IsSelected", false, new() { Invalidate = InvalidateKind.Render });
    public static readonly RoutedEvent ExpandedChangedEvent = new("ExpandedChanged");
    public string Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public object? Item { get; set; }
    public override bool IsFocusable => true;
    public override bool UsesDefaultDrawing => false;
    public TreeViewItem()
    {
        PropertyChanged += (_, p) => { if (p == IsExpandedProperty) { UpdateChildren(); InvalidateRender(); RaiseEvent(new(ExpandedChangedEvent)); } };
        AddHandler(PointerPressedEvent, e =>
        {
            if (e is not PointerEventArgs p || !ReferenceEquals(e.Source, this) || PointFromRoot(p.Position).Y > 28) return;
            if (PointFromRoot(p.Position).X < 20 && Children.Count > 0) IsExpanded = !IsExpanded;
            FindTree()?.Select(this); e.Handled = true;
        });
        AddHandler(KeyDownEvent, e =>
        {
            if (e is not KeyEventArgs k || !ReferenceEquals(e.Source, this)) return;
            if (k.Key == Key.Right) { if (!IsExpanded && Children.Count > 0) IsExpanded = true; else if (Children.OfType<TreeViewItem>().FirstOrDefault() is { } first) { FindTree()?.Select(first); first.RequestFocus(); } }
            else if (k.Key == Key.Left) { if (IsExpanded) IsExpanded = false; else if (Parent is TreeViewItem parent) { FindTree()?.Select(parent); parent.RequestFocus(); } }
            else if (k.Key is Key.Space or Key.Enter) FindTree()?.Select(this);
            else return;
            e.Handled = true;
        });
    }
    private TreeView? FindTree() { for (var p = Parent; p is not null; p = p.Parent) if (p is TreeView tree) return tree; return null; }
    private void UpdateChildren() { foreach (var child in Children) child.IsVisible = IsExpanded; }
    protected internal override void OnChildrenChanged() { UpdateChildren(); base.OnChildrenChanged(); }
    protected internal override Size MeasureCore(Size available)
    {
        var size = TextMeasurer.Current.Measure(Header, GetValue(TextBlock.FontSizeProperty));
        double w = size.Width + 28, h = 28;
        foreach (var child in Children)
        {
            child.Measure(new(Math.Max(0, available.Width - 20), double.PositiveInfinity));
            if (IsExpanded) { w = Math.Max(w, child.DesiredSize.Width + 20); h += child.DesiredSize.Height; }
        }
        return new(w, h);
    }
    protected internal override void ArrangeCore(Rect final)
    {
        double y = 28;
        foreach (var child in Children) { child.Arrange(new(20, y, Math.Max(0, final.Width - 20), IsExpanded ? child.DesiredSize.Height : 0)); if (IsExpanded) y += child.DesiredSize.Height; }
    }
    public override void Render(IElementDrawingContext c)
    {
        c.FillRectangle(new(0, 0, FinalRect.Width, 28), IsSelected ? c.SelectionBackground : c.Background, c.CornerRadius);
        var fg = IsSelected ? c.SelectionForeground : c.Foreground;
        if (IsSelected) c.FillRectangle(new(3, 6, 3, 16), c.Accent, 1.5);
        if (Children.Count > 0) ControlIcons.Chevron(c, new(10, 14), right: !IsExpanded);
        c.Text(Header, new(24, 4), GetValue(TextBlock.FontSizeProperty), fg);
    }
}

public class TreeView : StackPanel
{
    public override Rect? GetContentClip() => new Rect(Padding, Padding, Math.Max(0, FinalRect.Width - Padding * 2), Math.Max(0, FinalRect.Height - Padding * 2));
    public override FocusVisualGeometry GetFocusVisualGeometry() => _selected is { IsEffectivelyVisible: true }
        ? _selected.GetFocusVisualGeometry() : base.GetFocusVisualGeometry();
    public static readonly RoutedEvent SelectionChangedEvent = new("SelectionChanged");
    private TreeViewItem? _selected;
    private IEnumerable? _source;
    private System.Collections.Specialized.INotifyCollectionChanged? _observable;
    public TreeViewItem? SelectedItem { get => _selected; set => Select(value); }
    public Action<TreeViewItem?>? SelectedItemChanged { get; set; }
    public IEnumerable? ItemsSource
    {
        get => _source;
        set
        {
            if (_observable is not null) _observable.CollectionChanged -= SourceChanged;
            _source = value; _observable = value as System.Collections.Specialized.INotifyCollectionChanged;
            if (_observable is not null) _observable.CollectionChanged += SourceChanged;
            RefreshItems();
        }
    }
    private Func<object?, IEnumerable?>? _childrenSelector;
    private Func<object?, string>? _headerSelector;
    public Func<object?, IEnumerable?>? ChildrenSelector { get => _childrenSelector; set { _childrenSelector = value; if (_source is not null) RefreshItems(); } }
    public Func<object?, string>? HeaderSelector { get => _headerSelector; set { _headerSelector = value; if (_source is not null) RefreshItems(); } }
    public override bool IsVisualTransparent => true;
    public override bool IsFocusable => true;
    public IReadOnlyList<TreeViewItem> VisibleItems => Descendants().OfType<TreeViewItem>().Where(i => i.IsEffectivelyVisible).ToArray();
    public TreeView() => AddHandler(KeyDownEvent, e =>
    {
        if (e is not KeyEventArgs k) return;
        var items = VisibleItems; if (items.Count == 0) return;
        var index = _selected is null ? -1 : items.ToList().IndexOf(_selected);
        switch (k.Key)
        {
            case Key.Down: index = Math.Min(items.Count - 1, index + 1); break;
            case Key.Up: index = Math.Max(0, index - 1); break;
            case Key.Home: index = 0; break;
            case Key.End: index = items.Count - 1; break;
            default: return;
        }
        Select(items[index]); items[index].RequestFocus(); e.Handled = true;
    });
    public void Select(TreeViewItem? item)
    {
        if (ReferenceEquals(_selected, item)) return;
        if (item is not null && !Descendants().Contains(item)) throw new ArgumentException("Selected item must belong to this tree.", nameof(item));
        if (_selected is not null) _selected.IsSelected = false;
        _selected = item; if (item is not null) item.IsSelected = true;
        SelectedItemChanged?.Invoke(item); RaiseEvent(new(SelectionChangedEvent));
    }
    private void SourceChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshItems();
    public void RefreshItems()
    {
        if (_source is null) return;
        var active = new HashSet<object>(ReferenceEqualityComparer.Instance);
        TreeViewItem Build(object? item, int depth)
        {
            if (depth > 256 || item is not null && !active.Add(item)) throw new InvalidOperationException("Tree data contains a cycle or exceeds 256 levels.");
            var node = new TreeViewItem { Item = item, Header = HeaderSelector?.Invoke(item) ?? item?.ToString() ?? "" };
            try { if (ChildrenSelector?.Invoke(item) is { } children) foreach (var child in children) node.Children.Add(Build(child, depth + 1)); }
            catch { node.Dispose(); throw; }
            finally { if (item is not null) active.Remove(item); }
            return node;
        }
        var next = new List<Element>();
        try { foreach (var item in _source) next.Add(Build(item, 0)); }
        catch { foreach (var item in next) item.Dispose(); throw; }
        Select(null); var old = Children.ToArray(); Children.ReplaceAll(next); foreach (var item in old) item.Dispose();
    }
    protected override void OnDispose() { if (_observable is not null) _observable.CollectionChanged -= SourceChanged; base.OnDispose(); }
}
