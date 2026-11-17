using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI;

namespace EclipseUI.Elements;

/// <summary>
/// 列表虚拟化（G4 布局切片）：只实例化视口可见（含上下各 1 个缓冲）的 item 容器，
/// 滚动时在 <see cref="ArrangeCore"/> 里同步窗口——滚动只触发 Arrange 失效，
/// 测量被约束短路，窗口同步必须挂在排列。
/// <list type="bullet">
/// <item>条目来源：<see cref="ItemCount"/> + <see cref="ItemFactory"/>（index → 容器元素）；
///   数据或工厂变更后调 <see cref="Refresh"/>。提供 ItemUpdater 后回收并重新绑定，否则出窗即释放。</item>
/// <item>均匀行高：虚拟模式行高取 item 0 容器的测量期望（<see cref="UniformItemHeight"/>）；
///   IsVariableHeight 启用实测高度估计；ItemHeightProvider 可提供精确高度。</item>
/// <item>放在有限视口的 <see cref="ScrollViewer"/> 内启用虚拟化；没有滚动祖先时
///   退化为全量实例化的纵向堆叠（与 StackPanel 同布局，变高可用）。</item>
/// <item>滚轮/偏移由外层 <see cref="ScrollViewer"/> 承担；本面板只按窗口摆放容器。</item>
/// </list>
/// </summary>
public class VirtualizingStackPanel : Panel
{
    public static readonly StyledProperty<double> GapProperty =
        StyledProperty<double>.Register<VirtualizingStackPanel>("Gap", 0d,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public static readonly StyledProperty<int> ItemCountProperty =
        StyledProperty<int>.Register<VirtualizingStackPanel>("ItemCount", 0,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public int ItemCount
    {
        get => GetValue(ItemCountProperty);
        set => SetValue(ItemCountProperty, value);
    }

    /// <summary>条目工厂：index → 容器元素。换实现后调 <see cref="Refresh"/>。</summary>
    public Func<int, Element>? ItemFactory { get; set; }
    /// <summary>Rebinds a detached recycled container to a new index. Without an updater rows are disposed.</summary>
    public Action<Element, int>? ItemUpdater { get; set; }
    internal Action<Element>? ItemRetired { get; set; }
    public Func<int, double>? ItemHeightProvider { get; set; }
    public static readonly StyledProperty<bool> IsVariableHeightProperty = StyledProperty<bool>.Register<VirtualizingStackPanel>("IsVariableHeight", false, new() { Invalidate = InvalidateKind.Measure });
    public bool IsVariableHeight { get => GetValue(IsVariableHeightProperty); set => SetValue(IsVariableHeightProperty, value); }
    public Element? GetRealizedItem(int index) => _realized.GetValueOrDefault(index);
    /// <summary>Returns the current measured or estimated item bounds in panel coordinates.</summary>
    public Rect GetItemBounds(int index)
    {
        if (index < 0 || index >= ItemCount) throw new ArgumentOutOfRangeException(nameof(index));
        var variable = Variable && _heights.Length == ItemCount;
        return new(Padding, Padding + (variable ? Prefix(index) : index * (_itemHeight + Math.Max(0, Gap))),
            Math.Max(0, FinalRect.Width - Padding * 2), variable ? _heights[index] : _itemHeight);
    }
    /// <summary>Shows an item using measured heights, refining the position when an estimated row is realized.</summary>
    public void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= ItemCount) throw new ArgumentOutOfRangeException(nameof(index));
        _bringIndex = index;
        _bringOffset = null;
        if (FindScrollViewer() is { } owner) ApplyBringIntoView(owner);
        InvalidateArrange();
    }
    private int? _bringIndex;
    private double? _bringOffset;
    private bool ApplyBringIntoView(ScrollViewer owner)
    {
        if (_bringIndex is not { } index || index >= ItemCount || _itemHeight <= 0 || owner.ViewportHeight <= 0) return false;
        // A user scroll after this request takes precedence over a pending estimate correction.
        if (_bringOffset is { } previous && Math.Abs(owner.GetValue(ScrollViewer.VerticalOffsetProperty) - previous) > .001)
        { _bringIndex = null; _bringOffset = null; return false; }
        var bounds = GetItemBounds(index);
        var panelInOwner = owner.PointFromRoot(Point.Zero).Y - PointFromRoot(Point.Zero).Y;
        var top = owner.VerticalOffset + panelInOwner + bounds.Y - owner.Padding;
        var offset = owner.VerticalOffset;
        if (top < offset || bounds.Height > owner.ViewportHeight) offset = top;
        else if (top + bounds.Height > offset + owner.ViewportHeight) offset = top + bounds.Height - owner.ViewportHeight;
        offset = Math.Max(0, offset);
        var changed = Math.Abs(owner.VerticalOffset - offset) > .001;
        if (changed)
        {
            // The new extent can exceed the previous ScrollableHeight while rows are measured.
            owner.VerticalOffset = offset;
            owner.InvalidateMeasure();
        }
        _bringOffset = owner.GetValue(ScrollViewer.VerticalOffsetProperty);
        return changed;
    }
    public int RecycledCount => _recycled.Count;
    private readonly Stack<Element> _recycled = new();
    private double[] _heights = [], _heightTree = [];
    private double _heightWidth = double.NaN;
    private double _heightGap = double.NaN;
    private bool Variable => IsVariableHeight || ItemHeightProvider is not null;
    private void PrepareHeights(int count, double width, double gap)
    {
        if (_heights.Length == count && _heightWidth == width && _heightGap == gap) return;
        _heightWidth = width; _heightGap = gap;
        _heights = new double[count]; _heightTree = new double[count + 1];
        for (var i = 0; i < count; i++)
        {
            var h = ItemHeightProvider?.Invoke(i) ?? _itemHeight;
            if (!double.IsFinite(h) || h < 0) throw new InvalidOperationException("Item heights must be finite and non-negative.");
            _heights[i] = Math.Max(.1, h);
            _heightTree[i + 1] += _heights[i] + gap;
            var parent = (i + 1) + ((i + 1) & -(i + 1));
            if (parent <= count) _heightTree[parent] += _heightTree[i + 1];
        }
    }
    private double Prefix(int count)
    {
        double sum = 0;
        for (var i = count; i > 0; i -= i & -i) sum += _heightTree[i];
        return sum;
    }
    private int HeightIndex(double offset)
    {
        var lo = 0; var hi = _heights.Length;
        while (lo < hi) { var mid = (lo + hi) / 2; if (Prefix(mid + 1) <= offset) lo = mid + 1; else hi = mid; }
        return Math.Min(lo, Math.Max(0, _heights.Length - 1));
    }
    private void RecordHeight(int index, double height)
    {
        height = Math.Max(.1, height);
        var delta = height - _heights[index]; if (Math.Abs(delta) < .001) return;
        _heights[index] = height;
        for (var i = index + 1; i < _heightTree.Length; i += i & -i) _heightTree[i] += delta;
        InvalidateMeasure();
    }

