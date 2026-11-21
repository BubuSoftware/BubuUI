using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace EclipseUI.Elements;

public enum SelectionMode { Single, Multiple, Extended }

// A data occurrence can be consumed once. Reference matching takes precedence over value
// equality, while queues preserve the order of equal values and null duplicates.
internal sealed class ItemIndexMap
{
    private readonly record struct ValueKey(object? Value);
    private readonly Dictionary<object, Queue<int>> _references = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ValueKey, Queue<int>> _values = new();
    private readonly bool[] _used;
    internal ItemIndexMap(IReadOnlyList<object?> items)
    {
        _used = new bool[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item is not null)
            {
                if (!_references.TryGetValue(item, out var identities)) _references[item] = identities = new();
                identities.Enqueue(i);
            }
            var key = new ValueKey(item);
            if (!_values.TryGetValue(key, out var equal)) _values[key] = equal = new();
            equal.Enqueue(i);
        }
    }
    internal int[] Match(IReadOnlyList<object?> requests)
    {
        var matches = new int[requests.Count];
        Array.Fill(matches, -1);
        // Reserve every surviving reference before any new equal value can consume it.
        for (var i = 0; i < requests.Count; i++)
            if (requests[i] is { } item && _references.TryGetValue(item, out var identities)) matches[i] = Take(identities);
        for (var i = 0; i < requests.Count; i++)
            if (matches[i] < 0 && _values.TryGetValue(new(requests[i]), out var equal)) matches[i] = Take(equal);
        return matches;
    }
    private int Take(Queue<int> queue)
    {
        while (queue.TryDequeue(out var index))
            if (!_used[index]) { _used[index] = true; return index; }
        return -1;
    }
}

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
    public bool IsSelected(int index) => _selected.Contains(index);
    public int SelectedIndex => _selected.Count == 0 ? -1 : _selected.Min;
    public object? SelectedItem => SelectedIndex < 0 ? null : _items[SelectedIndex];
    public IReadOnlyList<object?> SelectedItems => _selected.Select(i => _items[i]).ToArray();
    public event Action? Changed;
    public void SetItems(IReadOnlyList<object?> items)
    {
        if (ReferenceEquals(_items, items)) return;
        var old = SelectedItems; var before = _selected.ToArray();
        _items = items; _selected.Clear();
        if (old.Count > 0)
            foreach (var index in new ItemIndexMap(items).Match(old))
                if (index >= 0) _selected.Add(index);
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
    public override Rect? GetContentClip() => new Rect(Padding, Padding, Math.Max(0, FinalRect.Width - Padding * 2), Math.Max(0, FinalRect.Height - Padding * 2));
    private IEnumerable? _source;
    private INotifyCollectionChanged? _observable;
    private Func<object?, Element>? _template;
    private List<Entry> _entries = [];
    private object?[] _values = [];
    private readonly Dictionary<Element, Entry> _containerEntries = new(ReferenceEqualityComparer.Instance);
    private bool _rebuilding;
    private sealed class Entry(object? item)
    {
        internal readonly object? Item = item;
        internal Element? Container;
        internal bool Owned;
        internal int Index;
    }
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
    public IReadOnlyList<object?> ItemValues => HasDataItems ? _values : Children.Cast<object?>().ToArray();
    protected bool HasDataItems => _source is not null || Items.Count > 0 || _entries.Count > 0;
    protected IEnumerable<Element> RealizedContainers => HasDataItems
        ? _containerEntries.Keys.Where(container => !container.IsDisposed) : Children;
    public override bool IsVisualTransparent => true;
    public ItemsControl() => Items.CollectionChanged += SourceChanged;
    private void SourceChanged(object? sender, NotifyCollectionChangedEventArgs e) { if (ReferenceEquals(sender, Items) && _source is not null) return; RefreshItems(); }
    public void RefreshItems() => RefreshItems(false);
    private void RefreshItems(bool recreate)
    {
        if (_rebuilding || IsDisposed) return;
        var values = (_source ?? Items).Cast<object?>().ToArray();
        var previous = _entries;
        var previousValues = _values;
        var focus = FocusManager.GetFor(this);
        var focused = focus?.Focused;
        var restoreFocus = focused is not null && Descendants().Contains(focused);
        var visibility = focused?.IsFocusVisible == true ? FocusVisibility.Show : FocusVisibility.Auto;
        var used = new HashSet<Entry>();
        var matches = recreate ? null : new ItemIndexMap(previous.Select(entry => entry.Item).ToArray()).Match(values);
        var next = new List<Entry>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var index = matches?[i] ?? -1;
            if (index >= 0) { var entry = previous[index]; used.Add(entry); next.Add(entry); }
            else next.Add(new(values[i]));
        }
        try
        {
            _rebuilding = true;
            _entries = next; _values = values;
            for (var i = 0; i < next.Count; i++) next[i].Index = i;
            InstallItemContainers(values);
        }
        catch
        {
            _entries = previous; _values = previousValues;
            for (var i = 0; i < previous.Count; i++) previous[i].Index = i;
            foreach (var entry in next.Where(entry => !used.Contains(entry)))
                if (entry.Container is { } container)
                {
                    _containerEntries.Remove(container);
                    if (entry.Owned && container.Parent is null) container.Dispose();
                }
            throw;
        }
        finally { _rebuilding = false; }
        foreach (var entry in previous.Where(entry => !used.Contains(entry)))
        {
            if (entry.Container is { } container)
            {
                if (_containerEntries.TryGetValue(container, out var current) && !ReferenceEquals(current, entry)) continue;
                _containerEntries.Remove(container);
                if (entry.Owned && !container.IsDisposed) container.Dispose();
            }
        }
        OnItemsChanged();
        if (restoreFocus && focus?.Focused is null && focused is { IsAttached: true, IsDisposed: false })
            focused.RequestFocus(FocusOrigin.Restore, visibility);
    }
    protected virtual void InstallItemContainers(IReadOnlyList<object?> values)
        => Children.ReplaceAll(Enumerable.Range(0, values.Count).Select(GetItemContainer));
    protected Element GetItemContainer(int index)
    {
        var entry = _entries[index];
        if (entry.Container is { IsDisposed: false } existing) return existing;
        if (entry.Container is { } retired) _containerEntries.Remove(retired);
        var container = CreateContainer(entry.Item);
        entry.Container = container; entry.Owned = !ReferenceEquals(container, entry.Item);
        _containerEntries[container] = entry;
        OnContainerRealized(container, index);
        return container;
    }
    protected virtual void OnContainerRealized(Element container, int index) { }
    protected Element? FindItemContainer(int index) => HasDataItems
        ? index >= 0 && index < _entries.Count && _entries[index].Container is { IsDisposed: false } container ? container : null
        : index >= 0 && index < Children.Count ? Children[index] : null;
    protected int IndexOfItemContainer(Element container) => HasDataItems
        ? _containerEntries.TryGetValue(container, out var entry) && entry.Index < _entries.Count && ReferenceEquals(_entries[entry.Index], entry) ? entry.Index : -1
        : Children.ToList().IndexOf(container);
    protected void ReleaseItemContainer(Element container)
    {
        if (_containerEntries.Remove(container, out var entry) && ReferenceEquals(entry.Container, container)) entry.Container = null;
    }
    protected virtual Element CreateContainer(object? item) => _template?.Invoke(item) ?? item as Element ?? MakeText(item?.ToString() ?? "");
    protected Element MakeItemContent(object? item) => _template?.Invoke(item) ?? item as Element ?? MakeText(item?.ToString() ?? "");
    internal static TextBlock MakeText(string text) { var block = new TextBlock(); block.AddRun(text); return block; }
    protected internal override void OnChildrenChanged() { if (!_rebuilding) OnItemsChanged(); base.OnChildrenChanged(); }
    protected virtual void OnItemsChanged() { }
    protected override void OnDispose()
    {
        if (_observable is not null) _observable.CollectionChanged -= SourceChanged;
        Items.CollectionChanged -= SourceChanged;
        var containers = _containerEntries.Keys.ToArray();
        _containerEntries.Clear(); _entries.Clear(); _values = [];
        try { base.OnDispose(); }
        finally { foreach (var container in containers) if (!container.IsDisposed) container.Dispose(); }
    }
}

