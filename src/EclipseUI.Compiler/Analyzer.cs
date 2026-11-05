using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI.Compiler;

/// <summary>语义检查：标签/属性解析到目录、字面量编译期转换、内容模型与 Key/块位置约束。</summary>
public static class TemplateAnalyzer
{
    private enum Ctx { Root, Container, TextRuns, Label, None, ContentSingle, ContentMulti }

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

        // 内容上下文保持 build-once 语义穿透嵌套块（@if/@foreach 内仍是内容快照）
        var childCtx = ctx is Ctx.ContentSingle or Ctx.ContentMulti ? ctx : Ctx.Container;
        bool firstRoot = true;
        foreach (var child in children)
        {
            bool isKeyAllowed = allowKeyRoot && firstRoot && child is UiElement;
            firstRoot = false;
            Visit(child, childCtx, catalog, diags, isKeyAllowed);
        }
    }

    private static void VisitText(UiText text, Ctx ctx, List<UiDiagnostic> diags)
    {
        if (ctx is Ctx.Container or Ctx.Root or Ctx.ContentSingle or Ctx.ContentMulti)
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
            // EUI031：具名插槽消费侧语法保留到 G4（spec/01 §6.2）；出现同名真实控件时不走这里。
            if (resolution.Kind == UiTagResolutionKind.NotFound && element.Tag is "Slot" or "ContentSlot")
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.ReservedSyntax,
                    $"'<{element.Tag}>' is reserved for named slots (G4) and has no effect yet", element.TagSpan));
                element.ResolvedDef = null;
                foreach (var child in element.Children)
                    Visit(child, Ctx.Container, catalog, diags, allowKey: false);
                return;
            }
            diags.Add(UiDiagnostics.Error(UiDiagnostics.UnknownTag,
                UnknownTagMessage(element.Tag, resolution, catalog), element.TagSpan));
            element.ResolvedDef = null;
            foreach (var child in element.Children)
                Visit(child, Ctx.Container, catalog, diags, allowKey: false);
            return;
        }

        var def = resolution.Element!;
        element.ResolvedDef = def;
        var isComponent = def.Origin == UiElementOrigin.Component;
        var targetWrites = new Dictionary<string, bool>();
        var attrSeen = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var attr in element.Attributes)
        {
            // EUI025：同一标签重复属性（spec/01 §3.1）。完全重复（同为普值赋值，不含绑定）在此收敛；
            // 名称相同但任一侧是 @bind-P/:bind 的重复仍走目标级 EUI033（S2 行为，spec/01 §3.3）。
            if (attrSeen.TryGetValue(attr.Name, out var seenIsBind))
            {
                if (seenIsBind || attr.IsBind)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.SameTargetDuplicate,
                        $"target '{attr.Name}' of '{element.Tag}' is assigned more than once; keep a single value or binding",
                        attr.NameSpan));
                else
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.DuplicateAttribute,
                        $"'{attr.Name}' is assigned more than once on '{element.Tag}'", attr.NameSpan));
                continue;
            }
            attrSeen.Add(attr.Name, attr.IsBind);

            // EUI031：保留语法——Class/Style（G4 样式）与附加属性（Owner.Prop 点号形式，G4）。
            if (attr.Name is "Class" or "Style")
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.ReservedSyntax,
                    $"'{attr.Name}' is reserved for styling (G4) and has no effect yet", attr.NameSpan));
                continue;
            }
            if (attr.Name.IndexOf('.') >= 0)
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.ReservedSyntax,
                    $"'{attr.Name}' looks like an attached property; attached properties are reserved for G4",
                    attr.NameSpan));
                continue;
            }

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

            // 解析按 §14.5.3 分支：控件 = 保留 → 事件 → 属性；组件 = 保留 → 参数
            // （委托参数就是事件，不做 On* 推断；组件不解析 Events，design/14 §14.6.2）。
            UiEventDef? eventDef = isComponent ? null : def.Events.TryGetValue(attr.Name, out var evt) ? evt : null;
            UiParameterDef? paramDef = isComponent && def.Parameters.TryGetValue(attr.Name, out var prm) ? prm : null;
            UiPropDef? propDef = null;
            if (eventDef is null && paramDef is null) def.Properties.TryGetValue(attr.Name, out propDef);

            if (eventDef is null && propDef is null && paramDef is null)
            {
                var hint = attr.Name.StartsWith("On", StringComparison.Ordinal)
                    ? Suggest.Nearest(attr.Name, def.Events.Keys) ?? Suggest.Nearest(attr.Name, def.Properties.Keys)
                          ?? Suggest.Nearest(attr.Name, def.Parameters.Keys)
                    : Suggest.Nearest(attr.Name, def.Properties.Keys) ?? Suggest.Nearest(attr.Name, def.Events.Keys)
                          ?? Suggest.Nearest(attr.Name, def.Parameters.Keys);
                var message = (isComponent ? "unknown parameter" : "unknown property") +
                    $" '{attr.Name}' on '{element.Tag}'" + (hint is null ? "" : $"; did you mean '{hint}'?");
                diags.Add(UiDiagnostics.Error(UiDiagnostics.UnknownProperty, message, attr.NameSpan));
                continue;
            }

            attr.PropDef = propDef;
            attr.EventDef = eventDef;
            attr.ParamDef = paramDef;

            if (attr.IsBind && isComponent)
            {
                // 组件 @bind 目标（design/14 §14.6.1 / spec/02 §4.4.3）：[UiParameter] + public setter
                // + {P}Changed 委托参数；内容/槽/委托/保留参数与无回调参数 → EUI040。
                if (paramDef is null)
                {
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.BindTargetInvalid,
                        $"'{attr.Name}' of '{element.Tag}' is not a component parameter; @bind-{attr.Name} needs a [UiParameter] property",
                        attr.NameSpan));
                }
                else if (!paramDef.CanWrite || paramDef.IsDelegate || paramDef.IsContent || paramDef.ChangedCallbackName is null)
                {
                    var why = paramDef.IsContent
                        ? "; the content parameter is build-once and cannot be bound"
                        : paramDef.IsDelegate
                            ? "; delegate parameters are events, not two-way sources"
                            : $"; the component must also declare '[UiParameter] public Action<{paramDef.ClrTypeDisplayName}>? {paramDef.Name}Changed'";
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.BindTargetInvalid,
                        $"@bind-{attr.Name} on '{element.Tag}' needs a writable [UiParameter] and a '{paramDef.Name}Changed' delegate parameter{why}",
                        attr.NameSpan));
                }
            }
            else if (attr.IsBind && (eventDef is not null || propDef is not { CanWrite: true }))
            {
                var reason = eventDef is not null ? "is an event" : "is not a bindable property";
                diags.Add(UiDiagnostics.Error(UiDiagnostics.BindTargetInvalid,
                    $"'{attr.Name}' of '{element.Tag}' {reason}; @bind-{attr.Name} needs a public writable property registered as '{attr.Name}Property'",
                    attr.NameSpan));
            }
            if (attr.IsBind && (attr.Kind != AttrValueKind.Expression || string.IsNullOrWhiteSpace(attr.Value)))
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.BindSourceInvalid,
                    $"@bind-{attr.Name} needs a {{expression}} source; a literal has nowhere to write back",
                    attr.ValueSpan));
            }
            else if (attr.IsBind && ctx is Ctx.ContentSingle or Ctx.ContentMulti)
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.BindTargetInvalid,
                    "@bind is not allowed inside component content; content is built once with the parent's values (spec/01 §4.14)",
                    attr.NameSpan));
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
            else if (paramDef is not null && paramDef.IsContent && !attr.IsBind)
            {
                // 内容参数经 P= 赋值 → EUI021（spec/02 §4.12.3）；内容由子节点提供。
                diags.Add(UiDiagnostics.Error(UiDiagnostics.ContentTargetInvalid,
                    $"'{attr.Name}' of '{element.Tag}' is the default content parameter ({paramDef.ClrTypeDisplayName}); " +
                    "provide child content instead of assigning it (P= targets regular parameters only)",
                    attr.NameSpan));
            }
            else if (paramDef is not null && paramDef.IsDelegate && !attr.IsBind &&
                     attr.Kind != AttrValueKind.Expression)
            {
                // 组件事件（委托参数）：字面量/绑定 → EUI004（spec/02 §4.6.2 规则 8）。
                diags.Add(UiDiagnostics.Error(UiDiagnostics.EventNeedsExpression,
                    $"parameter '{attr.Name}' of '{element.Tag}' is a delegate (component event) and requires an {{expression}} handler",
                    attr.ValueSpan));
            }
            else if (!attr.IsBind && attr.Kind == AttrValueKind.Literal)
            {
                var shape = propDef?.TypeShape ?? paramDef!.TypeShape;
                var source = propDef is not null ? "property" : "parameter";
                if (!UiLiterals.HasConverter(shape))
                {
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.NoLiteralConverter,
                        $"{source} '{attr.Name}' of '{element.Tag}' has no literal converter for '{shape.CanonicalKey}'; " +
                        "write an {expression} instead", attr.ValueSpan));
                }
                else if (UiLiterals.TryConvert(shape, attr.Value, out var converted, out var error))
                {
                    attr.ConvertedLiteral = converted;
                }
                else
                {
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.LiteralFormat,
                        $"{source} '{attr.Name}' of '{element.Tag}' {error}", attr.ValueSpan));
                }
            }
        }

        // EUI027：必填参数缺失（design/14 §14.6.1；组件按源码目录 Required 标记）
        if (isComponent)
        {
            foreach (var p in def.Parameters.Values)
            {
                if (p.Required && !attrSeen.ContainsKey(p.Name))
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.RequiredParameterMissing,
                        $"required parameter '{p.Name}' of '{element.Tag}' is missing; pass it on the tag",
                        element.TagSpan));
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

        // 内容上下文：控件按类级 [UiContent]（def.Content）；组件按 [UiContent] 参数
        // （Element? → 单根；IReadOnlyList<Element> → 多根；无 → None，spec/01 §4.14）。
        Ctx contentCtx;
        if (isComponent)
        {
            var contentParam = def.Parameters.Values.FirstOrDefault(p => p.IsContent);
            contentCtx = contentParam is null
                ? Ctx.None
                : contentParam.ContentIsMultiple ? Ctx.ContentMulti : Ctx.ContentSingle;
            if (contentCtx == Ctx.ContentSingle)
            {
                var rootCount = element.Children.Count(c => c is UiElement or UiIf or UiForeach);
                if (rootCount > 1)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.ContentTargetInvalid,
                        $"'{element.Tag}' has a single-root content parameter ({contentParam!.ClrTypeDisplayName}); " +
                        $"only one child element or block is allowed, found {rootCount}",
                        element.Children.First(c => c is UiElement or UiIf or UiForeach).Span));
            }
        }
        else
        {
            contentCtx = def.Content switch
            {
                UiContentModel.TextRuns => Ctx.TextRuns,
                UiContentModel.Text => Ctx.Label,
                UiContentModel.Children => Ctx.Container,
                _ => Ctx.None,
            };
        }

        foreach (var child in element.Children)
        {
            if (contentCtx == Ctx.None)
            {
                var guidance = isComponent
                    ? $"mark an Element? or IReadOnlyList<Element> [UiParameter] property of {def.ClrTypeDisplayName} with [UiContent] to accept default content"
                    : $"add [UiContent] on {def.ClrTypeDisplayName} to accept children";
                diags.Add(UiDiagnostics.Error(UiDiagnostics.TextNotAllowed,
                    $"'{element.Tag}' declares no content model for children; {guidance}", child.Span));
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
