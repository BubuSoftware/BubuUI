using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI;

public enum RoutingStrategy
{
    Tunnel,
    Bubble,
}

public sealed class RoutedEvent
{
    public RoutedEvent(string name, RoutingStrategy strategy = RoutingStrategy.Bubble)
    {
        Name = name;
        Strategy = strategy;
    }

    public string Name { get; }
    public RoutingStrategy Strategy { get; }

    public override string ToString() => Name;
}

public class RoutedEventArgs
{
    public RoutedEventArgs(RoutedEvent routedEvent) => RoutedEvent = routedEvent;

    public RoutedEvent RoutedEvent { get; }
    public bool Handled { get; set; }
    public Element? Source { get; set; }
}

public sealed class ElementCollection : IReadOnlyList<Element>
{
    private readonly List<Element> _items = new();
    private readonly Element _owner;

    internal ElementCollection(Element owner) => _owner = owner;

    public int Count => _items.Count;
    public Element this[int index] => _items[index];

    public void Add(Element child)
    {
        if (child._parent is not null)
            throw new InvalidOperationException($"Element '{child}' already has a parent.");
        child._parent = _owner;
        _items.Add(child);
        child._needsMeasure = true;
        child._needsArrange = true;
        _owner.InvalidateMeasure();
        _owner.InvalidateRender();
    }

    public bool Remove(Element child)
    {
        if (_items.Remove(child))
        {
            child._parent = null;
            _owner.InvalidateMeasure();
            _owner.InvalidateRender();
            return true;
        }
        return false;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        foreach (var child in _items)
            child._parent = null;
        _items.Clear();
        _owner.InvalidateMeasure();
        _owner.InvalidateRender();
    }

    public IEnumerator<Element> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// 元素：唯一参与布局/绘制/输入的节点。属性引擎是唯一真相源，
/// 变更只标脏不做布局，布局在帧调度时按脏标记增量执行。
/// </summary>
public abstract class Element
{
    internal Element? _parent;
    private Size _desired;
    private Rect _final;
    private Size _lastMeasureConstraint;
    internal bool _needsMeasure = true;
    internal bool _needsArrange = true;
    private long _renderVersion;
    private Dictionary<StyledProperty, object?>? _localValues;
    private Dictionary<RoutedEvent, List<Action<RoutedEventArgs>>>? _handlers;

    public Element? Parent => _parent;
    public Size DesiredSize => _desired;
    public Rect FinalRect => _final;

    /// <summary>
    /// 视觉版本：任何可能改变像素的变更（属性、按下态、焦点、子树增删）都会递增。
    /// 脏区跟踪把它与上一帧矩形快照结合使用：版本变化一定进脏区，
    /// 版本不变也可能因布局移动进入脏区。
    /// </summary>
    public long RenderVersion => _renderVersion;

    /// <summary>属性变更通知（值短路：相等不触发）。</summary>
    public event Action<Element, StyledProperty>? PropertyChanged;

    // 输入与焦点路由事件声明在 Element 上（design/14 §14.4.3）：继承是路由事件的语义基础，
    // 放在静态宿主类里不参与目录扫描，模板事件表面就只剩 Button.OnClick。

    public static readonly RoutedEvent PointerPressedEvent = new RoutedEvent("PointerPressed", RoutingStrategy.Bubble);
    public static readonly RoutedEvent PointerReleasedEvent = new RoutedEvent("PointerReleased", RoutingStrategy.Bubble);
    public static readonly RoutedEvent PointerMovedEvent = new RoutedEvent("PointerMoved", RoutingStrategy.Bubble);
    public static readonly RoutedEvent KeyDownEvent = new RoutedEvent("KeyDown", RoutingStrategy.Bubble);
    public static readonly RoutedEvent KeyUpEvent = new RoutedEvent("KeyUp", RoutingStrategy.Bubble);
    public static readonly RoutedEvent GotFocusEvent = new RoutedEvent("GotFocus", RoutingStrategy.Bubble);
    public static readonly RoutedEvent LostFocusEvent = new RoutedEvent("LostFocus", RoutingStrategy.Bubble);

    public T GetValue<T>(StyledProperty<T> property)
    {
        if (_localValues is not null && _localValues.TryGetValue(property, out var boxed))
            return (T)boxed!;
        if (property.Inherits && _parent is not null)
            return _parent.GetValue(property);
        return property.DefaultValue;
    }

    public void SetValue<T>(StyledProperty<T> property, T value)
    {
        if (_localValues is not null &&
            _localValues.TryGetValue(property, out var old) &&
            EqualityComparer<T>.Default.Equals((T)old!, value))
            return;

        (_localValues ??= new Dictionary<StyledProperty, object?>())[property] = value;
        PropertyChanged?.Invoke(this, property);
        InvalidateRender();
        switch (property.Invalidate)
        {
            case InvalidateKind.Measure: InvalidateMeasure(); break;
            case InvalidateKind.Arrange: InvalidateArrange(); break;
        }
        if (property.Inherits)
            NotifyInheritedChange(property);
    }

    private void NotifyInheritedChange(StyledProperty property)
    {
        foreach (var descendant in Descendants())
        {
            descendant.PropertyChanged?.Invoke(descendant, property);
            descendant.InvalidateRender();
            if (property.Invalidate == InvalidateKind.Measure)
                descendant.InvalidateMeasure();
        }
    }