    /// <summary>当前已实例化（在可视树中）的容器数——应 ≈ 视口行数 + 2 缓冲，与 ItemCount 无关。</summary>
    public int RealizedCount => _realized.Count;

    /// <summary>虚拟模式的均匀行高（item 0 测量期望；无条目为 0）。</summary>
    public double UniformItemHeight => _itemHeight;

    private readonly Dictionary<int, Element> _realized = new();
    private Element? _probe;            // item 0 容器缓存：量行高，不随窗口进出重建
    private double _itemHeight;
    private int _probedForCount = -1;   // 行高缓存对应的 ItemCount；工厂/数据变更由 Refresh 清

    /// <summary>数据或工厂变更后调用：清行高与窗口缓存并触发重排（旧 item 0 容器一并丢弃）。</summary>
    public void Refresh()
    {
        if (IsDisposed) return;
        _bringIndex = null; _bringOffset = null;
        var retired = new HashSet<Element>(_realized.Values, ReferenceEqualityComparer.Instance);
        if (_probe is not null) retired.Add(_probe);
        foreach (var row in _recycled) retired.Add(row);
        _recycled.Clear(); _heights = []; _heightTree = []; _heightWidth = double.NaN;
        _realized.Clear();
        _probe = null;
        _itemHeight = 0;
        _probedForCount = -1;
        InvalidateMeasure();
        DisposeItems(retired);
    }

    /// <summary>Refreshes item positions while retaining matched visible containers and their editor state.</summary>
    public void Refresh(Func<Element, int?> indexOfItem)
    {
        ArgumentNullException.ThrowIfNull(indexOfItem);
        if (IsDisposed) return;
        var next = new Dictionary<int, Element>();
        var retired = new HashSet<Element>(_realized.Values, ReferenceEqualityComparer.Instance);
        if (_probe is not null) retired.Add(_probe);
        foreach (var row in _recycled) retired.Add(row);
        foreach (var row in _realized.Values)
            if (indexOfItem(row) is { } index && index >= 0 && index < ItemCount && next.TryAdd(index, row)) retired.Remove(row);
        _realized.Clear();
        foreach (var pair in next) _realized.Add(pair.Key, pair.Value);
        _recycled.Clear(); _heights = []; _heightTree = []; _heightWidth = double.NaN;
        _probe = null; _itemHeight = 0; _probedForCount = -1;
        _bringIndex = null; _bringOffset = null;
        Children.ReplaceAll(next.OrderBy(pair => pair.Key).Select(pair => pair.Value));
        InvalidateMeasure();
        DisposeItems(retired);
    }

