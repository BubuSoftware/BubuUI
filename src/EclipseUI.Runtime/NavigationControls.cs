namespace EclipseUI.Elements;

public class ComboBox : Panel
{
    public override Rect? GetContentClip() => new Rect(Point.Zero, FinalRect.Size);
    public const string ButtonPart = "PART_Button", ListPart = "PART_List", PopupPart = "PART_Popup";
    private Button? _button;
    private ListBox? _list;
    private Popup? _popup;
    private System.Collections.IEnumerable? _source;
    private System.Collections.Specialized.INotifyCollectionChanged? _observable;
    private Func<object?, Element>? _itemTemplate;
    private bool _sync;
    public static readonly StyledProperty<int> SelectedIndexProperty = StyledProperty<int>.Register<ComboBox>("SelectedIndex", -1, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<string> PlaceholderProperty = StyledProperty<string>.Register<ComboBox>("Placeholder", "Select…", new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<bool> IsDropDownOpenProperty = StyledProperty<bool>.Register<ComboBox>("IsDropDownOpen", false, new() { Invalidate = InvalidateKind.Measure });
    public static readonly RoutedEvent SelectionChangedEvent = new("SelectionChanged");
    public System.Collections.ObjectModel.ObservableCollection<object?> Items { get; } = [];
    public System.Collections.IEnumerable? ItemsSource
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value)) return;
            if (_observable is not null) _observable.CollectionChanged -= ItemsChanged;
            _source = value; _observable = value as System.Collections.Specialized.INotifyCollectionChanged;
            if (_observable is not null) _observable.CollectionChanged += ItemsChanged;
            UpdateSelection();
        }
    }
    public System.Collections.IEnumerable EffectiveItems => _source ?? Items;
    public Func<object?, Element>? ItemTemplate { get => _itemTemplate; set { _itemTemplate = value; UpdateSelection(); } }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public object? SelectedItem
    {
        get => SelectedIndex >= 0 ? EffectiveItems.Cast<object?>().ElementAtOrDefault(SelectedIndex) : null;
        set => SelectedIndex = EffectiveItems.Cast<object?>().ToList().FindIndex(i => Equals(i, value));
    }
    public string DisplayText => SelectedItem?.ToString() ?? Placeholder;
    public string Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public bool IsDropDownOpen { get => GetValue(IsDropDownOpenProperty); set => SetValue(IsDropDownOpenProperty, value); }
    public override bool IsFocusable => true;
    public ComboBox()
    {
        Items.CollectionChanged += ItemsChanged;
        PropertyChanged += (_, p) =>
        {
            if (p == SelectedIndexProperty || p == PlaceholderProperty) UpdateSelection();
            if (p == IsDropDownOpenProperty) UpdateDropDown();
        };
        AddHandler(KeyDownEvent, e =>
        {
            if (e is not KeyEventArgs k) return;
            switch (k.Key)
            {
                case Key.Space: case Key.Enter: if (ReferenceEquals(e.Source, _button)) return; IsDropDownOpen = !IsDropDownOpen; break;
                case Key.Down when k.Modifiers.HasFlag(KeyModifiers.Alt): IsDropDownOpen = true; break;
                case Key.Down: SelectedIndex = Math.Min(EffectiveItems.Cast<object?>().Count() - 1, SelectedIndex + 1); break;
                case Key.Up: SelectedIndex = Math.Max(0, SelectedIndex - 1); break;
                case Key.Escape: IsDropDownOpen = false; break;
                default: return;
            }
            e.Handled = true;
        });
    }
    protected override void OnApplyTemplate(TemplateInstance instance)
    {
        base.OnApplyTemplate(instance);
        _button = instance.FindPart<Button>(ButtonPart) ?? throw new InvalidOperationException($"ComboBox template requires {ButtonPart} of type Button.");
        _list = instance.FindPart<ListBox>(ListPart) ?? throw new InvalidOperationException($"ComboBox template requires {ListPart} of type ListBox.");
        _popup = instance.FindPart<Popup>(PopupPart) ?? throw new InvalidOperationException($"ComboBox template requires {PopupPart} of type Popup.");
        if (_button is not null) { _button.FocusVisualOwner = this; _button.AddHandler(Button.ClickEvent, ButtonClicked); }
        if (_popup is not null) { _popup.PlacementTarget = this; _popup.LightDismissExclusion = _button; _popup.AddHandler(Popup.ClosedEvent, PopupClosed); }
        if (_list is not null)
        {
            _list.AddHandler(ListBox.SelectionChangedEvent, ListSelectionChanged);
            _list.AddHandler(PointerReleasedEvent, ListReleased);
            _list.AddHandler(KeyDownEvent, ListKeyDown);
        }
        UpdateSelection(notify: false); UpdateDropDown();
    }
    protected override void OnTemplateRemoving()
    {
        if (_button is not null) _button.RemoveHandler(Button.ClickEvent, ButtonClicked);
        if (_popup is not null) { _popup.LightDismissExclusion = null; _popup.RemoveHandler(Popup.ClosedEvent, PopupClosed); }
        if (_list is not null)
        {
            _list.RemoveHandler(ListBox.SelectionChangedEvent, ListSelectionChanged);
            _list.RemoveHandler(PointerReleasedEvent, ListReleased);
            _list.RemoveHandler(KeyDownEvent, ListKeyDown);
        }
        _button = null; _list = null; _popup = null;
        base.OnTemplateRemoving();
    }
    private void ButtonClicked(RoutedEventArgs e)
    {
        // The popup restores the public control, rather than its template's implementation button.
        if (!IsDropDownOpen) RequestFocus();
        IsDropDownOpen = !IsDropDownOpen; e.Handled = true;
    }
    private void PopupClosed(RoutedEventArgs e) { if (IsDropDownOpen) IsDropDownOpen = false; }
    private void ListSelectionChanged(RoutedEventArgs e) { if (!_sync && _list is not null) SelectedIndex = _list.SelectedIndex; e.Handled = true; }
    private void ListReleased(RoutedEventArgs e) => IsDropDownOpen = false;
    private void ListKeyDown(RoutedEventArgs e) { if (e is KeyEventArgs { Key: Key.Enter }) { IsDropDownOpen = false; e.Handled = true; } }
    private void ItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) { if (!ReferenceEquals(sender, Items) || _source is null) UpdateSelection(); }
    private void UpdateDropDown()
    {
        if (_popup is not null) _popup.IsOpen = IsDropDownOpen;
        if (IsDropDownOpen && _list is not null)
        {
            _list.MinWidth = FinalRect.Width; _popup?.InvalidateMeasure(); _list.RequestFocus();
            if (_list.SelectedIndex >= 0) _list.BringItemIntoView(_list.SelectedIndex);
        }
    }
    private void UpdateSelection(bool notify = true)
    {
        if (_sync) return;
        _sync = true;
        try
        {
            if (_list is not null) { _list.ItemsSource = EffectiveItems; _list.ItemTemplate = ItemTemplate; _list.SelectedIndex = SelectedIndex; }
            if (_button is not null) _button.Label = DisplayText;
        }
        finally { _sync = false; }
        InvalidateMeasure(); Invalidate();
        if (notify) RaiseEvent(new(SelectionChangedEvent));
    }
    protected override void OnDispose()
    {
        if (_observable is not null) _observable.CollectionChanged -= ItemsChanged;
        Items.CollectionChanged -= ItemsChanged;
        base.OnDispose();
    }
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
    public ContextMenu() => Content = _menu;
    private void TargetPressed(RoutedEventArgs e)
    {
        if (e is not PointerEventArgs { Button: PointerButton.Right } p) return;
        Placement = Placement.Pointer; PointerPosition = p.Position; Open(); e.Handled = true;
    }
    protected override void OnDispose() { Target = null; base.OnDispose(); }
}