    public IEnumerable<Element> Descendants()
    {
        foreach (var child in VisualChildren)
        {
            yield return child;
            foreach (var descendant in child.Descendants())
                yield return descendant;
        }
    }

    internal virtual IEnumerable<Element> VisualChildren
    {
        get { yield break; }
    }

    /// <summary>
    /// 绘制透明：节点仍参与布局（G3 才做真正的布局透明），但不产生自己的背景/边框。
    /// 块协调器（<c>@if</c> / <c>@foreach</c>）覆盖为 true，避免把模板块画成卡片。
    /// </summary>
    public virtual bool IsVisualTransparent => false;

    /// <summary>沿祖先链标脏（脏标记传播：祖先的 measure 依赖子树的 desired）。</summary>
    public void InvalidateMeasure()
    {
        var node = this;
        while (node is not null && !node._needsMeasure)
        {
            node._needsMeasure = true;
            node = node._parent;
        }
    }

    public void InvalidateArrange()
    {
        var node = this;
        while (node is not null && !node._needsArrange)
        {
            node._needsArrange = true;
            node = node._parent;
        }
    }

    /// <summary>标记视觉内容已变（不涉及布局）；脏区跟踪据此判断重绘范围。</summary>
    public void InvalidateRender() => _renderVersion++;

    public void Measure(Size available)
    {
        if (!_needsMeasure && available == _lastMeasureConstraint) return;
        _lastMeasureConstraint = available;
        _desired = MeasureCore(available);
        _needsMeasure = false;
        _needsArrange = true;
    }

    public void Arrange(Rect final)
    {
        if (!_needsArrange && final == _final) return;
        _final = final;
        ArrangeCore(final);
        _needsArrange = false;
    }

    protected internal abstract Size MeasureCore(Size available);
    protected internal abstract void ArrangeCore(Rect final);

    // ---- 路由事件 ----

    public void AddHandler(RoutedEvent routedEvent, Action<RoutedEventArgs> handler)
    {
        var handlers = _handlers ??= new Dictionary<RoutedEvent, List<Action<RoutedEventArgs>>>();
        if (!handlers.TryGetValue(routedEvent, out var list))
            handlers[routedEvent] = list = new List<Action<RoutedEventArgs>>();
        list.Add(handler);
    }

    /// <summary>不关心事件参数的便捷重载（注意：此形态不可 RemoveHandler）。</summary>
    public void AddHandler(RoutedEvent routedEvent, Action handler)
        => AddHandler(routedEvent, _ => handler());

    public void RemoveHandler(RoutedEvent routedEvent, Action<RoutedEventArgs> handler)
    {
        if (_handlers is not null && _handlers.TryGetValue(routedEvent, out var list))
            list.Remove(handler);
    }

    public void RaiseEvent(RoutedEventArgs args)
    {
        if (args.Source is null) args.Source = this;
        if (args.RoutedEvent.Strategy == RoutingStrategy.Bubble)
        {
            for (Element? node = this; node is not null && !args.Handled; node = node._parent)
                node.InvokeHandlers(args);
        }
        else
        {
            var chain = new List<Element>();
            for (Element? node = this; node is not null; node = node._parent)
                chain.Add(node);
            chain.Reverse();
            foreach (var node in chain)
            {
                node.InvokeHandlers(args);
                if (args.Handled) break;
            }
        }
    }

    private void InvokeHandlers(RoutedEventArgs args)
    {
        if (_handlers is null || !_handlers.TryGetValue(args.RoutedEvent, out var list)) return;
        var snapshot = list.ToArray();
        foreach (var handler in snapshot)
        {
            handler(args);
            if (args.Handled) return;
        }
    }

    // ---- 命中测试 ----

    public static readonly StyledProperty<bool> IsHitTestVisibleProperty =
        StyledProperty<bool>.Register<Element>("IsHitTestVisible", true);

    public bool IsHitTestVisible
    {
        get => GetValue(IsHitTestVisibleProperty);
        set => SetValue(IsHitTestVisibleProperty, value);
    }

    /// <summary>控件是否可聚焦（Tab 遍历与键盘路由的依据）。</summary>
    public virtual bool IsFocusable => false;

    /// <summary>
    /// 命中测试：点在根坐标系；返回最深命中元素（子元素从后往前，即视觉最上层优先）。
    /// 调用前提：该元素已被 Arrange（FinalRect 有效）。
    /// </summary>
    public Element? HitTest(Point point)
    {
        if (!IsHitTestVisible) return null;
        if (!new Rect(default, FinalRect.Size).Contains(PointFromRoot(point))) return null;
        foreach (var child in VisualChildren.Reverse())
        {
            if (child.HitTest(point) is { } hit)
                return hit;
        }
        return this;
    }

    /// <summary>根坐标 → 本元素局部坐标（沿父链累减 FinalRect 原点）。</summary>
    public Point PointFromRoot(Point rootPoint)
    {
        double x = rootPoint.X, y = rootPoint.Y;
        for (Element? node = this; node is not null; node = node._parent)
        {
            x -= node.FinalRect.X;
            y -= node.FinalRect.Y;
        }
        return new Point(x, y);
    }

    public override string ToString() => GetType().Name;
}