    // ItemsControl already owns these containers and can transfer them back to ordinary layout.
    internal void AdoptItemContainers(IEnumerable<KeyValuePair<int, Element>> containers)
    {
        foreach (var pair in containers) _realized.Add(pair.Key, pair.Value);
        Children.ReplaceAll(_realized.OrderBy(pair => pair.Key).Select(pair => pair.Value));
        InvalidateMeasure();
    }

    internal void ReleaseItemContainers()
    {
        _realized.Clear(); _recycled.Clear(); _probe = null;
        _heights = []; _heightTree = []; _heightWidth = double.NaN;
        _itemHeight = 0; _probedForCount = -1; _bringIndex = null; _bringOffset = null;
        Children.Clear(); InvalidateMeasure();
    }

    protected internal override Size MeasureCore(Size available)
    {
        var padding = Padding;
        var availW = Math.Max(0, available.Width - padding * 2);
        var infiniteWidth = double.IsPositiveInfinity(availW);
        var availH = Math.Max(0, available.Height - padding * 2);
        var count = Math.Max(0, ItemCount);
        var gap = Math.Max(0, Gap);

        var owner = FindScrollViewer();
        // Grid's exploratory infinite measure must not discard the real viewport's
        // measured heights on every pass. Keep that width until a new finite viewport arrives.
        if (owner is not null && infiniteWidth && double.IsFinite(_heightWidth)) availW = _heightWidth;

        // 退化模式（无滚动祖先）：全量实例化，纵向堆叠（变高可用）。
        // 注意不能用 availH 判断——ScrollViewer 内容的可用高恒为无穷。
        if (owner is null)
        {
            DiscardWindowExceptRange(0, ItemFactory is null ? -1 : count - 1);
            double width = 0, height = 0;
            var ordered = new List<Element>(count);
            if (ItemFactory is not null)
            {
                for (int i = 0; i < count; i++)
                {
                    var child = GetOrRealize(i);
                    ordered.Add(child);
                    child.Measure(new Size(availW, double.PositiveInfinity));
                    var desired = child.DesiredSize;
                    width = Math.Max(width, desired.Width);
                    height += desired.Height + (i > 0 ? gap : 0);
                }
            }
            Children.ReplaceAll(ordered);
            _itemHeight = ordered.Count > 0 ? (height - Math.Max(0, count - 1) * gap) / count : 0;
            return new Size(
                double.IsPositiveInfinity(availW) ? width + padding * 2 : availW + padding * 2,
                height + padding * 2);
        }

        // 虚拟模式：行高取 item 0 测量期望；extent = count × (行高 + gap) - gap。
        // 窗口同步不在这里做（测量期拿不到本次帧的视口/偏移），统一挂在 ArrangeCore。
        EnsureProbe(count, availW);
        var stride = _itemHeight + gap;
        if (Variable) PrepareHeights(count, availW, gap);
        var extent = count > 0 ? Variable ? Prefix(count) - gap : stride * count - gap : 0;
        return new Size(
            infiniteWidth ? widthOfProbe() + padding * 2 : availW + padding * 2,
            extent + padding * 2);

        double widthOfProbe() => _probe?.DesiredSize.Width ?? 0;
    }

