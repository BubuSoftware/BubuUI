using System.Collections.Generic;
using System.Linq;

namespace EclipseUI.Compiler;

public abstract class UiNode
{
    public TextSpan Span { get; set; }
}

public enum AttrValueKind
{
    Literal,
    Expression,
}

/// <summary>属性的绑定形态：v4 的 <c>@bind-P</c> 与已弃用的 <c>:bind</c>/<c>:value</c> 修饰符。</summary>
public enum UiBindForm
{
    None,
    /// <summary><c>P:value={expr}</c> ≡ <c>P={expr}</c>（弃用，EUI043）。</summary>
    LegacyValue,
    /// <summary><c>@bind-P={expr}</c>。</summary>
    TwoWay,
    /// <summary><c>P:bind={expr}</c> ≡ <c>@bind-P={expr}</c>（弃用，EUI043）。</summary>
    LegacyTwoWay,
}

/// <summary>
/// <c>Class={expr}</c> 的目标形态（E2，探针推断后回写）：string 走空白拆分，
/// string 集合走 ToArray，其它形态在探针报 EUI031。
/// </summary>
public enum UiClassListKind
{
    /// <summary>未推断（探针未跑/推断失败——C# 经 #line 兜底）。</summary>
    Unknown,
    /// <summary>表达式是 string：空白分隔类名，发射 <c>Split</c>。</summary>
    String,
    /// <summary>表达式是 IEnumerable&lt;string&gt;（含数组/List）：发射 ToArray。</summary>
    Collection,
}

public sealed class UiAttribute
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public UiBindForm BindForm { get; set; }
    public bool IsBind => BindForm == UiBindForm.TwoWay || BindForm == UiBindForm.LegacyTwoWay;
    public bool IsLegacy => BindForm == UiBindForm.LegacyValue || BindForm == UiBindForm.LegacyTwoWay;
    public AttrValueKind Kind { get; set; }
    public string Value { get; set; } = "";
    public TextSpan ValueSpan { get; set; }

    // 由 Analyzer 填充：控件属性/事件分开解析（design/14 §14.5.3）；
    // 组件参数解析到 ParamDef（design/14 §14.6.1），与 PropDef/EventDef 至多一个非空。
    public UiPropDef? PropDef { get; set; }
    public UiEventDef? EventDef { get; set; }
    public UiParameterDef? ParamDef { get; set; }

    /// <summary>G4：点号属性解析到的附加属性（design/14 §14.5.5）；与上三者互斥。</summary>
    public UiAttachedPropDef? AttachedDef { get; set; }
    public string? ConvertedLiteral { get; set; }

    /// <summary>Ref 的目标字段名（仅 <c>Ref={ident}</c> 且 ident 是简单标识符时设置；Analyzer 填充，C5）。</summary>
    public string? RefTarget { get; set; }

    /// <summary>
    /// E2（design/17 §4.8）：Class= 字面量拆分并经样式目录校验的类名序列（Analyzer 填充；
    /// 目录命中为 null（宽松模式）或存在未知类名时报错不回写，生成侧跳过）。
    /// </summary>
    public IReadOnlyList<string>? ClassNames { get; set; }

    /// <summary>E2：Class={expr} 的目标形态（探针回写到 facts，经 spanStart 定位应用；String/Collection 才发射）。</summary>
    public UiClassListKind ClassListKind { get; set; }

    /// <summary>E2：Style="…" 解析出的声明序列（Analyzer 填充；解析失败或表达式形态为 null）。</summary>
    public IReadOnlyList<EustyleSetter>? StyleSetters { get; set; }
}

public sealed class UiElement : UiNode
{
    public string Tag { get; set; } = "";
    public TextSpan TagSpan { get; set; }
    public bool SelfClosing { get; set; }
    public List<UiAttribute> Attributes { get; } = new();
    public List<UiNode> Children { get; set; } = new();

    // 由 Analyzer 填充
    public UiElementDef? ResolvedDef { get; set; }

    /// <summary>
    /// E4（design/17 §4.5）：嵌套塌缩注入的派生类名（生成器按模板静态子树匹配后回写；
    /// 发射时排在 <see cref="UiAttribute.ClassNames"/> 之后——派生类胜）。表达式 Class
    /// 与 C# 动态建树拿不到注入（明示边界）；去重由填充方保证。
    /// </summary>
    public List<string>? InjectedClasses { get; set; }
}