public class ListBox : ItemsControl
{
    public static readonly StyledProperty<int> SelectedIndexProperty = StyledProperty<int>.Register<ListBox>("SelectedIndex", -1, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<SelectionMode> SelectionModeProperty = StyledProperty<SelectionMode>.Register<ListBox>("SelectionMode", SelectionMode.Single, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<bool> IsVirtualizationEnabledProperty = StyledProperty<bool>.Register<ListBox>(nameof(IsVirtualizationEnabled), true, new() { Invalidate = InvalidateKind.Measure });
    public static readonly RoutedEvent SelectionChangedEvent = new("SelectionChanged");
    public SelectionModel Selection { get; } = new();
    private bool _sync;
    private int _navigationIndex = -1;
    private VirtualizingStackPanel? _virtualItems;
    private bool _virtualizing;
    private int? _pendingBringIndex;
    /// <summary>Data lists with at least 128 items realize only the scroll viewport. Caller-owned element items retain ordinary layout.</summary>
    public bool IsVirtualizationEnabled { get => GetValue(IsVirtualizationEnabledProperty); set => SetValue(IsVirtualizationEnabledProperty, value); }
    public bool IsVirtualizing => _virtualizing;
    public int RealizedItemCount => _virtualizing ? _virtualItems?.RealizedCount ?? 0 : RealizedContainers.Count();
    /// <summary>Returns a realized container, or null when its item is outside the virtualized viewport.</summary>
    public Element? ContainerFromIndex(int index) => FindItemContainer(index);
    public override FocusVisualGeometry GetFocusVisualGeometry()
    {
        var index = _navigationIndex >= 0 && _navigationIndex < ItemValues.Count ? _navigationIndex : SelectedIndex;
        return FindItemContainer(index) is { } item
            ? new(item, new(4, 4, Math.Max(0, item.FinalRect.Width - 8), Math.Max(0, item.FinalRect.Height - 8)))
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
            if ((p == IsVirtualizationEnabledProperty || p == OrientationProperty) && HasDataItems) RefreshItems();
            if (p == GapProperty && _virtualItems is not null) _virtualItems.Gap = Gap;
            if (_sync) return;
            if (p == SelectionModeProperty) { Selection.Mode = SelectionMode; Selection.SetItems(ItemValues); SynchronizeSelection(); }
            if (p == SelectedIndexProperty && ItemValues.Count > 0)
            {
                _navigationIndex = SelectedIndex; Selection.SetItems(ItemValues); Selection.Select(SelectedIndex); SynchronizeSelection();
                BringItemIntoView(SelectedIndex);
            }
        };
        AddHandler(PreviewPointerPressedEvent, args =>
        {
            if (args is not PointerEventArgs p || p.Button != PointerButton.Left) return;
            // Locate the data container across template and virtualization presenters.
            for (var child = args.Source; child is not null && !ReferenceEquals(child, this); child = child.ContentParent)
            {
                var index = IndexOfItemContainer(child);
                if (index < 0) continue;
                Select(index, p.Modifiers); break;
            }
        });
        AddHandler(KeyDownEvent, args =>
        {
            var count = ItemValues.Count;
            if (args is not KeyEventArgs k || count == 0) return;
            var index = _navigationIndex < 0 ? SelectedIndex : _navigationIndex;
            switch (k.Key)
            {
                case Key.Up: index = Math.Max(0, index - 1); break;
                case Key.Down: index = Math.Min(count - 1, index + 1); break;
                case Key.Home: index = 0; break;
                case Key.End: index = count - 1; break;
                case Key.A when InputGestures.HasCommand(k.Modifiers) && SelectionMode != SelectionMode.Single:
                    Selection.Select(0); Selection.Select(count - 1, extend: true); args.Handled = true; return;
                case Key.Space: Select(Math.Max(0, index), k.Modifiers | KeyModifiers.Control); args.Handled = true; return;
                default: return;
            }
            Select(index, k.Modifiers); args.Handled = true;
        });
    }
    protected override void InstallItemContainers(IReadOnlyList<object?> values)
    {
        var virtualize = IsVirtualizationEnabled && Orientation == Orientation.Vertical &&
            (values.Count >= 128 || _virtualizing && values.Count > 0) && values.All(item => item is not Element);
        if (virtualize)
        {
            var entering = !_virtualizing;
            var previous = entering ? RealizedContainers.ToArray() : [];
            ScrollViewer? viewer = null;
            if (entering)
                for (var parent = Parent; parent is not null; parent = parent.Parent)
                    if (parent is ScrollViewer scroll) { viewer = scroll; break; }
            var focused = FocusManager.GetFor(this)?.Focused;
            var retained = previous.Where(row => IndexOfItemContainer(row) >= 0 &&
                (viewer is null || focused is not null && (ReferenceEquals(focused, row) || row.Descendants().Contains(focused)) ||
                    row.FinalRect.Height > 0 && viewer.ViewportHeight > 0 &&
                    viewer.PointFromRoot(Point.Zero).Y - row.PointFromRoot(Point.Zero).Y + row.FinalRect.Height > viewer.Padding &&
                    viewer.PointFromRoot(Point.Zero).Y - row.PointFromRoot(Point.Zero).Y < viewer.Padding + viewer.ViewportHeight)).ToArray();
            _virtualizing = true;
            _pendingBringIndex = null;
            _virtualItems ??= new VirtualizingStackPanel { IsVariableHeight = true, ItemFactory = GetItemContainer, ItemRetired = ReleaseItemContainer };
            _virtualItems.Gap = Gap;
            _virtualItems.ItemCount = values.Count;
            _virtualItems.Refresh(container => IndexOfItemContainer(container) is var index && index >= 0 ? index : null);
            Children.ReplaceAll([_virtualItems]);
            if (entering)
            {
                _virtualItems.AdoptItemContainers(retained.Select(row => new KeyValuePair<int, Element>(IndexOfItemContainer(row), row)));
                var kept = retained.ToHashSet();
                foreach (var row in previous)
                    if (!kept.Contains(row))
                        try { row.Dispose(); }
                        finally { ReleaseItemContainer(row); }
            }
        }
        else
        {
            _virtualizing = false;
            _virtualItems?.ReleaseItemContainers();
            base.InstallItemContainers(values);
        }
    }
    private sealed class GeneratedItem : ListBoxItem
    {
        internal Action<Element>? Retired;
        protected override void OnDispose()
        {
            var retired = Retired; Retired = null;
            try { base.OnDispose(); }
            finally { retired?.Invoke(this); }
        }
    }
    public void Select(int index, KeyModifiers modifiers = KeyModifiers.None)
    {
        Selection.Mode = SelectionMode; Selection.SetItems(ItemValues);
        Selection.Select(index, SelectionMode == SelectionMode.Multiple || InputGestures.HasCommand(modifiers), modifiers.HasFlag(KeyModifiers.Shift));
        _navigationIndex = index; InvalidateRender(); BringItemIntoView(index);
    }
    protected override Element CreateContainer(object? item)
    {
        if (item is ListBoxItem existing) { existing.FocusVisualOwner = this; return existing; }
        ListBoxItem container = new GeneratedItem { Retired = ReleaseItemContainer };
        container.Item = item; container.FocusVisualOwner = this;
        try { container.Children.Add(MakeItemContent(item)); }
        catch { container.Dispose(); throw; }
        return container;
    }
    protected override void OnContainerRealized(Element container, int index)
    {
        if (container is ListBoxItem item) item.IsSelected = Selection.IsSelected(index);
        base.OnContainerRealized(container, index);
    }
    protected override void OnItemsChanged()
    {
        // Disposing item containers removes them from Children. This is teardown,
        // not a selection change to publish through events or two-way bindings.
        if (_sync || IsDisposed) return;
        foreach (var item in RealizedContainers.OfType<ListBoxItem>()) item.FocusVisualOwner = this;
        Selection.Mode = SelectionMode;
        var requested = SelectedIndex;
        var hadSelection = Selection.SelectedIndex >= 0;
        Selection.SetItems(ItemValues);
        if (!hadSelection && Selection.SelectedIndex < 0 && requested >= 0 && requested < ItemValues.Count) Selection.Select(requested);
        SynchronizeSelection(); _navigationIndex = SelectedIndex;
        if (SelectedIndex >= 0) BringItemIntoView(SelectedIndex);
        base.OnItemsChanged();
    }
    private void SynchronizeSelection()
    {
        if (_sync || IsDisposed) return;
        _sync = true;
        var changed = !_reportedIndices.SequenceEqual(Selection.SelectedIndices) || !_reportedItems.SequenceEqual(Selection.SelectedItems);
        try
        {
            SelectedIndex = Selection.SelectedIndex;
            var indices = Selection.SelectedIndices.ToHashSet();
            foreach (var item in RealizedContainers.OfType<ListBoxItem>()) item.IsSelected = indices.Contains(IndexOfItemContainer(item));
        }
        finally { _sync = false; }
        if (changed)
        {
            _reportedIndices = Selection.SelectedIndices.ToArray(); _reportedItems = Selection.SelectedItems.ToArray();
            SelectedItemChanged?.Invoke(SelectedItem); RaiseEvent(new(SelectionChangedEvent));
        }
    }
    public void BringItemIntoView(int index)
    {
        if (index < 0 || index >= ItemValues.Count) return;
        if (_virtualizing && _virtualItems is not null) { _virtualItems.BringIndexIntoView(index); return; }
        _pendingBringIndex = index;
        ApplyPendingBringIntoView();
    }
    protected internal override void ArrangeCore(Rect final)
    {
        base.ArrangeCore(final);
        ApplyPendingBringIntoView();
    }
    private void ApplyPendingBringIntoView()
    {
        if (_pendingBringIndex is not { } index) return;
        if (FindItemContainer(index) is not { } item) return;
        if (item.FinalRect.Height <= 0) return;
        for (var parent = Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not ScrollViewer viewer) continue;
            _pendingBringIndex = null;
            var top = viewer.PointFromRoot(Point.Zero).Y - item.PointFromRoot(Point.Zero).Y;
            var previous = viewer.VerticalOffset;
            if (top < viewer.Padding) viewer.ScrollToVerticalOffset(viewer.VerticalOffset + top - viewer.Padding);
            else if (top + item.FinalRect.Height > viewer.Padding + viewer.ViewportHeight)
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset + top + item.FinalRect.Height - viewer.Padding - viewer.ViewportHeight);
            // A first-layout request can occur while the ancestor is arranging. Its
            // measure flag survives that arrangement and applies the new content offset.
            if (Math.Abs(viewer.VerticalOffset - previous) > .001) viewer.InvalidateMeasure();
            break;
        }
    }
    protected override void OnDispose()
    {
        try { base.OnDispose(); }
        finally { if (_virtualItems is { IsDisposed: false } panel) panel.Dispose(); }
    }
}