    protected internal override void ArrangeCore(Rect final)
    {
        var padding = Padding;
        var gap = Math.Max(0, Gap);
        var count = Math.Max(0, ItemCount);
        var owner = FindScrollViewer();

        if (owner is null || double.IsPositiveInfinity(final.Height))
        {
            // 退化模式：按测量期望逐个堆叠（与 StackPanel 同布局）。
            double y = padding;
            foreach (var child in Children)
            {
                child.Arrange(new Rect(padding, y, child.DesiredSize.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height + gap;
            }
            return;
        }

        // 虚拟模式：按 owner 当前偏移/视口同步窗口（含上下各 1 缓冲），再按绝对行位摆放。
        // Nested list padding and other content before this panel shift the viewport in
        // panel coordinates. The owner's raw scroll offset is not an item offset.
        var panelInOwner = owner.PointFromRoot(Point.Zero).Y - PointFromRoot(Point.Zero).Y;
        var visibleTop = owner.Padding - panelInOwner - padding;
        var offset = Math.Max(0, visibleTop);
        var viewportH = Math.Max(0, visibleTop + owner.ViewportHeight - offset);
        var stride = _itemHeight + gap;

        int lo = 0, hi = -1;
        if (count > 0 && stride > 0)
        {
            lo = Variable ? Math.Max(0, HeightIndex(offset) - 1) : Math.Clamp((int)Math.Floor(offset / stride) - 1, 0, count - 1);
            hi = Variable ? Math.Min(count - 1, HeightIndex(offset + viewportH) + 2) : Math.Clamp((int)Math.Ceiling((offset + viewportH) / stride) + 1, 0, count - 1);
        }

        DiscardWindowExceptRange(lo, hi);

        var innerW = Math.Max(0, final.Width - padding * 2);
        var heightChanged = false;
        for (int i = lo; i <= hi; i++)
        {
            var child = GetOrRealize(i);
            child.Measure(new Size(innerW, double.PositiveInfinity));
            if (Variable && ItemHeightProvider is null)
            {
                heightChanged |= Math.Abs(_heights[i] - Math.Max(.1, child.DesiredSize.Height)) >= .001;
                RecordHeight(i, child.DesiredSize.Height);
            }
            child.Arrange(new Rect(padding, padding + (Variable ? Prefix(i) : i * stride), innerW, Variable ? _heights[i] : _itemHeight));
        }
        if (_bringIndex is { } requested)
        {
            var adjusted = ApplyBringIntoView(owner);
            if (!adjusted && !heightChanged && _realized.ContainsKey(requested))
            { _bringIndex = null; _bringOffset = null; }
        }
        if (heightChanged) owner.InvalidateMeasure();
    }

    private ScrollViewer? FindScrollViewer()
    {
        for (var node = Parent; node is not null; node = node.Parent)
            if (node is ScrollViewer viewer)
                return viewer;
        return null;
    }

    private void EnsureProbe(int count, double availW)
    {
        if (count == 0 || ItemFactory is null)
        {
            _itemHeight = 0;
            return;
        }
        // A panel moved from the nonvirtual mode already owns its item-zero instance.
        if (_probe is null && _realized.TryGetValue(0, out var existing))
        {
            _probe = existing;
            _probedForCount = count;
        }
        if (_probe is null || _probedForCount != count)
        {
            var retired = _probe;
            _probe = null;
            _realized.Remove(0);
            _probedForCount = -1;
            if (retired is not null) DisposeItem(retired);
            _probe = ItemFactory(0);
        }
        _probe.EnsureInitialized();
        _probe.SetThemeDefaults(ThemeDefaults);
        _probe.Measure(new Size(availW, double.PositiveInfinity));
        _probedForCount = count;
        _itemHeight = _probe.DesiredSize.Height;
    }

    private Element GetOrRealize(int index)
    {
        if (index == 0 && _probe is not null)
        {
            if (!_realized.ContainsKey(0))
            {
                _realized[0] = _probe;
                Children.Add(_probe);
            }
            return _probe;
        }

        if (_realized.TryGetValue(index, out var existing)) return existing;

        Element created;
        if (ItemUpdater is not null && _recycled.TryPop(out var recycled))
        {
            created = recycled;
            try { ItemUpdater(created, index); } catch { DisposeItem(created); throw; }
        }
        else created = ItemFactory!(index);
        _realized[index] = created;
        Children.Add(created);
        return created;
    }

    /// <summary>释放窗口外容器；item 0 只摘除并退回探针缓存，回到视口时复用。</summary>
    private void DiscardWindowExceptRange(int lo, int hi)
    {
        var doomed = _realized.Where(entry => entry.Key < lo || entry.Key > hi).ToArray();
        foreach (var (index, child) in doomed)
        {
            _realized.Remove(index);
            if (index == 0) { _probe = child; _probedForCount = ItemCount; }
        }
        List<Exception>? failures = null;
        foreach (var (index, child) in doomed.Reverse())
        {
            try
            {
                if (index == 0) Children.Remove(child);
                else if (ItemUpdater is not null && _recycled.Count < 256) { Children.Remove(child); _recycled.Push(child); }
                else DisposeItem(child);
            }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures is not null)
            throw new AggregateException("Virtualized item retirement failed.", failures);
    }

    protected override void OnDispose()
    {
        var probe = _probe;
        _probe = null;
        _realized.Clear();
        _itemHeight = 0;
        _probedForCount = -1;
        try { DisposeItems(_recycled.Append(probe).OfType<Element>().Distinct(ReferenceEqualityComparer.Instance).Cast<Element>()); _recycled.Clear(); }
        finally { base.OnDispose(); }
    }

    private void DisposeItem(Element item)
    {
        try { item.Dispose(); }
        finally { ItemRetired?.Invoke(item); }
    }

    private void DisposeItems(IEnumerable<Element> items)
    {
        List<Exception>? failures = null;
        foreach (var item in items.Reverse())
        {
            try { DisposeItem(item); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures is not null)
            throw new AggregateException("Virtualized item cleanup failed.", failures);
    }
}
