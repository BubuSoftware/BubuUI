namespace EclipseUI.Elements;

public class ComboBox : Panel
{
    public override Rect? GetContentClip() => new Rect(Point.Zero, FinalRect.Size);
    protected override FocusVisualMode DefaultFocusVisualMode => FocusVisualMode.Control;
    public override FocusVisualGeometry GetFocusVisualGeometry() => new(this, new(Point.Zero, FinalRect.Size), 4);
    public override void RenderFocusVisual(IElementDrawingContext c) =>
        c.StrokeRectangle(new(.5, .5, Math.Max(0, FinalRect.Width - 1), Math.Max(0, FinalRect.Height - 1)), c.Accent, 1, c.CornerRadius);
    private readonly Button _button;
    private readonly ListBox _list;
    private readonly Popup _popup;
    public static readonly StyledProperty<int> SelectedIndexProperty = StyledProperty<int>.Register<ComboBox>("SelectedIndex", -1, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<string> PlaceholderProperty = StyledProperty<string>.Register<ComboBox>("Placeholder", "Select…", new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<bool> IsDropDownOpenProperty = StyledProperty<bool>.Register<ComboBox>("IsDropDownOpen", false, new() { Invalidate = InvalidateKind.Measure });
    public static readonly RoutedEvent SelectionChangedEvent = new("SelectionChanged");
    public System.Collections.ObjectModel.ObservableCollection<object?> Items => _list.Items;
    public System.Collections.IEnumerable? ItemsSource { get => _list.ItemsSource; set { _list.ItemsSource = value; UpdateSelection(); } }
    public Func<object?, Element>? ItemTemplate { get => _list.ItemTemplate; set => _list.ItemTemplate = value; }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public object? SelectedItem { get => SelectedIndex >= 0 && SelectedIndex < _list.ItemValues.Count ? _list.ItemValues[SelectedIndex] : null; set => SelectedIndex = _list.ItemValues.ToList().FindIndex(i => Equals(i, value)); }
    public string Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public bool IsDropDownOpen { get => GetValue(IsDropDownOpenProperty); set => SetValue(IsDropDownOpenProperty, value); }
    public override bool IsFocusable => true;
    private bool _sync;
    public ComboBox()
    {
        _button = new DropDownButton { IsTabStop = false, FocusVisualOwner = this }; _list = new ListBox();
        var scroll = new ScrollViewer { MaxHeight = 240 }; scroll.Children.Add(_list);
        _popup = new Popup { PlacementTarget = this, Content = new Border { Content = scroll, Padding = 2 } };
        Children.Add(_button); Children.Add(_popup); MinWidth = 100;
        _button.AddHandler(Button.ClickEvent, e => { IsDropDownOpen = !IsDropDownOpen; e.Handled = true; });
        _popup.AddHandler(Popup.ClosedEvent, _ => { if (IsDropDownOpen) IsDropDownOpen = false; });
        _list.AddHandler(ListBox.SelectionChangedEvent, e => { if (!_sync) SelectedIndex = _list.SelectedIndex; e.Handled = true; });
        _list.AddHandler(PointerReleasedEvent, _ => IsDropDownOpen = false);
        _list.AddHandler(KeyDownEvent, e => { if (e is KeyEventArgs { Key: Key.Enter }) { IsDropDownOpen = false; e.Handled = true; } });
        Items.CollectionChanged += (_, _) => { UpdateSelection(); };
        PropertyChanged += (_, p) =>
        {
            if (p == SelectedIndexProperty || p == PlaceholderProperty) UpdateSelection();
            if (p == IsDropDownOpenProperty) { _popup.IsOpen = IsDropDownOpen; if (IsDropDownOpen) { _list.MinWidth = Math.Max(100, FinalRect.Width); _popup.InvalidateMeasure(); _list.RequestFocus(); } }
        };
        AddHandler(KeyDownEvent, e =>
        {
            if (e is not KeyEventArgs k) return;
            switch (k.Key)
            {
                case Key.Space: case Key.Enter: if (ReferenceEquals(e.Source, _button)) return; IsDropDownOpen = !IsDropDownOpen; break;
                case Key.Down when k.Modifiers.HasFlag(KeyModifiers.Alt): IsDropDownOpen = true; break;
                case Key.Down: SelectedIndex = Math.Min(_list.ItemValues.Count - 1, SelectedIndex + 1); break;
                case Key.Up: SelectedIndex = Math.Max(0, SelectedIndex - 1); break;
                case Key.Escape: IsDropDownOpen = false; break;
                default: return;
            }
            e.Handled = true;
        });
        UpdateSelection();
    }
    private void UpdateSelection()
    {
        if (_sync) return;
        _sync = true;
        try { _list.SelectedIndex = SelectedIndex; _button.Label = SelectedItem?.ToString() ?? Placeholder; }
        finally { _sync = false; }
        RaiseEvent(new(SelectionChangedEvent));
    }
    protected internal override Size MeasureCore(Size available) { _button.Measure(available); _popup.Measure(available); return _button.DesiredSize; }
    protected internal override void ArrangeCore(Rect final) { _button.Arrange(new(0, 0, final.Width, final.Height)); _popup.Arrange(new(0, 0, 0, 0)); }
}

public class Menu : StackPanel
{
    public override Rect? GetContentClip() => new Rect(Padding, Padding, Math.Max(0, FinalRect.Width - Padding * 2), Math.Max(0, FinalRect.Height - Padding * 2));
    public Menu()
    {
        Orientation = Orientation.Horizontal;
        AddHandler(KeyDownEvent, e =>
        {
            if (e is not KeyEventArgs k || k.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) return;
            var items = Children.OfType<MenuItem>().Where(i => i.IsEffectiveEnabled).ToArray(); if (items.Length == 0) return;
            var current = Array.FindIndex(items, i => ReferenceEquals(i, e.Source) || i.Descendants().Any(c => ReferenceEquals(c, e.Source)));
            var next = k.Key == Key.Home ? 0 : k.Key == Key.End ? items.Length - 1 : (current + (k.Key is Key.Left or Key.Up ? -1 : 1) + items.Length) % items.Length;
            var root = this as Element; while (root.Parent is not null) root = root.Parent;
            // Focus requests are routed to the active input manager.
            items[next].RequestFocus(); e.Handled = true;
        });
    }
}
[UiContent(UiContentKind.Children, Target = nameof(Items))]
public class ContextMenu : Popup
{
    private readonly Menu _menu = new() { Orientation = Orientation.Vertical };
    private Element? _target;
    public ElementCollection Items => _menu.Children;
    public Element? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value)) return;
            _target?.RemoveHandler(PointerPressedEvent, TargetPressed); _target = value; PlacementTarget = value;
            value?.AddHandler(PointerPressedEvent, TargetPressed);
        }
    }
    public ContextMenu() => Content = new Border { Content = _menu, Padding = 3 };
    private void TargetPressed(RoutedEventArgs e)
    {
        if (e is not PointerEventArgs { Button: PointerButton.Right } p) return;
        Placement = Placement.Pointer; PointerPosition = p.Position; Open(); e.Handled = true;
    }
    protected override void OnDispose() { Target = null; base.OnDispose(); }
}

