using EclipseUI;
using EclipseUI.Elements;

namespace EclipseUI.Rendering;

/// <summary>
/// Class 样式（S-B）：类名直配 setter 序列——无运行时选择器引擎。
/// 样式定义在宿主启动期 <c>StyleRegistry.Define("card", s => s.Background(...).Padding(8))</c>
/// 注册；元素 <c>Classes</c> 变化时经 <see cref="Styling.Provider"/> 回调逐类应用，
/// 写入有效值的 class 层（低于 local、高于继承/默认）。
/// 未知类名静默忽略（编译期校验随模板 Class 语法片落地）。
/// </summary>
public sealed class StyleRegistry : IStyleProvider
{
    public static StyleRegistry Instance { get; } = new();

    private readonly Dictionary<string, ClassStyle> _styles = new(StringComparer.Ordinal);

    /// <summary>
    /// 注册一个样式类（可续接伪类变体，D102）：重复注册覆盖（宿主启动期一次性配置）。
    /// <code>
    /// registry.Define("primary", s => s.Background(accent))
    ///         .Hover(s => s.Background(accentHover))
    ///         .Pressed(s => s.Background(accentPressed));
    /// </code>
    /// </summary>
    public ClassStyleVariants Define(string className, Action<StyleBuilder> build)
    {
        if (string.IsNullOrWhiteSpace(className))
            throw new ArgumentException("Style class name must not be empty.", nameof(className));
        if (build is null) throw new ArgumentNullException(nameof(build));
        var style = new ClassStyle();
        var builder = new StyleBuilder();
        build(builder);
        style.Base.AddRange(builder.Build());
        _styles[className] = style;
        return new ClassStyleVariants(style);
    }

    /// <summary>查询类是否已注册（测试与诊断用）。</summary>
    public bool IsDefined(string className) => _styles.ContainsKey(className);

    public void ApplyClass(Element element, string className)
    {
        if (!_styles.TryGetValue(className, out var style))
            return;
        var states = element.PseudoStates;
        foreach (var setter in style.Base)
            setter.Apply(element);
        // 变体按声明顺序应用：同时匹配多个伪类时，后声明覆盖先声明
        // （与 CSS「同优先级按源顺序」一致，如 hover+pressed 同开时 pressed 胜）。
        foreach (var (state, setters) in style.Variants)
        {
            if ((states & state) != state)
                continue;
            foreach (var setter in setters)
                setter.Apply(element);
        }
    }

    public void ClearStyles(Element element) => element.ClearStyleValues();
}

/// <summary>一个样式类的完整定义：基础 setter 序列 + 各伪类状态变体（D102）。</summary>
public sealed class ClassStyle
{
    internal List<IStyleSetter> Base { get; } = [];

    internal List<(PseudoStates State, List<IStyleSetter> Setters)> Variants { get; } = [];
}

/// <summary>
/// 伪类变体声明器（D102，<see cref="StyleRegistry.Define"/> 的返回值）：链式追加
/// <see cref="PseudoStates"/> 变体——应用时提供者读元素当前状态选择匹配变体，
/// 无状态元素只应用基础序列。变体语义对应 CSS 伪类：
/// <see cref="ClassStyleVariants.Hover"/>=<c>:hover</c>、
/// <see cref="ClassStyleVariants.Pressed"/>=<c>:active</c>、
/// <see cref="ClassStyleVariants.Focus"/>=<c>:focus</c>。
/// </summary>
public sealed class ClassStyleVariants
{
    private readonly ClassStyle _style;

    internal ClassStyleVariants(ClassStyle style) => _style = style;

    /// <summary>指针悬停变体（CSS :hover）。</summary>
    public ClassStyleVariants Hover(Action<StyleBuilder> build) => AddVariant(PseudoStates.Hover, build);

    /// <summary>指针按下变体（CSS :active / WPF IsPressed）。</summary>
    public ClassStyleVariants Pressed(Action<StyleBuilder> build) => AddVariant(PseudoStates.Pressed, build);

    /// <summary>键盘焦点变体（CSS :focus）。</summary>
    public ClassStyleVariants Focus(Action<StyleBuilder> build) => AddVariant(PseudoStates.Focused, build);

    /// <summary>有效禁用变体（CSS :disabled，E3）：祖先禁用同样命中（有效值沿树继承）。</summary>
    public ClassStyleVariants Disabled(Action<StyleBuilder> build) => AddVariant(PseudoStates.Disabled, build);

    /// <summary>
    /// 注册制伪类变体（E5，design/17 §3.2）：<paramref name="state"/> 传
    /// <c>PseudoStatesRegistry.Register("name")</c> 返回的掩码（幂等，重复调用安全）——
    /// 元素经 <see cref="Element.SetRegisteredState"/> 翻转该位后命中本变体。
    /// 与平台位变体共用同一应用序（声明序后者胜，design/17 §3.3）。
    /// </summary>
    public ClassStyleVariants State(PseudoStates state, Action<StyleBuilder> build) => AddVariant(state, build);

    private ClassStyleVariants AddVariant(PseudoStates state, Action<StyleBuilder> build)
    {
        if (build is null) throw new ArgumentNullException(nameof(build));
        var builder = new StyleBuilder();
        build(builder);
        _style.Variants.Add((state, builder.Build()));
        return this;
    }
}

internal interface IStyleSetter
{
    void Apply(Element element);
}

/// <summary>样式定义构建器：类型安全 setter，记录为命令序列（Define 时求值一次）。</summary>
[UiStyleOwner(typeof(Visual))]
[UiStyleOwner(typeof(EclipseUI.Elements.Panel))]
[UiStyleOwner(typeof(EclipseUI.Elements.TextBlock))]
public sealed class StyleBuilder
{
    private readonly List<IStyleSetter> _setters = [];

    internal List<IStyleSetter> Build() => [.. _setters];

    public StyleBuilder Set<T>(StyledProperty<T> property, T value)
    {
        _setters.Add(new StyleSetter<T>(property, value));
        return this;
    }

    public StyleBuilder Background(Color color) => Set(Visual.BackgroundProperty, (Color?)color);

    public StyleBuilder Foreground(Color color) => Set(Visual.ForegroundProperty, (Color?)color);

    // ---- S-C：主题资源 setter——亮暗切换自动跟随（优先级高于静态覆盖）----

    public StyleBuilder Background(ThemeColor resource) => Set(Visual.BackgroundResourceProperty, resource);

    public StyleBuilder Foreground(ThemeColor resource) => Set(Visual.ForegroundResourceProperty, resource);

    public StyleBuilder CornerRadius(double radius) => Set(Visual.CornerRadiusProperty, (double?)radius);

    public StyleBuilder FontSize(double size) => Set(TextBlock.FontSizeProperty, size);

    public StyleBuilder Padding(double uniform) => Set(Panel.PaddingProperty, uniform);

    /// <summary>焦点环宽度覆盖（E3）：绘制时回落主题令牌 <see cref="ElementTheme.FocusRingWidth"/>。</summary>
    public StyleBuilder FocusRingWidth(double width) => Set(Visual.FocusRingWidthProperty, (double?)width);

    private sealed class StyleSetter<T>(StyledProperty<T> property, T value) : IStyleSetter
    {
        public void Apply(Element element) => element.SetStyleValue(property, value);
    }
}