public class MenuItem : Button
{
    public static readonly StyledProperty<double> SubmenuIndicatorWidthProperty = StyledProperty<double>.Register<MenuItem>(nameof(SubmenuIndicatorWidth), 0, new() { Invalidate = InvalidateKind.Measure });
    public double SubmenuIndicatorWidth { get => GetValue(SubmenuIndicatorWidthProperty); set => SetValue(SubmenuIndicatorWidthProperty, Math.Max(0, value)); }
    public override Rect GetLabelBounds() => new(HorizontalPadding, VerticalPadding, Math.Max(0, FinalRect.Width - HorizontalPadding * 2 - (Submenu is null ? 0 : SubmenuIndicatorWidth)), Math.Max(0, FinalRect.Height - VerticalPadding * 2));
    public override FocusVisualGeometry GetFocusVisualGeometry() => new(this, new(Point.Zero, FinalRect.Size), CornerRadius);
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
        var m = TextMeasurer.Current.Measure(Label, GetValue(TextBlock.FontSizeProperty)); return new(m.Width + HorizontalPadding * 2 + SubmenuIndicatorWidth, m.Height + VerticalPadding * 2);
    }
    protected internal override void ArrangeCore(Rect final)
    {
        foreach (var child in Children.OfType<Popup>()) child.Arrange(new(0, 0, 0, 0));
    }

}

