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

    private readonly Dictionary<string, List<IStyleSetter>> _styles = new(StringComparer.Ordinal);

    /// <summary>注册一个样式类；重复注册覆盖（宿主启动期一次性配置）。</summary>
    public void Define(string className, Action<StyleBuilder> build)
    {
        if (string.IsNullOrWhiteSpace(className))
            throw new ArgumentException("Style class name must not be empty.", nameof(className));
        var builder = new StyleBuilder();
        build(builder);
        _styles[className] = builder.Build();
    }

    /// <summary>查询类是否已注册（测试与诊断用）。</summary>
    public bool IsDefined(string className) => _styles.ContainsKey(className);

    public void ApplyClass(Element element, string className)
    {
        if (!_styles.TryGetValue(className, out var setters))
            return;
        foreach (var setter in setters)
            setter.Apply(element);
    }

    public void ClearStyles(Element element) => element.ClearStyleValues();
}

internal interface IStyleSetter
{
    void Apply(Element element);
}

/// <summary>样式定义构建器：类型安全 setter，记录为命令序列（Define 时求值一次）。</summary>
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

    private sealed class StyleSetter<T>(StyledProperty<T> property, T value) : IStyleSetter
    {
        public void Apply(Element element) => element.SetStyleValue(property, value);
    }
}
