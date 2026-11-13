using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace EclipseUI.Elements;

public enum SelectionMode { Single, Multiple, Extended }

public sealed class SelectionModel
{
    private IReadOnlyList<object?> _items = [];
    private readonly SortedSet<int> _selected = [];
    private int _anchor = -1;
    private SelectionMode _mode;
    public SelectionMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            if (value != SelectionMode.Single || _selected.Count <= 1) return;
            var first = _selected.Min; _selected.Clear(); _selected.Add(first); _anchor = first; Changed?.Invoke();
        }
    }
    public IReadOnlyList<int> SelectedIndices => _selected.ToArray();
    public int SelectedIndex => _selected.Count == 0 ? -1 : _selected.Min;
    public object? SelectedItem => SelectedIndex < 0 ? null : _items[SelectedIndex];
    public IReadOnlyList<object?> SelectedItems => _selected.Select(i => _items[i]).ToArray();
    public event Action? Changed;
    public void SetItems(IReadOnlyList<object?> items)
    {
        if (_items.SequenceEqual(items)) return;
        var old = SelectedItems; var before = _selected.ToArray();
        _items = items; _selected.Clear();
        foreach (var item in old)
            for (var i = 0; i < items.Count; i++)
                if (!_selected.Contains(i) && Equals(items[i], item)) { _selected.Add(i); break; }
        if (Mode == SelectionMode.Single && _selected.Count > 1) { var first = _selected.Min; _selected.Clear(); _selected.Add(first); }
        _anchor = SelectedIndex;
        if (!before.SequenceEqual(_selected) || !old.SequenceEqual(SelectedItems)) Changed?.Invoke();
    }
    public void Clear() { if (_selected.Count == 0) return; _selected.Clear(); _anchor = -1; Changed?.Invoke(); }
    public void Select(int index, bool toggle = false, bool extend = false)
    {
        if (index < 0 || index >= _items.Count) { if (!toggle && !extend) Clear(); return; }
        var before = _selected.ToArray();
        if (Mode == SelectionMode.Single) { _selected.Clear(); _selected.Add(index); _anchor = index; }
        else if (extend && _anchor >= 0)
        {
            if (!toggle) _selected.Clear();
            for (var i = Math.Min(index, _anchor); i <= Math.Max(index, _anchor); i++) _selected.Add(i);
        }
        else
        {
            if (!toggle) _selected.Clear();
            if (!_selected.Add(index)) _selected.Remove(index);
            _anchor = index;
        }
        if (!before.SequenceEqual(_selected)) Changed?.Invoke();
    }
}