public class TabItem : ContentControl
{
    public static readonly StyledProperty<string> HeaderProperty = StyledProperty<string>.Register<TabItem>("Header", "Tab", new() { Invalidate = InvalidateKind.Measure });
    public string Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
}

public class TabControl : Panel
{
    public override Rect? GetContentClip() => new Rect(Point.Zero, FinalRect.Size);
    public static readonly StyledProperty<int> SelectedIndexProperty = StyledProperty<int>.Register<TabControl>("SelectedIndex", 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly RoutedEvent SelectionChangedEvent = new("SelectionChanged");
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public TabItem? SelectedItem => SelectedIndex < 0 || SelectedIndex >= Children.Count ? null : Children[SelectedIndex] as TabItem;
    public override bool IsFocusable => true;
    public static readonly StyledProperty<double> HeaderHeightProperty = StyledProperty<double>.Register<TabControl>(nameof(HeaderHeight), 0, new() { Invalidate = InvalidateKind.Measure });
    public static readonly StyledProperty<double> HeaderPaddingProperty = StyledProperty<double>.Register<TabControl>(nameof(HeaderPadding), 0, new() { Invalidate = InvalidateKind.Measure });
    public double HeaderHeight { get => GetValue(HeaderHeightProperty); set => SetValue(HeaderHeightProperty, Math.Max(0, value)); }
    public double HeaderPadding { get => GetValue(HeaderPaddingProperty); set => SetValue(HeaderPaddingProperty, Math.Max(0, value)); }
    public IReadOnlyList<double> HeaderWidths => _headerWidths;
    private double[] _headerWidths = [];
    public override FocusVisualGeometry GetFocusVisualGeometry() => SelectedIndex >= 0 && SelectedIndex < _headerWidths.Length
        ? new(this, new(_headerWidths.Take(SelectedIndex).Sum(), 0, _headerWidths[SelectedIndex], HeaderHeight), 0)
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
        _headerWidths = Children.Select(c => TextMeasurer.Current.Measure((c as TabItem)?.Header ?? "Tab", GetValue(TextBlock.FontSizeProperty)).Width + HeaderPadding * 2).ToArray();
        SelectedItem?.Measure(new(available.Width, Math.Max(0, available.Height - HeaderHeight)));
        return new(Math.Max(_headerWidths.Sum(), SelectedItem?.DesiredSize.Width ?? 0), HeaderHeight + (SelectedItem?.DesiredSize.Height ?? 0));
    }
    protected internal override void ArrangeCore(Rect final)
    {
        foreach (var child in Children) child.Arrange(new(0, HeaderHeight, final.Width, Math.Max(0, final.Height - HeaderHeight)));
    }
}
