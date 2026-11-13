using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;

namespace EclipseUI.Elements;

public sealed class DataGridColumn : INotifyPropertyChanged
{
    private string _header = "", _width = "*", _binding = "";
    public string Header { get => _header; set { _header = value; PropertyChanged?.Invoke(this, new(nameof(Header))); } }
    public string Width { get => _width; set { _width = value; PropertyChanged?.Invoke(this, new(nameof(Width))); } }
    public string Binding { get => _binding; set { _binding = value; PropertyChanged?.Invoke(this, new(nameof(Binding))); } }
    public Func<object?, object?>? ValueGetter { get; set; }
    public Action<object?, object?>? ValueSetter { get; set; }
    public Func<object?, Element>? CellTemplate { get; set; }
    public bool IsReadOnly { get; set; }
    public bool CanUserSort { get; set; } = true;
    public event PropertyChangedEventHandler? PropertyChanged;
    public object? GetValue(object? item)
    {
        if (ValueGetter is not null) return ValueGetter(item);
        var current = item;
        if (Binding.Length == 0) return current;
        foreach (var part in Binding.Split('.')) current = current?.GetType().GetProperty(part, BindingFlags.Public | BindingFlags.Instance)?.GetValue(current);
        return current;
    }
    public bool TrySetValue(object? item, string text)
    {
        if (IsReadOnly || item is null) return false;
        try
        {
            if (ValueSetter is not null) { ValueSetter(item, text); return true; }
            var parts = Binding.Split('.'); object? owner = item;
            foreach (var part in parts[..^1]) owner = owner?.GetType().GetProperty(part)?.GetValue(owner);
            var property = owner?.GetType().GetProperty(parts[^1]); if (property?.CanWrite != true) return false;
            var target = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var value = text.Length == 0 && Nullable.GetUnderlyingType(property.PropertyType) is not null ? null
                : target.IsEnum ? Enum.Parse(target, text, true) : Convert.ChangeType(text, target, CultureInfo.InvariantCulture);
            property.SetValue(owner, value); return true;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException or ArgumentException or TargetInvocationException) { return false; }
    }
}

