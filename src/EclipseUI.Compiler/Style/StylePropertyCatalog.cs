using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI.Compiler;

/// <summary>样式属性值形态（由目录派生，不再手写属性表）。</summary>
public enum StyleValueKind
{
    /// <summary>颜色：hex 字面量或 <c>theme(dark, light)</c> 双色。</summary>
    Color,

    /// <summary>逻辑单位裸数字。</summary>
    Number,

    /// <summary>
    /// 枚举（design/19 §5.2）：CSS keyword 风格的裸标识符（<c>Tone: Success</c>），
    /// 编译期按 <see cref="StylePropertyDef.Candidates"/> 校验成员名，发射
    /// <c>global::Ns.T.Member</c>。
    /// </summary>
    Enum,

    /// <summary>字符串（design/19 §5.2）：带引号字面量（<c>Label: "hello"</c>），
    /// keyword 形态也按字符串直通；无成员表约束。</summary>
    Text,
}

/// <summary>
/// 一条可样式属性（E1 单一来源，design/17 §4.3）：编译期由 <c>StyleBuilder</c> 的公开
/// setter 与 <c>StyledProperty&lt;T&gt;</c> 静态字段符号派生，Parser 目录、诊断提示、
/// <c>EmitDefines</c> 与 <c>EmitLocalSetters</c> 全部读这一份数据。
/// </summary>
/// <param name="Name">规范名：CLR 属性名 / <c>StyleBuilder</c> setter 名（如 <c>CornerRadius</c>）。</param>
/// <param name="Kind">期望值形态，由 setter 的参数类型派生。</param>
/// <param name="PropertyField">静态 <c>StyledProperty</c> 字段的 global:: 全限定名（local 直写用）。</param>
/// <param name="ResourceField"><c>theme()</c> 资源属性字段；null = 该属性不接受主题资源。</param>
/// <param name="DeclaringTypeName">字段声明类型的全限定名（诊断与后续适用性校验用）。</param>
/// <param name="IsBuiltIn">进料口来源（design/19 §6.1）：true = StyleBuilder 内建 setter 通道，
/// 发射命名方法 <c>s.Name(...)</c>；false = <c>[UiStyleProperty]</c> 标注字段通道，发射
/// 泛型逃生口 <c>s.Set(field, value)</c>（StyleBuilder 上没有对应方法）。</param>
/// <param name="EnumTypeName">Kind = Enum 时的枚举类型全限定名（发射 <c>T.Member</c> 用）；其余 null。</param>
/// <param name="Candidates">Kind = Enum 时的成员名表（升序，EUI052 校验与 LSP 值候选）；其余 null/空。
/// 列表按引用比较会破坏值相等，所以本记录手写结构相等（design/14 §14.10.3 教训）。</param>
public sealed record StylePropertyDef(
    string Name,
    StyleValueKind Kind,
    string PropertyField,
    string? ResourceField,
    string DeclaringTypeName,
    bool IsBuiltIn = true,
    string? EnumTypeName = null,
    IReadOnlyList<string>? Candidates = null,
    bool Animatable = false,
    bool IsUiColor = false)
{
    /// <summary>是否接受 <c>theme(dark, light)</c>（由是否存在 ThemeColor 重载 + 资源字段决定）。</summary>
    public bool AllowsTheme => ResourceField is not null;

    /// <summary>按值形态选目标属性字段；主题值找不到资源字段时返回 null（解析期已据此拒绝）。</summary>
    public string? FieldFor(bool theme) => theme ? ResourceField : PropertyField;

    public bool Equals(StylePropertyDef? other) =>
        other is not null
        && Name == other.Name
        && Kind == other.Kind
        && PropertyField == other.PropertyField
        && ResourceField == other.ResourceField
        && DeclaringTypeName == other.DeclaringTypeName
        && IsBuiltIn == other.IsBuiltIn
        && EnumTypeName == other.EnumTypeName
        && Animatable == other.Animatable
        && IsUiColor == other.IsUiColor
        && CatalogValueEq.Sequence(Candidates, other.Candidates);

    public override int GetHashCode()
    {
        var hash = CatalogValueEq.Add(CatalogValueEq.Start, Name);
        hash = CatalogValueEq.Add(hash, (int)Kind);
        hash = CatalogValueEq.Add(hash, PropertyField);
        hash = CatalogValueEq.Add(hash, ResourceField);
        hash = CatalogValueEq.Add(hash, DeclaringTypeName);
        hash = CatalogValueEq.Add(hash, IsBuiltIn);
        hash = CatalogValueEq.Add(hash, EnumTypeName);
        hash = CatalogValueEq.Add(hash, Animatable ? 1 : 0);
        hash = CatalogValueEq.Add(hash, IsUiColor ? 1 : 0);
        return CatalogValueEq.AddSequence(hash, Candidates);
    }
}

/// <summary>
/// 编译期派生的可样式属性目录。纯数据（不含 Roslyn 符号），可在增量生成器管道里做值相等缓存。
/// 名字查找是<b>大小写敏感的精确匹配</b>：书写名必须等于控件/属性对象的原始 CLR 名
/// （<c>CornerRadius</c>），没有别名、没有大小写或分隔符归一化——
/// <c>corner-radius</c> / <c>cornerradius</c> 一律未命中（调用方报 EUI051）。
/// </summary>
public sealed record StylePropertyCatalog(
    IReadOnlyList<StylePropertyDef> Properties,
    IReadOnlyList<UiMetadataProblem> Problems)
{
    /// <summary>无渲染程序集语境的空目录（照样会为每条声明报 EUI051）。</summary>
    public static StylePropertyCatalog Empty { get; } =
        new(Array.Empty<StylePropertyDef>(), Array.Empty<UiMetadataProblem>());

    /// <summary>按书写名精确找目录项（Ordinal）；未命中返回 null（调用方报 EUI051）。</summary>
    public StylePropertyDef? Find(string name)
    {
        foreach (var property in Properties)
            if (string.Equals(property.Name, name, StringComparison.Ordinal)) return property;
        return null;
    }

    /// <summary>诊断消息里列出的规范名（排序后），不再手写字符串数组。</summary>
    public IReadOnlyList<string> SortedNames() =>
        Properties.Select(static p => p.Name).OrderBy(static n => n, StringComparer.Ordinal).ToArray();

    public bool Equals(StylePropertyCatalog? other) =>
        other is not null
        && CatalogValueEq.Sequence(Properties, other.Properties)
        && CatalogValueEq.Sequence(Problems, other.Problems);

    public override int GetHashCode()
    {
        var hash = CatalogValueEq.AddSequence(CatalogValueEq.Start, Properties);
        return CatalogValueEq.AddSequence(hash, Problems);
    }
}