/// <summary>
/// 具名插槽填充（消费侧，design/14 §14.6.3 / spec/01 §4.15）：<c>&lt;Slot Name="Header"&gt;...&lt;/Slot&gt;</c>。
/// 只能作为组件标签的直属子级出现；Analyzer 解析到父组件 def 的 <see cref="UiSlotDef"/>
/// （失败保持 null，生成侧跳过该填充），错误为 EUI022 家族。
/// </summary>
public sealed class UiSlotNode : UiNode
{
    /// <summary>原始属性（含非法项），供 Analyzer 逐个报告；只误报不吞错。</summary>
    public List<UiAttribute> Attributes { get; } = new();
    public List<UiNode> Children { get; set; } = new();

    /// <summary><c>Name</c> 属性（裸形态；<c>@bind-Name</c>/<c>Name:bind</c> 不算）。null = 未写。</summary>
    public UiAttribute? NameAttribute =>
        Attributes.FirstOrDefault(a => a.Name == "Name" && !a.IsBind && !a.IsLegacy);

    // 由 Analyzer 填充
    public UiSlotDef? ResolvedSlot { get; set; }
}

/// <summary>
/// 插槽宿主（组件模板侧）：<c>&lt;ContentSlot Name="Header" /&gt;</c> 标记组件自身模板里拼接
/// 插槽内容的位置。没有运行时宿主元素：生成 Build 时在该处按属性内联拼接（spec/02 §4.12.5）。
/// </summary>
public sealed class UiContentSlotNode : UiNode
{
    /// <summary>原始属性（含非法项），供 Analyzer 逐个报告。</summary>
    public List<UiAttribute> Attributes { get; } = new();

    /// <summary>带子内容（<c>&lt;ContentSlot&gt;...&lt;/ContentSlot&gt;</c>）→ EUI022。</summary>
    public bool HasChildren { get; set; }

    /// <summary><c>Name</c> 属性（裸形态；<c>@bind-Name</c>/<c>Name:bind</c> 不算）。null = 未写。</summary>
    public UiAttribute? NameAttribute =>
        Attributes.FirstOrDefault(a => a.Name == "Name" && !a.IsBind && !a.IsLegacy);

    public string Name => NameAttribute is { Kind: AttrValueKind.Literal } attr ? attr.Value : "";

    // 由 Analyzer 填充：自身 code-behind 类上的插槽（失败 null → 生成侧跳过该拼接）
    public UiSlotDef? ResolvedSlot { get; set; }
}

public enum TextSegmentKind
{
    Static,
    Expression,
}

public sealed class TextSegment
{
    public TextSegmentKind Kind { get; set; }
    public string Text { get; set; } = "";
    public TextSpan Span { get; set; }

    public static TextSegment Static(string text, TextSpan span)
        => new TextSegment { Kind = TextSegmentKind.Static, Text = text, Span = span };

    public static TextSegment Expr(string text, TextSpan span)
        => new TextSegment { Kind = TextSegmentKind.Expression, Text = text, Span = span };
}

public sealed class UiText : UiNode
{
    public List<TextSegment> Segments { get; } = new();
}

public sealed class UiIf : UiNode
{
    public string Condition { get; set; } = "";
    public TextSpan ConditionSpan { get; set; }
    public List<UiNode> Children { get; set; } = new();

    // 多分支（K23 ③ / D87）：else if 链与 else 体。单分支（两者皆空）保持
    // SetCondition/SetContent 生成形态不变（golden 稳定）；多分支生成 AddBranch/SetDefault。
    public List<UiElseIf> ElseIfs { get; } = new();
    public List<UiNode>? ElseChildren { get; set; }

    public bool HasElseChain => ElseIfs.Count > 0 || ElseChildren is not null;
}

/// <summary><c>else if (cond) { … }</c> 续接分支（不带 @，K23 ③）。</summary>
public sealed class UiElseIf
{
    public string Condition { get; set; } = "";
    public TextSpan ConditionSpan { get; set; }
    public TextSpan Span { get; set; }
    public List<UiNode> Children { get; set; } = new();
}

public sealed class UiForeach : UiNode
{
    public string VarName { get; set; } = "";
    public string Collection { get; set; } = "";
    public TextSpan CollectionSpan { get; set; }
    public List<UiNode> Children { get; set; } = new();
}

