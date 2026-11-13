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

/// <summary>
/// 自定义样式属性（design/19，2026-11-12）：让应用/第三方库从**自己的程序集**声明可写进
/// 样式表（.eustyle / @style / Style=）的属性——属性目录的进料口从「StyleBuilder 的方法」
/// 解耦为「带本标注的静态 StyledProperty&lt;T&gt; 字段」（编译器全局符号扫描，显式 opt-in；
/// 不扫全部 StyledProperty 字段，避免 Bounds 之类内部属性污染目录）。
/// 编译期只读符号、生成 <c>s.Set(field, value)</c> 调用，运行时零新机制。
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class UiStylePropertyAttribute : Attribute
{
    /// <summary>
    /// 样式属性名（样式表书写名，大小写敏感的 CLR 原名口径）。缺省 = 字段名去
    /// <c>Property</c> 后缀；去后为空必须显式命名。
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 适用控件类型；缺省 = 不校验适用性（与内建属性现状口径一致——Owner 适用性诊断属 P3）。
    /// </summary>
    public Type? Owner { get; set; }
}

/// <summary>
/// 声明式字面量解析器（design/14 §14.7.3，2026-11-12）：让第三方类型也能在模板里写
/// <c>P="literal"</c>。编译期只读符号校验一个 <c>public static T Parse(string)</c>
/// 方法（T 为被标注类型），字面量格式用 <see cref="Pattern"/> 由 BCL Regex 校验
/// （框架代码，不执行用户程序集，R1 保留），生成 <c>global::Ns.T.Parse("literal")</c>
/// 调用——元素构造时执行一次，不逐帧解析（R2 放宽口径见 spec/02）。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct,
    AllowMultiple = false, Inherited = false)]
public sealed class UiLiteralParserAttribute : Attribute
{
    /// <summary>静态方法名；必须是 <c>public static T {ParseMethod}(string)</c>，T 为被标注的类型。</summary>
    public string ParseMethod { get; set; } = "Parse";

    /// <summary>
    /// 可选。编译期由编译器用 BCL Regex（CultureInvariant，100ms 超时）校验字面量，
    /// 使 EUI003 仍即时报出；不填则格式错误只能由运行时 Parse 抛异常发现。
    /// </summary>
    public string? Pattern { get; set; }

    /// <summary>EUI003 消息里的期望格式说明；缺省时回显 Pattern。</summary>
    public string? Hint { get; set; }

    /// <summary>可选值候选，供 LSP 在引号内补全（与 EnumMembers 同一档位）。</summary>
    public string[]? Candidates { get; set; }
}
