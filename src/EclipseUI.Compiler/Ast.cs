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
    public string? ConvertedLiteral { get; set; }

    /// <summary>Ref 的目标字段名（仅 <c>Ref={ident}</c> 且 ident 是简单标识符时设置；Analyzer 填充，C5）。</summary>
    public string? RefTarget { get; set; }
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
}

public sealed class UiForeach : UiNode
{
    public string VarName { get; set; } = "";
    public string Collection { get; set; } = "";
    public TextSpan CollectionSpan { get; set; }
    public List<UiNode> Children { get; set; } = new();
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
