using System.Collections;

namespace EclipseUI.Elements;

public class TreeViewItem : StackPanel
{
    public override Rect? GetContentClip() => new Rect(Point.Zero, FinalRect.Size);
    public override FocusVisualGeometry GetFocusVisualGeometry() => new(this, new(0, 0, FinalRect.Width, HeaderHeight), 0);
    public static readonly StyledProperty<string> HeaderProperty = StyledProperty<string>.Register<TreeViewItem>("Header", "", new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<bool> IsExpandedProperty = StyledProperty<bool>.Register<TreeViewItem>("IsExpanded", false, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<bool> IsSelectedProperty = StyledProperty<bool>.Register<TreeViewItem>("IsSelected", false, new() { Invalidate = InvalidateKind.Render });
    public static readonly RoutedEvent ExpandedChangedEvent = new("ExpandedChanged");
    public string Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public object? Item { get; set; }
    public override bool IsFocusable => true;
    public static readonly StyledProperty<double> HeaderHeightProperty = StyledProperty<double>.Register<TreeViewItem>(nameof(HeaderHeight), 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> IndentProperty = StyledProperty<double>.Register<TreeViewItem>(nameof(Indent), 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> HeaderPaddingProperty = StyledProperty<double>.Register<TreeViewItem>(nameof(HeaderPadding), 0, new() { Invalidate = InvalidateKind.Measure });
    public double HeaderHeight { get => GetValue(HeaderHeightProperty); set => SetValue(HeaderHeightProperty, Math.Max(0, value)); }
    public double Indent { get => GetValue(IndentProperty); set => SetValue(IndentProperty, Math.Max(0, value)); }
    public double HeaderPadding { get => GetValue(HeaderPaddingProperty); set => SetValue(HeaderPaddingProperty, Math.Max(0, value)); }
    public TreeViewItem()
    {
        PropertyChanged += (_, p) => { if (p == IsExpandedProperty) { UpdateChildren(); InvalidateRender(); RaiseEvent(new(ExpandedChangedEvent)); } };
        AddHandler(PointerPressedEvent, e =>
        {
            if (e is not PointerEventArgs p || !IsOwnHeaderSource(e.Source) || PointFromRoot(p.Position).Y > HeaderHeight) return;
            if (PointFromRoot(p.Position).X < Indent && Children.Count > 0) IsExpanded = !IsExpanded;
            FindTree()?.Select(this); e.Handled = true;
        });
        AddHandler(KeyDownEvent, e =>
        {
            if (e is not KeyEventArgs k || !ReferenceEquals(e.Source, this)) return;
            if (k.Key == Key.Right) { if (!IsExpanded && Children.Count > 0) IsExpanded = true; else if (Children.OfType<TreeViewItem>().FirstOrDefault() is { } first) { FindTree()?.Select(first); first.RequestFocus(); } }
            else if (k.Key == Key.Left) { if (IsExpanded) IsExpanded = false; else if (FindParentItem() is { } parent) { FindTree()?.Select(parent); parent.RequestFocus(); } }
            else if (k.Key is Key.Space or Key.Enter) FindTree()?.Select(this);
            else return;
            e.Handled = true;
        });
    }
    private bool IsOwnHeaderSource(Element? source)
    {
        for (var element = source; element is not null && !ReferenceEquals(element, this); element = element.Parent)
            if (element is TreeViewItem) return false;
        return source is not null;
    }
    private TreeViewItem? FindParentItem() { for (var p = Parent; p is not null; p = p.Parent) if (p is TreeViewItem item) return item; return null; }
    private TreeView? FindTree() { for (var p = Parent; p is not null; p = p.Parent) if (p is TreeView tree) return tree; return null; }
    private void UpdateChildren() { foreach (var child in Children) child.IsVisible = IsExpanded; }
    protected internal override void OnChildrenChanged() { UpdateChildren(); base.OnChildrenChanged(); }
    protected internal override Size MeasureCore(Size available)
    {
        var size = TextMeasurer.Current.Measure(Header, GetValue(TextBlock.FontSizeProperty));
        double w = size.Width + HeaderPadding, h = HeaderHeight;
        foreach (var child in Children)
        {
            child.Measure(new(Math.Max(0, available.Width - Indent), double.PositiveInfinity));
            if (IsExpanded) { w = Math.Max(w, child.DesiredSize.Width + Indent); h += child.DesiredSize.Height; }
        }
        return new(w, h);
    }
    protected internal override void ArrangeCore(Rect final)
    {
        double y = HeaderHeight;
        foreach (var child in Children) { child.Arrange(new(Indent, y, Math.Max(0, final.Width - Indent), IsExpanded ? child.DesiredSize.Height : 0)); if (IsExpanded) y += child.DesiredSize.Height; }
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
    private readonly HashSet<Element> _generatedRoots = new(ReferenceEqualityComparer.Instance);
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
        if (_source is null)
        {
            var removed = Children.Where(_generatedRoots.Contains).ToArray();
            if (_selected is not null && removed.Any(root => ReferenceEquals(root, _selected) || root.Descendants().Contains(_selected))) Select(null);
            foreach (var root in removed) { Children.Remove(root); root.Dispose(); }
            _generatedRoots.Clear();
            return;
        }
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
        Select(null); var old = Children.ToArray(); Children.ReplaceAll(next);
        _generatedRoots.Clear(); _generatedRoots.UnionWith(next);
        foreach (var item in old) item.Dispose();
    }
    protected override void OnDispose() { if (_observable is not null) _observable.CollectionChanged -= SourceChanged; base.OnDispose(); }
}