public class DataGrid : Panel
{
    private readonly Grid _header = new() { RowDefinitions = "Auto" };
    private readonly VirtualizingStackPanel _rows = new() { IsVariableHeight = true };
    private readonly ScrollViewer _scroll = new();
    private readonly List<object?> _view = [];
    private ObservableCollection<DataGridColumn> _columns = [];
    private readonly HashSet<DataGridColumn> _subscribed = [];
    private IEnumerable? _source;
    private INotifyCollectionChanged? _observable;
    private int _sortColumn = -1;
    private bool _ascending = true;
    public static readonly StyledProperty<int> SelectedIndexProperty = StyledProperty<int>.Register<DataGrid>("SelectedIndex", -1, new() { Invalidate = InvalidateKind.Render });
    public static readonly StyledProperty<bool> IsReadOnlyProperty = StyledProperty<bool>.Register<DataGrid>("IsReadOnly", true, new() { Invalidate = InvalidateKind.Measure });
    public static readonly RoutedEvent SelectionChangedEvent = new("SelectionChanged");
    public static readonly RoutedEvent CellEditCommittedEvent = new("CellEditCommitted");
    public static readonly RoutedEvent CellEditRejectedEvent = new("CellEditRejected");
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public object? SelectedItem => SelectedIndex >= 0 && SelectedIndex < _view.Count ? _view[SelectedIndex] : null;
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public ObservableCollection<DataGridColumn> Columns
    {
        get => _columns;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _columns.CollectionChanged -= ColumnsChanged; _columns = value; _columns.CollectionChanged += ColumnsChanged; RebuildColumns();
        }
    }
    public IEnumerable? ItemsSource
    {
        get => _source;
        set
        {
            if (_observable is not null) _observable.CollectionChanged -= SourceChanged;
            _source = value; _observable = value as INotifyCollectionChanged;
            if (_observable is not null) _observable.CollectionChanged += SourceChanged;
            RefreshItems();
        }
    }
    public IReadOnlyList<object?> View => _view.AsReadOnly();
    public int RealizedRowCount => _rows.RealizedCount;
    public override bool IsFocusable => true;
    public DataGrid()
    {
        _scroll.Children.Add(_rows); Children.Add(_header); Children.Add(_scroll);
        _rows.ItemFactory = CreateRow; _rows.ItemUpdater = UpdateRow;
        _columns.CollectionChanged += ColumnsChanged;
        PropertyChanged += (_, p) =>
        {
            if (p == IsReadOnlyProperty) _rows.Refresh();
            if (p == SelectedIndexProperty)
            {
                foreach (var row in _rows.Children.OfType<ListBoxItem>()) row.IsSelected = row.Item is RowIndex index && index.Index == SelectedIndex;
                RaiseEvent(new(SelectionChangedEvent));
            }
        };
        AddHandler(KeyDownEvent, e =>
        {
            if (e is not KeyEventArgs k || _view.Count == 0) return;
            if (k.Key == Key.Down) SelectedIndex = Math.Min(_view.Count - 1, SelectedIndex + 1);
            else if (k.Key == Key.Up) SelectedIndex = Math.Max(0, SelectedIndex - 1);
            else if (k.Key == Key.Home) SelectedIndex = 0;
            else if (k.Key == Key.End) SelectedIndex = _view.Count - 1;
            else return;
            var y = SelectedIndex * 36d;
            if (y < _scroll.VerticalOffset) _scroll.ScrollToVerticalOffset(y);
            else if (y + 36 > _scroll.VerticalOffset + _scroll.ViewportHeight) _scroll.ScrollToVerticalOffset(y + 36 - _scroll.ViewportHeight);
            e.Handled = true;
        });
    }
    private sealed class HeaderButton : Button
    {
        internal int SortDirection;
        public override void Render(IElementDrawingContext c) { if (SortDirection != 0) ControlIcons.Chevron(c, new(FinalRect.Width - 12, FinalRect.Height / 2), up: SortDirection > 0); }
    }
    private sealed record RowIndex(int Index);
    private void ColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildColumns();
    private void ColumnChanged(object? sender, PropertyChangedEventArgs e) => RebuildColumns();
    private void RebuildColumns()
    {
        foreach (var column in _subscribed) column.PropertyChanged -= ColumnChanged;
        _subscribed.Clear();
        foreach (var column in _columns) { column.PropertyChanged += ColumnChanged; _subscribed.Add(column); }
        var previous = _header.Children.ToArray(); _header.Children.Clear(); foreach (var child in previous) child.Dispose();
        _header.ColumnDefinitions = string.Join(',', _columns.Select(c => c.Width));
        for (var i = 0; i < _columns.Count; i++)
        {
            var index = i; var button = new HeaderButton { Label = _columns[i].Header, SortDirection = _sortColumn == i ? _ascending ? 1 : -1 : 0 };
            Grid.SetColumn(button, i); button.AddHandler(Button.ClickEvent, _ => Sort(index)); _header.Children.Add(button);
        }
        _rows.Refresh(); InvalidateMeasure();
    }
    private void SourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshItems();
    public void RefreshItems()
    {
        var selected = SelectedItem; _view.Clear(); if (_source is not null) _view.AddRange(_source.Cast<object?>());
        if (_sortColumn >= 0 && _sortColumn < Columns.Count) ApplySort();
        SelectedIndex = selected is null ? -1 : _view.FindIndex(i => Equals(i, selected));
        _rows.ItemCount = _view.Count; _rows.Refresh(); InvalidateMeasure();
    }
    public void Sort(int column)
    {
        if (column < 0 || column >= Columns.Count) throw new ArgumentOutOfRangeException(nameof(column));
        if (!Columns[column].CanUserSort) return;
        var selected = SelectedItem; _ascending = _sortColumn == column ? !_ascending : true; _sortColumn = column;
        ApplySort(); SelectedIndex = selected is null ? -1 : _view.FindIndex(i => Equals(i, selected)); RebuildColumns();
    }
    private void ApplySort()
    {
        var column = Columns[_sortColumn];
        int Compare(object? a, object? b)
        {
            var av = column.GetValue(a); var bv = column.GetValue(b);
            if (ReferenceEquals(av, bv)) return 0; if (av is null) return -1; if (bv is null) return 1;
            if (av.GetType() == bv.GetType() && av is IComparable comparable) return comparable.CompareTo(bv);
            return StringComparer.CurrentCulture.Compare(av.ToString(), bv.ToString());
        }
        var ordered = _view.Select((item, index) => (item, index)).OrderBy(p => p.item, Comparer<object?>.Create((a, b) => _ascending ? Compare(a, b) : Compare(b, a))).Select(p => p.item).ToArray();
        _view.Clear(); _view.AddRange(ordered);
    }
    private Element CreateRow(int index)
    {
        var row = new ListBoxItem { Padding = 2 };
        row.AddHandler(PreviewPointerPressedEvent, _ => { if (row.Item is RowIndex current) SelectedIndex = current.Index; });
        UpdateRow(row, index); return row;
    }
    private void UpdateRow(Element element, int index)
    {
        var row = (ListBoxItem)element; row.Item = new RowIndex(index); row.IsSelected = index == SelectedIndex;
        var old = row.Children.ToArray(); row.Children.Clear(); foreach (var child in old) child.Dispose();
        var grid = new Grid { ColumnDefinitions = string.Join(',', Columns.Select(c => c.Width)) };
        for (var i = 0; i < Columns.Count; i++)
        {
            var column = Columns[i]; var item = _view[index]; Element cell;
            if (column.CellTemplate is not null) cell = column.CellTemplate(item);
            else if (!IsReadOnly && !column.IsReadOnly)
            {
                var editor = new CellEditor { Text = Convert.ToString(column.GetValue(item), CultureInfo.InvariantCulture) ?? "" };
                editor.CommitAction = () => CommitEdit(item, column, editor);
                editor.AddHandler(LostFocusEvent, _ => CommitEdit(item, column, editor));
                editor.AddHandler(KeyDownEvent, e => { if (e is KeyEventArgs { Key: Key.Enter }) { CommitEdit(item, column, editor); e.Handled = true; } });
                cell = editor;
            }
            else cell = ItemsControl.MakeText(Convert.ToString(column.GetValue(item), CultureInfo.InvariantCulture) ?? "");
            Grid.SetColumn(cell, i); grid.Children.Add(cell);
        }
        row.Children.Add(grid);
    }
    private sealed class CellEditor : TextBox
    {
        internal Action? CommitAction;
        protected override void OnDetached() { CommitAction?.Invoke(); base.OnDetached(); }
    }
    private void CommitEdit(object? item, DataGridColumn column, TextBox editor)
    {
        if (editor.Text == (Convert.ToString(column.GetValue(item), CultureInfo.InvariantCulture) ?? "")) return;
        var success = column.TrySetValue(item, editor.Text);
        if (!success) editor.Text = Convert.ToString(column.GetValue(item), CultureInfo.InvariantCulture) ?? "";
        RaiseEvent(new(success ? CellEditCommittedEvent : CellEditRejectedEvent));
    }
    protected internal override Size MeasureCore(Size available)
    {
        _header.Measure(available); _scroll.Measure(new(available.Width, Math.Max(0, available.Height - _header.DesiredSize.Height)));
        return new(Math.Max(_header.DesiredSize.Width, _scroll.DesiredSize.Width), _header.DesiredSize.Height + _scroll.DesiredSize.Height);
    }
    protected internal override void ArrangeCore(Rect final)
    {
        var h = Math.Min(final.Height, _header.DesiredSize.Height); _header.Arrange(new(0, 0, final.Width, h)); _scroll.Arrange(new(0, h, final.Width, final.Height - h));
    }
    protected override void OnDispose()
    {
        if (_observable is not null) _observable.CollectionChanged -= SourceChanged;
        _columns.CollectionChanged -= ColumnsChanged; foreach (var column in _subscribed) column.PropertyChanged -= ColumnChanged;
        base.OnDispose();
    }
}