public class ItemsControl : StackPanel
{
    private IEnumerable? _source;
    private INotifyCollectionChanged? _observable;
    private Func<object?, Element>? _template;
    private List<Entry> _entries = [];
    private bool _rebuilding;
    private sealed record Entry(object? Item, Element Container, bool Owned);
    public ObservableCollection<object?> Items { get; } = [];
    public IEnumerable? ItemsSource
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value)) return;
            if (_observable is not null) _observable.CollectionChanged -= SourceChanged;
            _source = value; _observable = value as INotifyCollectionChanged;
            if (_observable is not null) _observable.CollectionChanged += SourceChanged;
            RefreshItems();
        }
    }
    public Func<object?, Element>? ItemTemplate { get => _template; set { if (ReferenceEquals(_template, value)) return; _template = value; RefreshItems(true); } }
    public IReadOnlyList<object?> ItemValues => _source is not null || Items.Count > 0 || _entries.Count > 0
        ? _entries.Select(e => e.Item).ToArray() : Children.Cast<object?>().ToArray();
    public override bool IsVisualTransparent => true;
    public ItemsControl() => Items.CollectionChanged += SourceChanged;
    private void SourceChanged(object? sender, NotifyCollectionChangedEventArgs e) { if (ReferenceEquals(sender, Items) && _source is not null) return; RefreshItems(); }
    public void RefreshItems() => RefreshItems(false);
    private void RefreshItems(bool recreate)
    {
        if (_rebuilding || IsDisposed) return;
        var values = (_source ?? Items).Cast<object?>().ToArray();
        var previous = _entries; var unused = previous.ToList(); var next = new List<Entry>();
        try
        {
            foreach (var value in values)
            {
                var match = recreate ? null : unused.FirstOrDefault(e => ReferenceEquals(e.Item, value)) ?? unused.FirstOrDefault(e => Equals(e.Item, value));
                if (match is not null) { unused.Remove(match); next.Add(match); }
                else { var container = CreateContainer(value); next.Add(new(value, container, !ReferenceEquals(value, container))); }
            }
            _rebuilding = true;
            Children.ReplaceAll(next.Select(e => e.Container));
            _entries = next;
        }
        catch
        {
            foreach (var entry in next.Where(e => !previous.Contains(e) && e.Owned && e.Container.Parent is null)) entry.Container.Dispose();
            throw;
        }
        finally { _rebuilding = false; }
        foreach (var entry in unused.Where(e => e.Owned)) entry.Container.Dispose();
        OnItemsChanged();
    }
    protected virtual Element CreateContainer(object? item) => _template?.Invoke(item) ?? item as Element ?? MakeText(item?.ToString() ?? "");
    protected Element MakeItemContent(object? item) => _template?.Invoke(item) ?? item as Element ?? MakeText(item?.ToString() ?? "");
    internal static TextBlock MakeText(string text) { var block = new TextBlock(); block.AddRun(text); return block; }
    protected internal override void OnChildrenChanged() { if (!_rebuilding) OnItemsChanged(); base.OnChildrenChanged(); }
    protected virtual void OnItemsChanged() { }
    protected override void OnDispose()
    {
        if (_observable is not null) _observable.CollectionChanged -= SourceChanged;
        Items.CollectionChanged -= SourceChanged; base.OnDispose();
    }
}

