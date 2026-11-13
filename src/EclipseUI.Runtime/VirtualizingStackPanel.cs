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
///   数据或工厂变更后调 <see cref="Refresh"/>。容器复用（pooling）留给后续切片——v1 出窗即释放。</item>
/// <item>均匀行高：虚拟模式行高取 item 0 容器的测量期望（<see cref="UniformItemHeight"/>）；
///   变高行与像素精确的 extent 估计留给后续切片。</item>
/// <item>放在 <see cref="ScrollViewer"/>（或任何有限高度祖先）内即虚拟化；可用高度无限时
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
        var retired = new HashSet<Element>(_realized.Values, ReferenceEqualityComparer.Instance);
        if (_probe is not null) retired.Add(_probe);
        _realized.Clear();
        _probe = null;
        _itemHeight = 0;
        _probedForCount = -1;
        InvalidateMeasure();
        DisposeItems(retired);
    }

    protected internal override Size MeasureCore(Size available)
    {
        var padding = Padding;
        var availW = Math.Max(0, available.Width - padding * 2);
        var availH = Math.Max(0, available.Height - padding * 2);
        var count = Math.Max(0, ItemCount);
        var gap = Math.Max(0, Gap);

        var owner = FindScrollViewer();

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
        var extent = count > 0 ? stride * count - gap : 0;
        return new Size(
            double.IsPositiveInfinity(availW) ? widthOfProbe() + padding * 2 : availW + padding * 2,
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
        var viewportH = Math.Max(0, owner.ViewportHeight - padding * 2);
        var offset = Math.Clamp(owner.VerticalOffset, 0, Math.Max(0, owner.ScrollableHeight));
        var stride = _itemHeight + gap;

        int lo = 0, hi = -1;
        if (count > 0 && stride > 0)
        {
            lo = Math.Clamp((int)Math.Floor(offset / stride) - 1, 0, count - 1);
            hi = Math.Clamp((int)Math.Ceiling((offset + viewportH) / stride) + 1, 0, count - 1);
        }

        DiscardWindowExceptRange(lo, hi);

        var innerW = Math.Max(0, final.Width - padding * 2);
        for (int i = lo; i <= hi; i++)
        {
            var child = GetOrRealize(i);
            child.Measure(new Size(innerW, double.PositiveInfinity));
            child.Arrange(new Rect(padding, padding + i * stride, innerW, _itemHeight));
        }
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
            retired?.Dispose();
            _probe = ItemFactory(0);
        }
        _probe.EnsureInitialized();
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

        var created = ItemFactory!(index);
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
                else child.Dispose();
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
        try { probe?.Dispose(); }
        finally { base.OnDispose(); }
    }

    private static void DisposeItems(IEnumerable<Element> items)
    {
        List<Exception>? failures = null;
        foreach (var item in items.Reverse())
        {
            try { item.Dispose(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures is not null)
            throw new AggregateException("Virtualized item cleanup failed.", failures);
    }
}
