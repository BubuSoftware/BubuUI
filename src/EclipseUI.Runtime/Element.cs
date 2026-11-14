using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EclipseUI.Elements;

namespace EclipseUI;

public enum RoutingStrategy
{
    Tunnel,
    Bubble,

    /// <summary>只在目标元素自身分发，不沿父链。</summary>
    Direct,
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
        ArgumentNullException.ThrowIfNull(child);
        UiThreadGuard.AssertUiThread(_owner, "ElementCollection.Add");
        ValidateChild(child, allowCurrent: false);
        child.EnsureInitialized();
        // User initialization can dispose the owner or attach this child elsewhere.
        ValidateChild(child, allowCurrent: false);
        child._parent = _owner;
        child._parentCollection = this;
        _items.Add(child);
        child.ReconnectUpdates();
        child._needsMeasure = true;
        child._needsArrange = true;
        _owner.InvalidateMeasure();
        _owner.InvalidateRender();
        List<Exception>? failures = null;
        try { child._focusManager?.RefreshFocusPath(); } catch (Exception error) { (failures ??= new()).Add(error); }
        try { _owner.NotifyFocusTreeChanged(); } catch (Exception error) { (failures ??= new()).Add(error); }
        try { child.AttachToParent(_owner); } catch (Exception error) { (failures ??= new()).Add(error); }
        _owner.OnChildrenChanged();
        if (failures is not null) throw new AggregateException("Content attachment notifications failed.", failures);
    }

    public bool Remove(Element child)
    {
        ArgumentNullException.ThrowIfNull(child);
        UiThreadGuard.AssertUiThread(_owner, "ElementCollection.Remove");
        for (var i = 0; i < _items.Count; i++)
        {
            if (!ReferenceEquals(_items[i], child)) continue;
            var previousRoot = child.SchedulingRoot;
            _items.RemoveAt(i);
            child._parent = null;
            child._parentCollection = null;
            child.ReconnectUpdates(previousRoot);
            _owner.InvalidateMeasure();
            _owner.InvalidateRender();
            List<Exception>? failures = null;
            try { _owner.NotifyFocusTreeChanged(); } catch (Exception error) { (failures ??= new()).Add(error); }
            try { if (child.UpdateParent is null) child.DetachFromTree(); } catch (Exception error) { (failures ??= new()).Add(error); }
            _owner.OnChildrenChanged();
            if (failures is not null) throw new AggregateException("Content removal notifications failed.", failures);
            return true;
        }
        return false;
    }

    public void Clear()
    {
        UiThreadGuard.AssertUiThread(_owner, "ElementCollection.Clear");
        if (_items.Count == 0) return;
        var previousRoot = _owner.SchedulingRoot;
        var removed = _items.ToArray();
        _items.Clear();
        foreach (var child in removed)
        {
            child._parent = null;
            child._parentCollection = null;
            child.ReconnectUpdates(previousRoot);
        }
        _owner.InvalidateMeasure();
        _owner.InvalidateRender();
        List<Exception>? failures = null;
        try { _owner.NotifyFocusTreeChanged(); } catch (Exception error) { (failures ??= new()).Add(error); }
        foreach (var child in removed.Reverse())
        {
            try { if (child.UpdateParent is null) child.DetachFromTree(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        _owner.OnChildrenChanged();
        if (failures is not null)
            throw new AggregateException("Content removal lifecycle notifications failed.", failures);
    }

    /// <summary>Commits a new order while preserving attachment of retained child instances.</summary>
    internal void ReplaceAll(IEnumerable<Element> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        UiThreadGuard.AssertUiThread(_owner, "ElementCollection.ReplaceAll");
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);
        var next = children.ToArray();
        var nextSet = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        foreach (var child in next)
        {
            ArgumentNullException.ThrowIfNull(child);
            if (!nextSet.Add(child))
                throw new InvalidOperationException("A content collection cannot contain the same element twice.");
            ValidateChild(child, allowCurrent: true);
        }
        foreach (var child in next) child.EnsureInitialized();
        // Initialization may have changed ownership, including an earlier child's ownership.
        foreach (var child in next) ValidateChild(child, allowCurrent: true);
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);
        if (_items.Count == next.Length && _items.Zip(next).All(pair => ReferenceEquals(pair.First, pair.Second)))
            return;

        var previousSet = new HashSet<Element>(_items, ReferenceEqualityComparer.Instance);
        var removed = _items.Where(child => !nextSet.Contains(child)).ToArray();
        var added = next.Where(child => !previousSet.Contains(child)).ToArray();
        var previousRoot = _owner.SchedulingRoot;
        _items.Clear();
        _items.AddRange(next);
        foreach (var child in removed)
        {
            child._parent = null;
            child._parentCollection = null;
            child.ReconnectUpdates(previousRoot);
        }
        foreach (var child in added)
        {
            child._parent = _owner;
            child._parentCollection = this;
            child.ReconnectUpdates();
            child._needsMeasure = true;
            child._needsArrange = true;
        }
        _owner.InvalidateMeasure();
        _owner.InvalidateRender();

        List<Exception>? failures = null;
        foreach (var child in added)
        {
            try { child._focusManager?.RefreshFocusPath(); } catch (Exception error) { (failures ??= new()).Add(error); }
        }
        try { _owner.NotifyFocusTreeChanged(); } catch (Exception error) { (failures ??= new()).Add(error); }
        foreach (var child in removed.Reverse())
        {
            try { if (child.UpdateParent is null) child.DetachFromTree(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        foreach (var child in added)
        {
            // Earlier callbacks may have removed or already attached a newly added child.
            if (!ReferenceEquals(child._parentCollection, this) || child.IsAttached) continue;
            try { child.AttachToParent(_owner); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        _owner.OnChildrenChanged();
        if (failures is not null)
            throw new AggregateException("Content replacement lifecycle notifications failed.", failures);
    }

    private void ValidateChild(Element child, bool allowCurrent)
    {
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);
        ObjectDisposedException.ThrowIf(child.IsDisposed, child);
        var current = allowCurrent && ReferenceEquals(child._parentCollection, this);
        if (!current && child._parent is not null)
            throw new InvalidOperationException($"Element '{child}' already has a parent.");
        if (child._contentOwner is not null)
            throw new InvalidOperationException($"Element '{child}' already belongs to another content owner.");
        for (UiObject? ancestor = _owner; ancestor is not null; ancestor = ancestor.UpdateParent)
            if (ReferenceEquals(ancestor, child))
                throw new InvalidOperationException("An element cannot contain itself or an ancestor.");
        if (!current) child.ValidateCanAttach();
    }

    // foreach 走 List<T>.Enumerator struct（S5 预算：不装箱）；接口实现保留给
    // IReadOnlyList<T> 契约与 LINQ 路径——返回接口会把 struct enumerator 装箱，
    // 布局/绘制热路径每次 foreach children 都会分配。
    public List<Element>.Enumerator GetEnumerator() => _items.GetEnumerator();
    IEnumerator<Element> IEnumerable<Element>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// 元素：唯一参与布局/绘制/输入的节点。属性引擎是唯一真相源，
/// 变更只标脏不做布局，布局在帧调度时按脏标记增量执行。
/// </summary>
public abstract class Element : UiObject
{
    internal Element? _parent;
    internal FocusManager? _focusManager;
    internal ElementCollection? _parentCollection;
    internal UiObject? _contentOwner;
    private Size _desired;
    private Rect _final;
    private Size _lastMeasureConstraint;
    // 内容需求与向父级报告的受约束尺寸分开保存。排列不能把内容需求传播回父级槽。
    private Size _coreDesired;
    private Size _lastCoreMeasureConstraint;
    private bool _hasMeasured;
    private Rect _lastArrangeSlot;
    internal bool _needsMeasure = true;
    internal bool _needsArrange = true;
    private long _renderVersion;
    private Dictionary<StyledProperty, IValueBox>? _localValues;
    private Dictionary<StyledProperty, IValueBox>? _templateValues;
    private int _templateValueDepth;
    private Dictionary<StyledProperty, IValueBox>? _styleValues;
    private IReadOnlyList<string>? _classes;
    private IReadOnlyList<string>? _templateClasses;
    private IReadOnlyList<string>? _localClasses;
    private bool _isHovered;
    private bool _isPressed;
    private bool _isFocused, _isFocusWithin, _isFocusVisible;
    private bool _isEnabled = true;
    private Dictionary<RoutedEvent, List<Action<RoutedEventArgs>>>? _handlers;
    // 宿主安装 UiSynchronizationContext 时只绑定到根元素；Debug 线程亲和断言沿父链查找，
    // 因此块内动态创建的元素无需再登记（design/15 §15.14.3）。

    public Element? Parent => _parent;
    public bool RequestFocus(FocusOrigin origin = FocusOrigin.Auto, FocusVisibility visibility = FocusVisibility.Auto)
    {
        var root = this;
        while (root.Parent is not null) root = root.Parent;
        return root._focusManager?.Focus(this, origin, visibility) ?? false;
    }
    internal override UiObject? UpdateParent => (UiObject?)_parent ?? _contentOwner;
    protected internal override IEnumerable<UiObject> UiChildren => VisualChildren;
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

    public static readonly RoutedEvent PreviewKeyDownEvent = new("PreviewKeyDown", RoutingStrategy.Tunnel);
    public static readonly RoutedEvent PreviewPointerPressedEvent = new("PreviewPointerPressed", RoutingStrategy.Tunnel);
    public static readonly RoutedEvent TextInputEvent = new("TextInput", RoutingStrategy.Bubble);
    public static readonly RoutedEvent TextCompositionEvent = new("TextComposition", RoutingStrategy.Bubble);
    public static readonly RoutedEvent PointerEnteredEvent = new("PointerEntered", RoutingStrategy.Direct);
    public static readonly RoutedEvent PointerExitedEvent = new("PointerExited", RoutingStrategy.Direct);
    public virtual bool UsesDefaultDrawing => true;
    public virtual void Render(IElementDrawingContext context) { }
    /// <summary>Local clip for this element's own drawing; focus decorations are drawn separately.</summary>
    public virtual Rect? GetDrawingClip() => new Rect(Point.Zero, FinalRect.Size);
    /// <summary>Local child clip, shared by painting, hit testing and navigation focus.</summary>
    public virtual Rect? GetContentClip() => this is Panel { ClipToBounds: true } ? new Rect(Point.Zero, FinalRect.Size) : null;
    public static readonly StyledProperty<bool> IsVisibleProperty = StyledProperty<bool>.Register<Element>("IsVisible", true,
        new PropertyOptions { Invalidate = InvalidateKind.Measure });
    public bool IsVisible { get => GetValue(IsVisibleProperty); set => SetValue(IsVisibleProperty, value); }
    public bool IsEffectivelyVisible => IsVisible && (this is not Elements.Popup popup || popup.IsOpen) && (Parent?.IsEffectivelyVisible ?? true);

    public static readonly RoutedEvent PointerPressedEvent = new RoutedEvent("PointerPressed", RoutingStrategy.Bubble);
    public static readonly RoutedEvent PointerReleasedEvent = new RoutedEvent("PointerReleased", RoutingStrategy.Bubble);
    public static readonly RoutedEvent PointerMovedEvent = new RoutedEvent("PointerMoved", RoutingStrategy.Bubble);
    public static readonly RoutedEvent PointerWheelChangedEvent = new RoutedEvent("PointerWheelChanged", RoutingStrategy.Bubble);
    public static readonly RoutedEvent KeyDownEvent = new RoutedEvent("KeyDown", RoutingStrategy.Bubble);
    public static readonly RoutedEvent KeyUpEvent = new RoutedEvent("KeyUp", RoutingStrategy.Bubble);
    public static readonly RoutedEvent GotFocusEvent = new RoutedEvent("GotFocus", RoutingStrategy.Bubble);
    public static readonly RoutedEvent LostFocusEvent = new RoutedEvent("LostFocus", RoutingStrategy.Bubble);

    private IElementThemeDefaults? _themeDefaults;

    /// <summary>Defaults inherited by the actual content subtree; changing skins also invalidates layout.</summary>
    public IElementThemeDefaults? ThemeDefaults => _themeDefaults ?? _parent?.ThemeDefaults;

    public void SetThemeDefaults(IElementThemeDefaults? value)
    {
        UiThreadGuard.AssertUiThread(this, nameof(SetThemeDefaults));
        if (ReferenceEquals(_themeDefaults, value)) return;
        _themeDefaults = value;
        InvalidateMeasure(); InvalidateRender();
        foreach (var element in Descendants()) { element.InvalidateMeasure(); element.InvalidateRender(); }
    }

    public T GetValue<T>(StyledProperty<T> property)
    {
        // Effective values: local → template → class → inherited → scoped theme → property default.
        // 命中值盒：强类型读取，无拆箱。类型不匹配（正常 API 下不可达）时退化到下一层。
        if (_localValues is not null &&
            _localValues.TryGetValue(property, out var box) &&
            box is ValueBox<T> typedBox)
            return typedBox.Value;
        if (_templateValues is not null &&
            _templateValues.TryGetValue(property, out var templateBox) &&
            templateBox is ValueBox<T> templateTyped)
            return templateTyped.Value;
        if (_styleValues is not null &&
            _styleValues.TryGetValue(property, out var styleBox) &&
            styleBox is ValueBox<T> styleTyped)
            return styleTyped.Value;
        if (property.Inherits && _parent is not null)
            return _parent.GetValue(property);
        return ThemeDefaults is { } theme ? theme.GetDefault(this, property) : property.DefaultValue;
    }

    public void SetValue<T>(StyledProperty<T> property, T value)
    {
        UiThreadGuard.AssertUiThread(this, nameof(SetValue));
        if (_templateValueDepth > 0)
        {
            SetTemplateValue(property, value);
            return;
        }
        if (_localValues is not null &&
            _localValues.TryGetValue(property, out var existing) &&
            existing is ValueBox<T> typedBox)
        {
            // 命中已有 ValueBox<T>：相等短路；否则只改 .Value 字段，不新建对象，
            // 消除重复赋值时的装箱与分配。
            if (EqualityComparer<T>.Default.Equals(typedBox.Value, value))
                return;
            typedBox.Value = value;
        }
        else
        {
            // 首次设置（或类型不匹配的防御性重建）才 new。
            (_localValues ??= new Dictionary<StyledProperty, IValueBox>())[property] = new ValueBox<T>(value);
        }

        ApplyValueChange(property);
    }

    /// <summary>Initializes a template root value without replacing a caller's local override.</summary>
    public void SetTemplateValue<T>(StyledProperty<T> property, T value)
    {
        UiThreadGuard.AssertUiThread(this, nameof(SetTemplateValue));
        var previous = GetValue(property);
        if (_templateValues is not null &&
            _templateValues.TryGetValue(property, out var existing) && existing is ValueBox<T> typed)
        {
            if (EqualityComparer<T>.Default.Equals(typed.Value, value)) return;
            typed.Value = value;
        }
        else
            (_templateValues ??= new())[property] = new ValueBox<T>(value);
        if (!EqualityComparer<T>.Default.Equals(previous, GetValue(property)))
        {
            var previousDepth = _templateValueDepth;
            _templateValueDepth = 0;
            try { ApplyValueChange(property); }
            finally { _templateValueDepth = previousDepth; }
        }
    }

    /// <summary>Routes normal CLR setters into the template layer, preserving their validation and coercion.</summary>
    public TemplateValueScope BeginTemplateValues()
    {
        UiThreadGuard.AssertUiThread(this, nameof(BeginTemplateValues));
        var previousDepth = _templateValueDepth;
        _templateValueDepth++;
        return new TemplateValueScope(this, previousDepth);
    }

    public struct TemplateValueScope : IDisposable
    {
        private Element? _owner;
        private readonly int _previousDepth;

        internal TemplateValueScope(Element owner, int previousDepth)
        {
            _owner = owner;
            _previousDepth = previousDepth;
        }

        public void Dispose()
        {
            if (_owner is not { } owner) return;
            owner._templateValueDepth = _previousDepth;
            _owner = null;
        }
    }

    /// <summary>
    /// 写入 class 样式层的值（S-B）：优先级低于 local（<see cref="SetValue"/>）、
    /// 高于继承与默认值。由 <see cref="IStyleProvider"/> 的样式应用调用——
    /// 模板作者与控件代码不直接使用（样式经 <see cref="Classes"/> 声明）。
    /// 失效语义与 <see cref="SetValue"/> 完全一致。
    /// </summary>
    public void SetStyleValue<T>(StyledProperty<T> property, T value)
    {
        if (_styleValues is not null &&
            _styleValues.TryGetValue(property, out var existing) &&
            existing is ValueBox<T> typedBox)
        {
            if (EqualityComparer<T>.Default.Equals(typedBox.Value, value))
                return;
            typedBox.Value = value;
        }
        else
        {
            (_styleValues ??= new Dictionary<StyledProperty, IValueBox>())[property] = new ValueBox<T>(value);
        }

        ApplyValueChange(property);
    }

    /// <summary>
    /// 清除 local 层的一个值（「显式取消覆盖」语义，S-B）：下一次 <see cref="GetValue{T}"/>
    /// 落到 class 层/继承/默认。对未设置的属性是 no-op。
    /// </summary>
    public void ClearLocalValue<T>(StyledProperty<T> property)
    {
        if (_localValues?.Remove(property) == true)
            ApplyValueChange(property);
    }

    /// <summary>清除全部 class 样式值并按失效级别触发重算（Classes 变化时由提供者调用）。</summary>
    public void ClearStyleValues()
    {
        if (_styleValues is not { Count: > 0 })
            return;
        var affected = new List<StyledProperty>(_styleValues.Keys);
        _styleValues = null;
        foreach (var property in affected)
            ApplyValueChange(property);
    }

    private void ApplyValueChange(StyledProperty property)
    {
        PropertyChanged?.Invoke(this, property);
        InvalidateRender();
        switch (property.Invalidate)
        {
            case InvalidateKind.Measure: InvalidateMeasure(); break;
            case InvalidateKind.Arrange: InvalidateArrange(); break;
        }
        if (property.Inherits)
            NotifyInheritedChange(property);
        if (property == IsVisibleProperty) NotifyFocusTreeChanged();
    }

    /// <summary>
    /// 伪类状态掩码（D102，CSS 伪类对应物）：<see cref="IsHovered"/>/<see cref="IsPressed"/>/
    /// <see cref="IsFocused"/>/<see cref="IsFocusVisible"/>/<see cref="IsFocusWithin"/> 的合成，供样式匹配与绘制段键使用。变化即失效：
    /// 渲染重算 + class 层样式重应用（伪类变体按当前状态重选）。
    /// E3：<see cref="PseudoStates.Disabled"/> 位在此合成（**有效值**——沿祖先链继承，
    /// 父禁用则子也禁用），使变体匹配与段键对其零特殊处理。
    /// E5：注册制伪类位（<see cref="PseudoStatesRegistry"/> 分配）同样直接 OR 进本掩码——
    /// 变体匹配与段键对注册位零特殊处理。
    /// </summary>
    public PseudoStates PseudoStates =>
        (_isHovered ? PseudoStates.Hover : PseudoStates.None) |
        (_isPressed ? PseudoStates.Pressed : PseudoStates.None) |
        (_isFocused ? PseudoStates.Focused : PseudoStates.None) |
        (_isFocusVisible ? PseudoStates.FocusVisible : PseudoStates.None) |
        (_isFocusWithin ? PseudoStates.FocusWithin : PseudoStates.None) |
        (IsEffectiveEnabled ? PseudoStates.None : PseudoStates.Disabled) |
        _registeredStates;

    /// <summary>指针悬停（CSS :hover）。由 <see cref="InputRouter"/> 在命中目标变化时维护。</summary>
    public bool IsHovered
    {
        get => _isHovered;
        internal set => SetPseudoState(PseudoStates.Hover, value, ref _isHovered);
    }

    /// <summary>指针按下未释放（CSS :active）。由 <see cref="InputRouter"/> 在捕获期间维护。</summary>
    public bool IsPressed
    {
        get => _isPressed;
        internal set => SetPseudoState(PseudoStates.Pressed, value, ref _isPressed);
    }

    /// <summary>键盘焦点（CSS :focus）。由 <see cref="FocusManager"/> 维护；仅可聚焦元素成立。</summary>
    public bool IsFocused => _isFocused;
    public bool IsFocusWithin => _isFocusWithin;
    public bool IsFocusVisible => _isFocusVisible;
    public virtual bool IsTextInputClient => false;
    [UiStyleProperty(Owner = typeof(Element))]
    public static readonly StyledProperty<FocusVisualMode> FocusVisualModeProperty = StyledProperty<FocusVisualMode>.Register<Element>("FocusVisualMode", FocusVisualMode.Auto, new() { Invalidate = InvalidateKind.Render });
    public FocusVisualMode FocusVisualMode { get => GetValue(FocusVisualModeProperty); set => SetValue(FocusVisualModeProperty, value); }
    protected virtual FocusVisualMode DefaultFocusVisualMode => FocusVisualMode.Outline;
    public FocusVisualMode EffectiveFocusVisualMode => FocusVisualMode == FocusVisualMode.Auto ? DefaultFocusVisualMode : FocusVisualMode;
    public virtual FocusVisualGeometry GetFocusVisualGeometry() => new(this, new Rect(Point.Zero, FinalRect.Size));
    // Composite controls can delegate the outline to an ancestor shell without moving input ownership.
    internal Element? FocusVisualOwner { get; set; }
    public virtual Element FocusVisualTarget => FocusVisualOwner ?? this;
    /// <summary>Control-mode focus decoration, drawn after children in local coordinates.</summary>
    public virtual void RenderFocusVisual(IElementDrawingContext context) { }
    internal virtual void CancelPointerInteraction() { }

    internal bool SetFocusState(bool focused, bool within, bool visible)
    {
        if (_isFocused == focused && _isFocusWithin == within && _isFocusVisible == visible) return false;
        _isFocused = focused; _isFocusWithin = within; _isFocusVisible = visible;
        return true;
    }
    internal void PublishFocusState() { InvalidateRender(); if (!IsDisposed) ReapplyStyles(); }
    internal FocusManager? FindFocusManager()
    {
        var root = this; while (root.Parent is not null) root = root.Parent;
        return root._focusManager;
    }
    internal void NotifyFocusTreeChanged() => FindFocusManager()?.RefreshFocusPath();

    /// <summary>
    /// 自身启用声明值（E3，默认 true）。**有效值沿祖先链继承**——任一祖先为 false 则
    /// 后代全部有效禁用（桌面标准语义：禁用面板 = 面板内全部控件禁用）。
    /// 变化传播：有效状态变化影响整棵子树的 <see cref="PseudoStates.Disabled"/> 位，
    /// 逐后代渲染失效 + class 层重应用（变体按新掩码重选，段键含状态位自动重建）。
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            NotifyFocusTreeChanged();
            InvalidateRender();
            ReapplyStyles();
            foreach (var descendant in Descendants())
            {
                descendant.InvalidateRender();
                descendant.ReapplyStyles();
            }
        }
    }

    /// <summary>有效启用状态：<see cref="IsEnabled"/> 沿祖先链的合取（E3）。</summary>
    public bool IsEffectiveEnabled
    {
        get
        {
            for (Element? node = this; node is not null; node = node._parent)
                if (!node._isEnabled)
                    return false;
            return true;
        }
    }

    private void SetPseudoState(PseudoStates state, bool value, ref bool field)
    {
        if (field == value) return;
        field = value;
        InvalidateRender(); // 显式失效兜底：变体移除时重应用可能值相等短路（SetStyleValue 同值不触发）
        ReapplyStyles();
    }

    /// <summary>注册制伪类位（E5，design/17 §3.2）——<see cref="PseudoStates"/> 的注册位存储。</summary>
    private PseudoStates _registeredStates;

    /// <summary>
    /// 翻转注册制伪类位（E5，design/17 §3.2）：第三方/控件自定义状态（<c>:loading</c>…）
    /// 的唯一写入通道。<paramref name="mask"/> 必须是 <see cref="PseudoStatesRegistry.Register"/>
    /// 分配位的非空子集——掩码即凭证：平台位与伪造位一律拒绝（启动期注册，运行期 O(1) 位测试）。
    /// 变化即失效：渲染重算 + class 层重应用（变体按新掩码重选，段键含状态位自动重建）。
    /// </summary>
    public void SetRegisteredState(PseudoStates mask, bool value)
    {
        if (!PseudoStatesRegistry.IsRegisteredMask(mask))
            throw new ArgumentException(
                $"pseudo-state mask {mask} is not a non-empty subset of bits allocated by " +
                "PseudoStatesRegistry.Register; platform bits and arbitrary values are rejected.",
                nameof(mask));
        var next = value ? _registeredStates | mask : _registeredStates & ~mask;
        if (next == _registeredStates) return;
        _registeredStates = next;
        InvalidateRender();
        ReapplyStyles();
    }

    /// <summary>
    /// 样式类（S-B）：经 <see cref="Styling.Provider"/> 应用样式表；类集变化时
    /// 先清 class 层值再重新应用（local 值不受影响）。Provider 未装配时记录声明、
    /// 装配后补应用（宿主可晚于建树装配）。伪类变体（D102）由提供者按
    /// <see cref="PseudoStates"/> 当前值选择——状态变化走 <see cref="ReapplyStyles"/> 重选。
    /// </summary>
    public IReadOnlyList<string> Classes
    {
        get => _classes ?? Array.Empty<string>();
        set => ApplyClasses(value);
    }

    public void ApplyClasses(IReadOnlyList<string>? names)
    {
        var next = names ?? Array.Empty<string>();
        if ((_localClasses ?? Array.Empty<string>()).SequenceEqual(next)) return;
        _localClasses = next.Count == 0 ? null : next.ToArray();
        RebuildClasses();
    }

    public void ApplyTemplateClasses(IReadOnlyList<string>? names)
    {
        var next = names ?? Array.Empty<string>();
        if ((_templateClasses ?? Array.Empty<string>()).SequenceEqual(next)) return;
        _templateClasses = next.Count == 0 ? null : next.ToArray();
        RebuildClasses();
    }

    private void RebuildClasses()
    {
        var next = (_templateClasses ?? Array.Empty<string>()).Concat(_localClasses ?? Array.Empty<string>()).ToArray();
        if ((_classes ?? Array.Empty<string>()).SequenceEqual(next)) return;
        _classes = next.Length == 0 ? null : next;
        if (Styling.Provider is null)
        {
            var index = Styling.PendingClasses.FindIndex(p => ReferenceEquals(p.Element, this));
            var entry = (this, _classes ?? Array.Empty<string>());
            if (index < 0) Styling.PendingClasses.Add(entry);
            else Styling.PendingClasses[index] = entry;
            return;
        }
        ReapplyStyles();
    }

    /// <summary>清 class 层并按当前类集重应用（类集变化与伪类状态变化共用）。</summary>
    private void ReapplyStyles()
    {
        var provider = Styling.Provider;
        if (provider is null) return;
        provider.ClearStyles(this);
        if (_classes is null) return;
        foreach (var cls in _classes)
            provider.ApplyClass(this, cls);
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
    /// 绘制透明：不产生自己的背景/边框。块协调器（<c>@if</c> / <c>@foreach</c>）覆盖为 true，
    /// 避免把模板块画成卡片。与 <see cref="IsLayoutTransparent"/> 相互独立。
    /// </summary>
    public virtual bool IsVisualTransparent => false;

    /// <summary>
    /// 布局透明：节点仍留在元素树里（坐标链、命中测试、脏区都不变），但不引入自己的内边距与间距，
    /// 子元素按最近布局祖先的排布参数落位，空内容不占间距槽。块协调器覆盖为 true
    /// （spec/01 §4.10/§4.11，台账 D77）。
    /// </summary>
    public virtual bool IsLayoutTransparent => false;

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

    /// <summary>
    /// 测量先扣 Margin 并应用本地尺寸要求，使换行使用正确宽度。
    /// 内容需求加回 Margin 后受 available 限制，不能扩大父级的约束。
    /// </summary>
    public void Measure(Size available)
    {
        if (double.IsNaN(available.Width) || double.IsNaN(available.Height) ||
            available.Width < 0 || available.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(available), "Measure constraints must be non-negative or positive infinity.");
        EnsureInitialized();
        if (!IsVisible || this is Elements.Popup { IsOpen: false })
        {
            _desired = _coreDesired = Size.Empty;
            _lastMeasureConstraint = available;
            _hasMeasured = true;
            _needsMeasure = false;
            _needsArrange = true;
            return;
        }
        if (!_needsMeasure && available == _lastMeasureConstraint) return;
        var margin = Margin;
        var inner = margin == 0
            ? available
            : new Size(Math.Max(0, available.Width - margin * 2), Math.Max(0, available.Height - margin * 2));
        var constraint = ApplySizeConstraints(inner);
        var content = MeasureCore(constraint);
        ValidateMeasuredSize(content);
        _coreDesired = ApplySizeConstraints(content);
        ValidateMeasuredSize(_coreDesired);
        _desired = new Size(
            Math.Min(available.Width, Math.Max(0, _coreDesired.Width + margin * 2)),
            Math.Min(available.Height, Math.Max(0, _coreDesired.Height + margin * 2)));
        ValidateMeasuredSize(_desired);
        _lastMeasureConstraint = available;
        _lastCoreMeasureConstraint = constraint;
        _hasMeasured = true;
        _needsMeasure = false;
        _needsArrange = true;
    }

    /// <summary>
    /// 排列（design/05 §布局契约）：<paramref name="slot"/> 是父给的本元素槽（含 Margin 区），
    /// 先扣 Margin 得内槽，再按对齐档与显式尺寸落核心矩形——<see cref="FinalRect"/> 即核心矩形
    /// （绘制、命中测试、脏区都读它）。Stretch 同样受显式尺寸和 Min/Max 约束。
    /// </summary>
    public void Arrange(Rect slot)
    {
        if (!double.IsFinite(slot.X) || !double.IsFinite(slot.Y) ||
            !double.IsFinite(slot.Width) || !double.IsFinite(slot.Height) || slot.Width < 0 || slot.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(slot), "Arrange slots must be finite with non-negative sizes.");
        if (!_hasMeasured) Measure(slot.Size);
        else if (_needsMeasure) Measure(_lastMeasureConstraint);
        if (!_needsArrange && slot == _lastArrangeSlot) return;

        var margin = Margin;
        var inner = margin == 0
            ? slot
            : new Rect(slot.X + margin, slot.Y + margin,
                Math.Max(0, slot.Width - margin * 2), Math.Max(0, slot.Height - margin * 2));

        var size = ArrangeSize(inner.Size, margin);
        // 父级最终槽可能不同于测量报价。按实际宽度受控重测一次，让子面板的高度、
        // 文本断行和绘制一致；保留纵向测量条件，不把完整内容高度截成排列槽高度。
        if (size.Width != _lastCoreMeasureConstraint.Width &&
            (size.Width > _lastCoreMeasureConstraint.Width || size.Width < _coreDesired.Width))
        {
            Measure(new Size(Math.Max(0, size.Width + margin * 2), _lastMeasureConstraint.Height));
            size = ArrangeSize(inner.Size, margin);
        }
        var w = size.Width;
        var h = size.Height;

        var x = HorizontalAlignment switch
        {
            HorizontalAlignment.Left => inner.X,
            HorizontalAlignment.Center => inner.X + (inner.Width - w) / 2,
            HorizontalAlignment.Right => inner.Right - w,
            _ => inner.X + (inner.Width - w) / 2,
        };
        var y = VerticalAlignment switch
        {
            VerticalAlignment.Top => inner.Y,
            VerticalAlignment.Center => inner.Y + (inner.Height - h) / 2,
            VerticalAlignment.Bottom => inner.Bottom - h,
            _ => inner.Y + (inner.Height - h) / 2,
        };

        _final = new Rect(x, y, w, h);
        ArrangeCore(_final);
        _lastArrangeSlot = slot;
        _needsArrange = false;
    }

    private Size ArrangeSize(Size slot, double margin) => ApplySizeConstraints(new Size(
        HorizontalAlignment == HorizontalAlignment.Stretch ? slot.Width : Math.Min(slot.Width, Math.Max(0, _desired.Width - margin * 2)),
        VerticalAlignment == VerticalAlignment.Stretch ? slot.Height : Math.Min(slot.Height, Math.Max(0, _desired.Height - margin * 2))));

    private static void ValidateMeasuredSize(Size size)
    {
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width < 0 || size.Height < 0)
            throw new InvalidOperationException("Measured sizes must be finite and non-negative.");
    }

    protected internal virtual void OnChildrenChanged() { }

    protected internal abstract Size MeasureCore(Size available);
    protected internal abstract void ArrangeCore(Rect final);

    // ---- 布局契约（design/05 §布局契约，Layoutable 级统一面）----

    /// <summary>均匀外边距（四边同值，与 Padding 同款标量口径）。计入 DesiredSize，
    /// 排列期从槽内扣除。父面板无需感知——DesiredSize 已含。</summary>
    public static readonly StyledProperty<double> MarginProperty =
        StyledProperty<double>.Register<Element>("Margin", 0d,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    /// <summary>显式宽度（NaN = 未设置，取测量期望/拉伸）。</summary>
    public static readonly StyledProperty<double> WidthProperty =
        StyledProperty<double>.Register<Element>("Width", double.NaN,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    /// <summary>显式高度（NaN = 未设置）。</summary>
    public static readonly StyledProperty<double> HeightProperty =
        StyledProperty<double>.Register<Element>("Height", double.NaN,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public static readonly StyledProperty<double> MinWidthProperty =
        StyledProperty<double>.Register<Element>("MinWidth", 0d,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public static readonly StyledProperty<double> MaxWidthProperty =
        StyledProperty<double>.Register<Element>("MaxWidth", double.PositiveInfinity,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public static readonly StyledProperty<double> MinHeightProperty =
        StyledProperty<double>.Register<Element>("MinHeight", 0d,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public static readonly StyledProperty<double> MaxHeightProperty =
        StyledProperty<double>.Register<Element>("MaxHeight", double.PositiveInfinity,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    /// <summary>水平对齐（默认 Stretch，填满槽后统一应用尺寸约束）。</summary>
    public static readonly StyledProperty<HorizontalAlignment> HorizontalAlignmentProperty =
        StyledProperty<HorizontalAlignment>.Register<Element>("HorizontalAlignment", HorizontalAlignment.Stretch,
            new PropertyOptions { Invalidate = InvalidateKind.Arrange });

    /// <summary>垂直对齐（默认 Stretch）。</summary>
    public static readonly StyledProperty<VerticalAlignment> VerticalAlignmentProperty =
        StyledProperty<VerticalAlignment>.Register<Element>("VerticalAlignment", VerticalAlignment.Stretch,
            new PropertyOptions { Invalidate = InvalidateKind.Arrange });

    public double Margin
    {
        get => GetValue(MarginProperty);
        set => SetValue(MarginProperty, value);
    }

    public double Width
    {
        get => GetValue(WidthProperty);
        set => SetValue(WidthProperty, value);
    }

    public double Height
    {
        get => GetValue(HeightProperty);
        set => SetValue(HeightProperty, value);
    }

    public double MinWidth
    {
        get => GetValue(MinWidthProperty);
        set => SetValue(MinWidthProperty, value);
    }

    public double MaxWidth
    {
        get => GetValue(MaxWidthProperty);
        set => SetValue(MaxWidthProperty, value);
    }

    public double MinHeight
    {
        get => GetValue(MinHeightProperty);
        set => SetValue(MinHeightProperty, value);
    }

    public double MaxHeight
    {
        get => GetValue(MaxHeightProperty);
        set => SetValue(MaxHeightProperty, value);
    }

    public HorizontalAlignment HorizontalAlignment
    {
        get => GetValue(HorizontalAlignmentProperty);
        set => SetValue(HorizontalAlignmentProperty, value);
    }

    public VerticalAlignment VerticalAlignment
    {
        get => GetValue(VerticalAlignmentProperty);
        set => SetValue(VerticalAlignmentProperty, value);
    }

    /// <summary>测量条件、内容需求和排列尺寸共用：显式 Width/Height 优先，Min/Max 兜底
    ///（Min &gt; Max 时 Min 胜——Avalonia 同口径，不抛异常）。</summary>
    private Size ApplySizeConstraints(Size core)
    {
        double w = double.IsNaN(Width) ? core.Width : Width;
        double h = double.IsNaN(Height) ? core.Height : Height;
        w = Math.Clamp(w, MinWidth, Math.Max(MinWidth, MaxWidth));
        h = Math.Clamp(h, MinHeight, Math.Max(MinHeight, MaxHeight));
        return new Size(w, h);
    }

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
        else if (args.RoutedEvent.Strategy == RoutingStrategy.Direct)
        {
            InvokeHandlers(args);
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
    public static readonly StyledProperty<bool> IsTabStopProperty = StyledProperty<bool>.Register<Element>("IsTabStop", true, new() { Invalidate = InvalidateKind.Render });
    public bool IsTabStop { get => GetValue(IsTabStopProperty); set => SetValue(IsTabStopProperty, value); }
    public virtual bool IsFocusable => false;

    /// <summary>
    /// 命中测试：点在根坐标系；返回最深命中元素（子元素从后往前，即视觉最上层优先）。
    /// 调用前提：该元素已被 Arrange（FinalRect 有效）。
    /// </summary>
    public Element? HitTest(Point point)
    {
        if (!IsHitTestVisible || !IsEffectivelyVisible) return null;
        var local = PointFromRoot(point);
        var inside = new Rect(default, FinalRect.Size).Contains(local);
        if (!inside && Parent is null) return null;
        if (this is ScrollViewer bars && bars.TryGetScrollBar(PointFromRoot(point), out _, out _)) return this;
        if (GetContentClip() is { } clip && !clip.Contains(local)) return inside ? this : null;
        foreach (var child in VisualChildren.Reverse())
        {
            if (child.HitTest(point) is { } hit)
                return hit;
        }
        return inside ? this : null;
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

    internal override void RemoveFromParent()
    {
        if (_parentCollection is { } collection) collection.Remove(this);
        else _contentOwner?.DetachContent(this);
    }

    protected override void OnDispose()
    {
        NotifyFocusTreeChanged();
        Styling.PendingClasses.RemoveAll(p => ReferenceEquals(p.Element, this));
        _handlers?.Clear();
        PropertyChanged = null;
        base.OnDispose();
    }

    public override string ToString() => GetType().Name;
}
