using System;

namespace EclipseUI;

/// <summary>
/// 内容模型（控件作者声明，编译器目录按同名枚举消费）。
/// 数值必须与 <c>EclipseUI.Compiler.UiContentModel</c> 一一对应（design/14 §14.4.4/§14.11.1 有断言）。
/// </summary>
public enum UiContentKind
{
    /// <summary>不接受子内容。</summary>
    None = 0,

    /// <summary>只接受元素、<c>@if</c>、<c>@foreach</c>；纯空白文本忽略。</summary>
    Children = 1,

    /// <summary>文本子内容进入单一 <see cref="string"/> 目标属性。</summary>
    Text = 2,

    /// <summary>文本子内容编译为多个 run，目标是 <see cref="ITextContentHost"/>。</summary>
    TextRuns = 3,
}

/// <summary>
/// 声明内容模型。默认无特性 = <see cref="UiContentKind.None"/>，不猜测。
/// <c>Target</c> 省略时按模型默认（<see cref="UiContentKind.Children"/> → <c>"Children"</c>，其余必须显式）。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public sealed class UiContentAttribute : Attribute
{
    public UiContentAttribute(UiContentKind kind = UiContentKind.Children) => Kind = kind;

    public UiContentKind Kind { get; }

    public string? Target { get; set; }
}

/// <summary>退出模板表面：类型不作为标签，属性/字段不作为模板属性或事件。</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class UiIgnoreAttribute : Attribute
{
}

/// <summary>组件参数（design/14 §14.6.1）。必填用 <c>Required = true</c>，不是 C# <c>required</c>。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UiParameterAttribute : Attribute
{
    public bool Required { get; set; }
}

/// <summary>具名插槽（design/14 §14.6.3，G4 实现）。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UiSlotAttribute : Attribute
{
    public UiSlotAttribute(string name) => Name = name;

    public string Name { get; }
}