/// <summary>
/// <c>@code { … }</c>（K21/K23 ④，D86）：文档顶层的类成员区域，原文透传进 partial 类。
/// 位置不限、可多个块、书写顺序不影响成员可见性（生成器先全量收集再建探针 Compilation）。
/// </summary>
public sealed class UiCodeBlock : UiNode
{
    /// <summary>块体原文（不含定界大括号）。</summary>
    public string Code { get; set; } = "";

    /// <summary>块体原文在 .ui 中的 span（#line 映射与 EUI045 位置回映用）。</summary>
    public TextSpan CodeSpan { get; set; }
}

/// <summary>
/// <c>@style { … }</c>（E1，design/17 §4.8）：文档顶层的组件样式块，块体为 Eustyle 样式表
/// 源码（与 .eustyle 文件同语法，<see cref="Eustyle"/> 解析）。类名编译期加组件前缀
/// （<c>{Component}__{class}</c>）注册进全局 <c>StyleRegistry</c>——作用域由命名隔离实现，
/// 运行时零新机制；本文件 <c>Class="…"</c> 的解析顺序（先块内后全局目录）随 E2 落地。
/// </summary>
public sealed class UiStyleBlock : UiNode
{
    /// <summary>块体原文（不含定界大括号）。</summary>
    public string Code { get; set; } = "";

    /// <summary>块体原文在 .ui 中的 span（诊断 span 绝对定位用）。</summary>
    public TextSpan CodeSpan { get; set; }
}

/// <summary>
/// <c>@using</c>（K22，D85）：文档顶层指令，多条、位置不限；生成文件头按模板顺序去重输出
/// 普通 <c>using Ns;</c>，标签只在导入命名空间内解析。
/// K31：另有别名形态 <c>@using Btn = Ns.Type</c>——生成 <c>using Btn = global::Ns.Type;</c>，
/// 并让 <c>&lt;Btn/&gt;</c> 在本模板内解析为该类型（别名优先于目录 tag）。
/// </summary>
public sealed class UiUsingDirective : UiNode
{
    /// <summary>命名空间形态的导入名；别名形态为空（别名不肯进 imports，见 TemplateAnalyzer.CollectImports）。</summary>
    public string Namespace { get; set; } = "";

    /// <summary>K31：别名标识符（如 <c>Btn</c>）；命名空间形态为 null。</summary>
    public string? Alias { get; set; }

    /// <summary>K31：别名的目标类型名原文（如 <c>ThirdParty.Controls.Button</c>，可带 <c>global::</c>）。</summary>
    public string AliasTarget { get; set; } = "";

    /// <summary>命名空间名或别名目标的 span（含别名目标类型时指向类型名）。</summary>
    public TextSpan NamespaceSpan { get; set; }
}

/// <summary>
/// <c>@switch (value) { case CONST: … break; default: … break; }</c>（K23 ③/K29，D87）。
/// 无贯穿语义：每个 section 是独立分支，但非空 section 仍要求 <c>break;</c>（K29 ③）。
/// </summary>
public sealed class UiSwitch : UiNode
{
    public string Value { get; set; } = "";
    public TextSpan ValueSpan { get; set; }
    public List<UiSwitchSection> Sections { get; } = new();
}

/// <summary>@switch 的单个 case/default 分支（K29：分支体为完整内容模板）。</summary>
public sealed class UiSwitchSection
{
    public bool IsDefault { get; set; }

    /// <summary>case 标签原文（编译期常量表达式；default 分支为空）。</summary>
    public string Label { get; set; } = "";

    /// <summary>标签原文 span（AddCase 调用行的 #line 映射目标）。</summary>
    public TextSpan LabelSpan { get; set; }

    /// <summary>section 头（case/default 行）的 span（诊断位置）。</summary>
    public TextSpan HeaderSpan { get; set; }

    public List<UiNode> Children { get; } = new();
    public bool HasBreak { get; set; }
}

public sealed class UiDocument
{
    public UiDocument(string fileName, string text, List<UiNode> nodes)
    {
        FileName = fileName;
        Text = text;
        Nodes = nodes;
        Lines = new LineMap(text);
    }

    public string FileName { get; }
    public string Text { get; }
    public List<UiNode> Nodes { get; }
    public LineMap Lines { get; }
    public string ClassName => System.IO.Path.GetFileNameWithoutExtension(FileName);
}
