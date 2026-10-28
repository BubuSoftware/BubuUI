using System.Collections.Generic;
using System.Linq;

namespace EclipseUI.Compiler;

/// <summary>语义检查：标签/属性解析到目录、字面量编译期转换、内容模型与 Key/块位置约束。</summary>
public static class TemplateAnalyzer
{
    private enum Ctx { Root, Container, TextRuns, Label }

    public static void Analyze(UiDocument doc, UiCatalog catalog, List<UiDiagnostic> diags)
    {
        int roots = doc.Nodes.Count(n => n is UiElement);
        if (roots != 1)
            diags.Add(UiDiagnostics.Error(UiDiagnostics.RootElementExpected,
                $"template must contain exactly one root element, found {roots}", TextSpan.Zero));
        foreach (var node in doc.Nodes)
        {
            if (node is UiIf or UiForeach)
                diags.Add(UiDiagnostics.Error(UiDiagnostics.TextNotAllowed,
                    "control flow blocks are not allowed at the template root", node.Span));
        }

        foreach (var node in doc.Nodes)
            Visit(node, Ctx.Root, catalog, diags, allowKey: false);
    }

    private static void Visit(UiNode node, Ctx ctx, UiCatalog catalog, List<UiDiagnostic> diags, bool allowKey)
    {
        switch (node)
        {
            case UiElement element:
                VisitElement(element, ctx, catalog, diags, allowKey);
                break;
            case UiText text:
                VisitText(text, ctx, diags);
                break;
            case UiIf ifNode:
                VisitBlock(ifNode.ConditionSpan, ifNode.Children, ctx, catalog, diags);
                break;
            case UiForeach foreachNode:
                VisitBlock(foreachNode.CollectionSpan, foreachNode.Children, ctx, catalog, diags);
                break;
        }
    }

    private static void VisitBlock(TextSpan exprSpan, List<UiNode> children, Ctx ctx, UiCatalog catalog, List<UiDiagnostic> diags)
    {
        if (ctx == Ctx.TextRuns || ctx == Ctx.Label)
            diags.Add(UiDiagnostics.Error(UiDiagnostics.TextNotAllowed,
                "control flow blocks are only allowed inside element containers", exprSpan));
        if (exprSpan.Length == 0)
            diags.Add(UiDiagnostics.Error(UiDiagnostics.Syntax, "expression expected", exprSpan));

        bool firstElement = true;
        foreach (var child in children)
        {
            bool isKeyAllowed = child is UiElement && firstElement;
            if (child is UiElement) firstElement = false;
            Visit(child, Ctx.Container, catalog, diags, isKeyAllowed);
        }
    }

    private static void VisitText(UiText text, Ctx ctx, List<UiDiagnostic> diags)
    {
        if (ctx == Ctx.Container || ctx == Ctx.Root)
            diags.Add(UiDiagnostics.Error(UiDiagnostics.TextNotAllowed,
                "text content is not allowed here; wrap it in an element", text.Span));
    }

    private static void VisitElement(UiElement element, Ctx ctx, UiCatalog catalog, List<UiDiagnostic> diags, bool allowKey)
    {
        if (!catalog.TryGetElement(element.Tag, out var def))
        {
            var suggestion = Suggest.Nearest(element.Tag, catalog.ElementTags);
            var message = $"unknown tag '{element.Tag}'" + (suggestion is null ? "" : $"; did you mean '{suggestion}'?");
            diags.Add(UiDiagnostics.Error(UiDiagnostics.UnknownTag, message, element.TagSpan));
            element.ResolvedDef = null;
            foreach (var child in element.Children)
                Visit(child, Ctx.Container, catalog, diags, allowKey: false);
            return;
        }

        element.ResolvedDef = def;

        foreach (var attr in element.Attributes)
        {
            if (attr.Name == "Key")
            {
                if (!allowKey)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.KeyMisuse,
                        "Key= is only valid on the root element of an @foreach template", attr.NameSpan));
                else if (attr.Kind != AttrValueKind.Expression)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.KeyMisuse,
                        "Key= requires an {expression}", attr.NameSpan));
                continue;
            }

            if (!def.Props.TryGetValue(attr.Name, out var propDef))
            {
                var suggestion = Suggest.Nearest(attr.Name, def.Props.Keys);
                var message = $"unknown property '{attr.Name}' on '{element.Tag}'" + (suggestion is null ? "" : $"; did you mean '{suggestion}'?");
                diags.Add(UiDiagnostics.Error(UiDiagnostics.UnknownProperty, message, attr.NameSpan));
                continue;
            }

            attr.PropDef = propDef;

            if (propDef.IsEvent)
            {
                if (attr.Kind != AttrValueKind.Expression)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.EventNeedsExpression,
                        $"event '{attr.Name}' requires an {{expression}} handler", attr.ValueSpan));
                if (attr.IsBind)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.EventNeedsExpression,
                        $"event '{attr.Name}' cannot be used with :bind", attr.NameSpan));
            }
            else if (!attr.IsBind && attr.Kind == AttrValueKind.Literal)
            {
                if (!UiLiterals.TryConvert(propDef.ClrType, attr.Value, out var converted, out var error))
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.LiteralFormat,
                        $"property '{attr.Name}' of '{element.Tag}' ({propDef.ClrType}) {error}", attr.ValueSpan));
                else
                    attr.ConvertedLiteral = converted;
            }
        }

        var contentCtx = def.Content switch
        {
            UiContentModel.TextRuns => Ctx.TextRuns,
            UiContentModel.LabelText => Ctx.Label,
            _ => Ctx.Container,
        };

        foreach (var child in element.Children)
        {
            if ((contentCtx == Ctx.TextRuns || contentCtx == Ctx.Label) && child is UiElement)
                diags.Add(UiDiagnostics.Error(UiDiagnostics.TextNotAllowed,
                    $"element children are not allowed inside '{element.Tag}' ({def.Content})", child.Span));
            Visit(child, contentCtx, catalog, diags, allowKey: false);
        }
    }
}

/// <summary>编译前端入口：词法 → 语法 → 语义检查。</summary>
public static class FrontEnd
{
    public static UiDocument Parse(string text, string fileName, List<UiDiagnostic> diags)
    {
        var parser = new Parser(text, diags);
        return parser.ParseDocument(fileName);
    }

    public static void Analyze(UiDocument doc, UiCatalog catalog, List<UiDiagnostic> diags)
        => TemplateAnalyzer.Analyze(doc, catalog, diags);
}