public class ListBox : ItemsControl
{
    public static readonly StyledProperty<int> SelectedIndexProperty = StyledProperty<int>.Register<ListBox>("SelectedIndex", -1, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<SelectionMode> SelectionModeProperty = StyledProperty<SelectionMode>.Register<ListBox>("SelectionMode", SelectionMode.Single, new() { Invalidate = InvalidateKind.Render });
    public static readonly RoutedEvent SelectionChangedEvent = new("SelectionChanged");
    public SelectionModel Selection { get; } = new();
    private bool _sync;
    private int _navigationIndex = -1;
    public override FocusVisualGeometry GetFocusVisualGeometry()
    {
        var index = _navigationIndex >= 0 && _navigationIndex < Children.Count ? _navigationIndex : SelectedIndex;
        return index >= 0 && index < Children.Count
            ? new(Children[index], new(4, 4, Math.Max(0, Children[index].FinalRect.Width - 8), Math.Max(0, Children[index].FinalRect.Height - 8)))
            : base.GetFocusVisualGeometry();
    }
    private int[] _reportedIndices = [];
    private object?[] _reportedItems = [];
    public Action<object?>? SelectedItemChanged { get; set; }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public object? SelectedItem { get => Selection.SelectedItem; set { var values = ItemValues; SelectedIndex = Enumerable.Range(0, values.Count).FirstOrDefault(i => Equals(values[i], value), -1); } }
    public IReadOnlyList<object?> SelectedItems => Selection.SelectedItems;
    public SelectionMode SelectionMode { get => GetValue(SelectionModeProperty); set => SetValue(SelectionModeProperty, value); }
    public override bool IsFocusable => true;
    public ListBox()
    {
        Selection.Changed += SynchronizeSelection;
        PropertyChanged += (_, p) =>
        {
            if (_sync) return;
            if (p == SelectionModeProperty) { Selection.Mode = SelectionMode; Selection.SetItems(ItemValues); SynchronizeSelection(); }
            if (p == SelectedIndexProperty && ItemValues.Count > 0) { _navigationIndex = SelectedIndex; Selection.SetItems(ItemValues); Selection.Select(SelectedIndex); SynchronizeSelection(); }
        };
        AddHandler(PreviewPointerPressedEvent, args =>
        {
            if (args is not PointerEventArgs p || p.Button != PointerButton.Left) return;
            var child = args.Source;
            while (child?.Parent is not null && !ReferenceEquals(child.Parent, this)) child = child.Parent;
            var index = child is null ? -1 : Children.ToList().IndexOf(child);
            if (index < 0) return;
            Select(index, p.Modifiers);
        });
        AddHandler(KeyDownEvent, args =>
        {
            if (args is not KeyEventArgs k || Children.Count == 0) return;
            var index = _navigationIndex < 0 ? SelectedIndex : _navigationIndex;
            switch (k.Key)
            {
                case Key.Up: index = Math.Max(0, index - 1); break;
                case Key.Down: index = Math.Min(Children.Count - 1, index + 1); break;
                case Key.Home: index = 0; break;
                case Key.End: index = Children.Count - 1; break;
                case Key.A when k.Modifiers.HasFlag(KeyModifiers.Control) && SelectionMode != SelectionMode.Single:
                    Selection.Select(0); Selection.Select(Children.Count - 1, extend: true); args.Handled = true; return;
                case Key.Space: Select(Math.Max(0, index), k.Modifiers | KeyModifiers.Control); args.Handled = true; return;
                default: return;
            }
            Select(index, k.Modifiers); args.Handled = true;
        });
    }
    public void Select(int index, KeyModifiers modifiers = KeyModifiers.None)
    {
        Selection.Mode = SelectionMode; Selection.SetItems(ItemValues);
        Selection.Select(index, SelectionMode == SelectionMode.Multiple || modifiers.HasFlag(KeyModifiers.Control), modifiers.HasFlag(KeyModifiers.Shift));
        _navigationIndex = index; InvalidateRender(); BringItemIntoView(index);
    }
    protected override Element CreateContainer(object? item)
    {
        if (item is ListBoxItem existing) { existing.FocusVisualOwner = this; return existing; }
        var container = new ListBoxItem { Item = item, FocusVisualOwner = this }; container.Children.Add(MakeItemContent(item)); return container;
    }
    protected override void OnItemsChanged()
    {
        if (_sync) return;
        foreach (var item in Children.OfType<ListBoxItem>()) item.FocusVisualOwner = this;
        Selection.Mode = SelectionMode;
        var requested = SelectedIndex;
        var hadSelection = Selection.SelectedIndex >= 0;
        Selection.SetItems(ItemValues);
        if (!hadSelection && Selection.SelectedIndex < 0 && requested >= 0 && requested < ItemValues.Count) Selection.Select(requested);
        SynchronizeSelection(); base.OnItemsChanged();
    }
    private void SynchronizeSelection()
    {
        if (_sync) return;
        _sync = true;
        var changed = !_reportedIndices.SequenceEqual(Selection.SelectedIndices) || !_reportedItems.SequenceEqual(Selection.SelectedItems);
        try
        {
            SelectedIndex = Selection.SelectedIndex;
            var indices = Selection.SelectedIndices;
            for (var i = 0; i < Children.Count; i++) if (Children[i] is ListBoxItem item) item.IsSelected = indices.Contains(i);
        }
        finally { _sync = false; }
        // Multiple selection can change while the first selected index remains the same.
        if (changed)
        {
            _reportedIndices = Selection.SelectedIndices.ToArray(); _reportedItems = Selection.SelectedItems.ToArray();
            SelectedItemChanged?.Invoke(SelectedItem); RaiseEvent(new(SelectionChangedEvent));
        }
    }
    public void BringItemIntoView(int index)
    {
        if (index < 0 || index >= Children.Count) return;
        for (var parent = Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not ScrollViewer viewer) continue;
            var y = Children[index].FinalRect.Y;
            if (y < viewer.VerticalOffset) viewer.ScrollToVerticalOffset(y);
            else if (y + Children[index].FinalRect.Height > viewer.VerticalOffset + viewer.ViewportHeight)
                viewer.ScrollToVerticalOffset(y + Children[index].FinalRect.Height - viewer.ViewportHeight);
            break;
        }
    }
}
