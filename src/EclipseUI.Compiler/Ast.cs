using System.Collections.Generic;

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

public sealed class UiAttribute
{
    public string Name { get; set; } = "";
    public TextSpan NameSpan { get; set; }
    public bool IsBind { get; set; }
    public AttrValueKind Kind { get; set; }
    public string Value { get; set; } = "";
    public TextSpan ValueSpan { get; set; }

    // 由 Analyzer 填充
    public UiPropDef? PropDef { get; set; }
    public string? ConvertedLiteral { get; set; }
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
