using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI.Compiler;

/// <summary>语义检查：标签/属性解析到目录、字面量编译期转换、内容模型与 Key/块位置约束。</summary>
public static class TemplateAnalyzer
{
    private enum Ctx { Root, Container, TextRuns, Label, None }

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
                VisitBlock(ifNode.ConditionSpan, ifNode.Children, ctx, catalog, diags, allowKeyRoot: false);
                break;
            case UiForeach foreachNode:
                VisitBlock(foreachNode.CollectionSpan, foreachNode.Children, ctx, catalog, diags, allowKeyRoot: true);
                break;
        }
    }

    private static void VisitBlock(TextSpan exprSpan, List<UiNode> children, Ctx ctx, UiCatalog catalog,
        List<UiDiagnostic> diags, bool allowKeyRoot)
    {
        if (ctx == Ctx.TextRuns || ctx == Ctx.Label)
            diags.Add(UiDiagnostics.Error(UiDiagnostics.TextNotAllowed,
                "control flow blocks are only allowed inside element containers", exprSpan));
        if (exprSpan.Length == 0)
            diags.Add(UiDiagnostics.Error(UiDiagnostics.Syntax, "expression expected", exprSpan));

        bool firstRoot = true;
        foreach (var child in children)
        {
            bool isKeyAllowed = allowKeyRoot && firstRoot && child is UiElement;
            firstRoot = false;
            Visit(child, Ctx.Container, catalog, diags, isKeyAllowed);
        }
    }

    private static void VisitText(UiText text, Ctx ctx, List<UiDiagnostic> diags)
    {
        if (ctx == Ctx.Container || ctx == Ctx.Root)
            diags.Add(UiDiagnostics.Error(UiDiagnostics.TextNotAllowed,
                "text content is not allowed here; wrap it in an element", text.Span));
    }

    private static bool IsReservedName(string name) =>
        name is "Key" or "Ref" or "Class" or "Style";

    private static string LegacySyntaxMessage(UiAttribute attr) =>
        attr.BindForm == UiBindForm.LegacyTwoWay
            ? $"'{attr.Name}:bind' is deprecated; write @bind-{attr.Name}={{expression}}"
            : $"'{attr.Name}:value' is deprecated; write {attr.Name}=... (the same target and the same generated code)";

    private static string UnknownTagMessage(string tag, UiTagResolution resolution, UiCatalog catalog) =>
        resolution.Kind switch
        {
            UiTagResolutionKind.Ambiguous =>
                $"tag '{tag}' is ambiguous between {string.Join(" or ", resolution.Candidates!)}; " +
                "disambiguate with [UiTag] on one of them",
            UiTagResolutionKind.Unavailable =>
                $"tag '{tag}' is not usable: {resolution.Hidden!.Reason}",
            _ => $"unknown tag '{tag}'" +
                (Suggest.Nearest(tag, catalog.ElementTags) is { } hint ? $"; did you mean '{hint}'?" : ""),
        };

    private static void VisitElement(UiElement element, Ctx ctx, UiCatalog catalog, List<UiDiagnostic> diags, bool allowKey)
    {
        var resolution = catalog.Resolve(element.Tag);
        if (resolution.Kind != UiTagResolutionKind.Found)
        {
            diags.Add(UiDiagnostics.Error(UiDiagnostics.UnknownTag,
                UnknownTagMessage(element.Tag, resolution, catalog), element.TagSpan));
            element.ResolvedDef = null;
            foreach (var child in element.Children)
                Visit(child, Ctx.Container, catalog, diags, allowKey: false);
            return;
        }

        var def = resolution.Element!;
        element.ResolvedDef = def;
        var targetWrites = new Dictionary<string, bool>();

        foreach (var attr in element.Attributes)
        {
            if (attr.IsLegacy)
                diags.Add(UiDiagnostics.Warning(UiDiagnostics.LegacySyntax, LegacySyntaxMessage(attr), attr.NameSpan));

            if (attr.IsBind && IsReservedName(attr.Name))
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.BindTargetInvalid,
                    $"'{attr.Name}' is a reserved attribute; @bind-{attr.Name} is not allowed on '{element.Tag}'", attr.NameSpan));
                continue;
            }

            if (attr.Name == "Key")
            {
                if (!allowKey)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.KeyMisuse,
                        "Key= is only valid on the first element root of an @foreach template", attr.NameSpan));
                else if (attr.Kind != AttrValueKind.Expression || string.IsNullOrWhiteSpace(attr.Value))
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.KeyMisuse,
                        "Key= requires a non-empty {expression}", attr.NameSpan));
                continue;
            }

            // 控件按 §14.5.3 的顺序解析：保留属性 → 事件 → 属性。组件参数属 C4。
            UiEventDef? eventDef = def.Events.TryGetValue(attr.Name, out var evt) ? evt : null;
            UiPropDef? propDef = null;
            if (eventDef is null) def.Properties.TryGetValue(attr.Name, out propDef);

            if (eventDef is null && propDef is null)
            {
                var hint = attr.Name.StartsWith("On", StringComparison.Ordinal)
                    ? Suggest.Nearest(attr.Name, def.Events.Keys) ?? Suggest.Nearest(attr.Name, def.Properties.Keys)
                    : Suggest.Nearest(attr.Name, def.Properties.Keys) ?? Suggest.Nearest(attr.Name, def.Events.Keys);
                var message = $"unknown property '{attr.Name}' on '{element.Tag}'" + (hint is null ? "" : $"; did you mean '{hint}'?");
                diags.Add(UiDiagnostics.Error(UiDiagnostics.UnknownProperty, message, attr.NameSpan));
                continue;
            }

            attr.PropDef = propDef;
            attr.EventDef = eventDef;

            if (attr.IsBind && (eventDef is not null || propDef is not { CanWrite: true }))
            {
                var reason = eventDef is not null ? "is an event" : "is not a bindable property";
                diags.Add(UiDiagnostics.Error(UiDiagnostics.BindTargetInvalid,
                    $"'{attr.Name}' of '{element.Tag}' {reason}; @bind-{attr.Name} needs a public writable property registered as '{attr.Name}Property'",
                    attr.NameSpan));
            }
            else if (attr.IsBind && (attr.Kind != AttrValueKind.Expression || string.IsNullOrWhiteSpace(attr.Value)))
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.BindSourceInvalid,
                    $"@bind-{attr.Name} needs a {{expression}} source; a literal has nowhere to write back",
                    attr.ValueSpan));
            }

            if (eventDef is null)
            {
                if (targetWrites.TryGetValue(attr.Name, out var firstIsBind))
                {
                    if (firstIsBind || attr.IsBind)
                        diags.Add(UiDiagnostics.Error(UiDiagnostics.SameTargetDuplicate,
                            $"target '{attr.Name}' of '{element.Tag}' is assigned more than once; keep a single value or binding",
                            attr.NameSpan));
                }
                else
                {
                    targetWrites[attr.Name] = attr.IsBind;
                }
            }

            if (eventDef is not null)
            {
                if (!attr.IsBind && attr.Kind != AttrValueKind.Expression)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.EventNeedsExpression,
                        $"event '{attr.Name}' requires an {{expression}} handler", attr.ValueSpan));
            }
            else if (!attr.IsBind && attr.Kind == AttrValueKind.Literal)
            {
                var key = propDef!.TypeShape.CanonicalKey;
                if (!UiLiterals.TryConvert(key, attr.Value, out var converted, out var error))
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.LiteralFormat,
                        $"property '{attr.Name}' of '{element.Tag}' ({key}) {error}", attr.ValueSpan));
                else
                    attr.ConvertedLiteral = converted;
            }
        }

        if (def.Content == UiContentModel.Text && def.ContentTarget is not null &&
            targetWrites.ContainsKey(def.ContentTarget))
        {
            var text = element.Children.OfType<UiText>().FirstOrDefault();
            if (text is not null)
                diags.Add(UiDiagnostics.Error(UiDiagnostics.SameTargetDuplicate,
                    $"'{element.Tag}' text content and the explicit '{def.ContentTarget}' attribute write the same target; remove one",
                    text.Span));
        }

        var contentCtx = def.Content switch
        {
            UiContentModel.TextRuns => Ctx.TextRuns,
            UiContentModel.Text => Ctx.Label,
            UiContentModel.Children => Ctx.Container,
            _ => Ctx.None,
        };

        foreach (var child in element.Children)
        {
            if (contentCtx == Ctx.None)
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.TextNotAllowed,
                    $"'{element.Tag}' declares no content model; add [UiContent] on {def.ClrTypeDisplayName} to accept children",
                    child.Span));
                continue;
            }
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