public class MenuItem : Button
{
    public override Rect GetLabelBounds() => new(8, 6, Math.Max(0, FinalRect.Width - 16 - (Submenu is null ? 0 : 16)), Math.Max(0, FinalRect.Height - 12));
    public override FocusVisualGeometry GetFocusVisualGeometry() => new(this, new(Point.Zero, FinalRect.Size), 3);
    public static readonly StyledProperty<bool> IsCheckableProperty = StyledProperty<bool>.Register<MenuItem>("IsCheckable", false, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<bool> CheckedProperty = StyledProperty<bool>.Register<MenuItem>("Checked", false, new() { Invalidate = InvalidateKind.Render });
    public bool IsCheckable { get => GetValue(IsCheckableProperty); set => SetValue(IsCheckableProperty, value); }
    public bool Checked { get => GetValue(CheckedProperty); set => SetValue(CheckedProperty, value); }
    [UiSlot("Submenu")]
    public Element? Submenu { get => Children.OfType<ContextMenu>().FirstOrDefault(); set { var old = Submenu; if (old is not null) Children.Remove(old); if (value is not null) Children.Add(value); } }
    public MenuItem() => AddHandler(ClickEvent, args =>
    {
        if (!ReferenceEquals(args.Source, this)) return;
        if (IsCheckable) Checked = !Checked;
        if (Submenu is ContextMenu sub) { sub.PlacementTarget = this; sub.Placement = Placement.Right; sub.IsOpen = !sub.IsOpen; }
        else for (var p = Parent; p is not null; p = p.Parent) if (p is Popup popup) popup.Close();
    });
    protected internal override Size MeasureCore(Size available)
    {
        foreach (var child in Children.OfType<Popup>()) child.Measure(available);
        var m = TextMeasurer.Current.Measure(Label, GetValue(TextBlock.FontSizeProperty)); return new(m.Width + 32, m.Height + 12);
    }
    protected internal override void ArrangeCore(Rect final)
    {
        foreach (var child in Children.OfType<Popup>()) child.Arrange(new(0, 0, 0, 0));
    }
    public override bool UsesDefaultDrawing => false;
    public override void Render(IElementDrawingContext c)
    {
        c.FillRectangle(new(0, 0, FinalRect.Width, FinalRect.Height), c.Background, c.CornerRadius);
        if (Submenu is not null) ControlIcons.Chevron(c, new(FinalRect.Width - 10, FinalRect.Height / 2), right: true);
        c.PushClip(GetLabelBounds());
        c.Text((IsCheckable ? Checked ? "✓ " : "   " : "") + Label, new(8, 6), GetValue(TextBlock.FontSizeProperty), c.Foreground);
        c.PopClip();
    }
}

public class TabItem : ContentControl
{
    public static readonly StyledProperty<string> HeaderProperty = StyledProperty<string>.Register<TabItem>("Header", "Tab", new() { Invalidate = InvalidateKind.Measure });
    public string Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
}

public class TabControl : Panel
{
    public override Rect? GetContentClip() => new Rect(0, HeaderHeight, FinalRect.Width, Math.Max(0, FinalRect.Height - HeaderHeight));
    public static readonly StyledProperty<int> SelectedIndexProperty = StyledProperty<int>.Register<TabControl>("SelectedIndex", 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly RoutedEvent SelectionChangedEvent = new("SelectionChanged");
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public TabItem? SelectedItem => SelectedIndex < 0 || SelectedIndex >= Children.Count ? null : Children[SelectedIndex] as TabItem;
    public override bool IsFocusable => true;
    private const double HeaderHeight = 34;
    private double[] _headerWidths = [];
    public override FocusVisualGeometry GetFocusVisualGeometry() => SelectedIndex >= 0 && SelectedIndex < _headerWidths.Length
        ? new(this, new(_headerWidths.Take(SelectedIndex).Sum() + 4, 4, Math.Max(0, _headerWidths[SelectedIndex] - 8), HeaderHeight - 8), 4)
        : base.GetFocusVisualGeometry();
    public TabControl()
    {
        PropertyChanged += (_, p) => { if (p == SelectedIndexProperty) { UpdateTabs(); RaiseEvent(new(SelectionChangedEvent)); } };
        AddHandler(PointerPressedEvent, e =>
        {
            if (e is not PointerEventArgs p || PointFromRoot(p.Position).Y >= HeaderHeight) return;
            var x = PointFromRoot(p.Position).X; double edge = 0;
            for (var i = 0; i < _headerWidths.Length; i++) { edge += _headerWidths[i]; if (x < edge) { SelectedIndex = i; e.Handled = true; break; } }
        });
        AddHandler(KeyDownEvent, e =>
        {
            if (e is not KeyEventArgs k || Children.Count == 0) return;
            if (k.Key == Key.Tab && k.Modifiers.HasFlag(KeyModifiers.Control))
            {
                SelectedIndex = (SelectedIndex + (k.Modifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1) + Children.Count) % Children.Count;
                RequestFocus();
            }
            else if (ReferenceEquals(e.Source, this) && k.Key is Key.Left or Key.Right) SelectedIndex = (SelectedIndex + (k.Key == Key.Left ? -1 : 1) + Children.Count) % Children.Count;
            else return;
            e.Handled = true;
        });
    }
    private void UpdateTabs() { for (var i = 0; i < Children.Count; i++) Children[i].IsVisible = i == SelectedIndex; InvalidateRender(); }
    protected internal override void OnChildrenChanged() { UpdateTabs(); base.OnChildrenChanged(); }
    protected internal override Size MeasureCore(Size available)
    {
        _headerWidths = Children.Select(c => TextMeasurer.Current.Measure((c as TabItem)?.Header ?? "Tab", GetValue(TextBlock.FontSizeProperty)).Width + 24).ToArray();
        SelectedItem?.Measure(new(available.Width, Math.Max(0, available.Height - HeaderHeight)));
        return new(Math.Max(_headerWidths.Sum(), SelectedItem?.DesiredSize.Width ?? 0), HeaderHeight + (SelectedItem?.DesiredSize.Height ?? 0));
    }
    protected internal override void ArrangeCore(Rect final)
    {
        foreach (var child in Children) child.Arrange(new(0, HeaderHeight, final.Width, Math.Max(0, final.Height - HeaderHeight)));
    }
    public override void Render(IElementDrawingContext c)
    {
        double x = 0;
        for (var i = 0; i < _headerWidths.Length; i++)
        {
            c.PushClip(new(x, 0, _headerWidths[i], HeaderHeight));
            c.FillRectangle(new(x, 0, _headerWidths[i], HeaderHeight), i == SelectedIndex ? c.SelectionBackground : c.Background, c.CornerRadius);
            c.Text((Children[i] as TabItem)?.Header ?? "Tab", new(x + 12, 7), GetValue(TextBlock.FontSizeProperty), i == SelectedIndex ? c.SelectionForeground : c.Foreground);
            if (i == SelectedIndex) c.FillRectangle(new(x + 12, HeaderHeight - 3, Math.Max(0, _headerWidths[i] - 24), 3), c.Accent, 1.5);
            c.PopClip();
            x += _headerWidths[i];
        }
    }
}
