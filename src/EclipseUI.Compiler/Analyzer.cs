using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI.Compiler;

/// <summary>语义检查：标签/属性解析到目录、字面量编译期转换、内容模型与 Key/块位置约束。</summary>
public static class TemplateAnalyzer
{
    private enum Ctx { Root, Container, TextRuns, Label, None, ContentSingle, ContentMulti }

    public static void Analyze(UiDocument doc, UiCatalog catalog, List<UiDiagnostic> diags,
        IReadOnlyDictionary<string, UiSlotDef>? ownSlots = null, string? ownTypeDisplay = null)
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

        // 具名插槽宿主（spec/02 §4.12.5）：自身模板中的 <ContentSlot> 按 code-behind 类的
        // [UiSlot] 属性解析。生成器/探针调用带 ownSlots（code-behind 符号扫描，权威——code-behind
        // 类在源码里不派生 Component，目录不会有它的 def）；缺省时按目录显示名与 .ui 文件名
        // 的短名匹配（Samples.TaskList ↔ TaskList，面向无符号的静态前端）。
        var own = ownSlots is not null
            ? new OwnComponent(ownTypeDisplay ?? doc.ClassName, ownSlots)
            : OwnComponentFromCatalog(doc, catalog);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in Nodes(doc.Nodes).OfType<UiContentSlotNode>())
            ValidateContentSlot(host, own, doc, diags, placed);

        foreach (var node in doc.Nodes)
            Visit(node, Ctx.Root, catalog, diags, allowKey: false);
    }

    private static IEnumerable<UiNode> Nodes(IEnumerable<UiNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            switch (node)
            {
                case UiElement element:
                    foreach (var descendant in Nodes(element.Children)) yield return descendant;
                    break;
                case UiIf ifNode:
                    foreach (var descendant in Nodes(ifNode.Children)) yield return descendant;
                    break;
                case UiForeach foreachNode:
                    foreach (var descendant in Nodes(foreachNode.Children)) yield return descendant;
                    break;
            }
        }
    }

    /// <summary>自身组件（生成器传入符号权威表，或静态前端按目录短名匹配）。</summary>
    private sealed record OwnComponent(string DisplayName, IReadOnlyDictionary<string, UiSlotDef> Slots);

    private static OwnComponent? OwnComponentFromCatalog(UiDocument doc, UiCatalog catalog)
    {
        OwnComponent? match = null;
        var count = 0;
        foreach (var def in catalog.Elements)
        {
            if (def.Origin != UiElementOrigin.Component) continue;
            var display = def.ClrTypeDisplayName;
            if (display != doc.ClassName &&
                !display.EndsWith("." + doc.ClassName, StringComparison.Ordinal)) continue;
            count++;
            match ??= new OwnComponent(def.ClrTypeDisplayName, def.Slots);
        }
        return count == 1 ? match : null;
    }

    /// <summary>
    /// 单个 <c>&lt;ContentSlot&gt;</c> 的名字层面校验（EUI022 家族）：非法属性 / 缺名 / 非字面量 /
    /// 带内容 / 自身插槽未知 / 同名重复放置。全部通过才置 <see cref="UiContentSlotNode.ResolvedSlot"/>
    /// （生成侧按它内联拼接）。
    /// </summary>
    private static void ValidateContentSlot(UiContentSlotNode host, OwnComponent? own, UiDocument doc,
        List<UiDiagnostic> diags, HashSet<string> placed)
    {
        UiAttribute? nameAttr = null;
        foreach (var attr in host.Attributes)
        {
            var isName = attr.Name == "Name" && !attr.IsBind && !attr.IsLegacy;
            if (isName && nameAttr is null)
            {
                nameAttr = attr;
                continue;
            }
            diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                isName
                    ? "duplicate 'Name' attribute on <ContentSlot>"
                    : $"'{attr.Name}' is not allowed on <ContentSlot>; a slot host accepts only Name=<literal>",
                attr.NameSpan));
        }

        if (nameAttr is null)
        {
            diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                "slot host requires a Name attribute: <ContentSlot Name=\"...\" />", host.Span));
            return;
        }
        if (nameAttr.Kind != AttrValueKind.Literal)
        {
            diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                "'Name' of <ContentSlot> must be a string literal, not an expression", nameAttr.ValueSpan));
            return;
        }
        if (host.HasChildren)
        {
            diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                "'<ContentSlot>' is a placement marker and takes no content; write <ContentSlot Name=\"...\" />",
                host.Span));
        }

        var name = nameAttr.Value;
        if (own is null)
        {
            diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                $"cannot resolve the slots of the component template of '{doc.ClassName}'; " +
                "[UiSlot] properties on the code-behind class (or its base classes) define this template's slots",
                host.Span));
            return;
        }
        if (!own.Slots.TryGetValue(name, out var slot))
        {
            var hint = Suggest.Nearest(name, own.Slots.Keys);
            diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                $"unknown slot '{name}' on '{own.DisplayName}'" + (hint is null ? "" : $"; did you mean '{hint}'?"),
                nameAttr.ValueSpan));
            return;
        }
        if (!placed.Add(name))
        {
            diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                $"slot '{name}' is placed more than once in this template; a spliced content element must have a single parent",
                host.Span));
            return;
        }

        host.ResolvedSlot = slot;
    }

    /// <summary>
    /// 单个 <c>&lt;Slot&gt;</c> 填充（父组件标签直属）：Name 形状 → def.Slots 解析 → Single 的
    /// 重复填充/多根计数（EUI022 家族）。解析成功才置 <see cref="UiSlotNode.ResolvedSlot"/>；
    /// 子内容按插槽基数走建立 once 内容上下文（继承 EUI040/@bind、EUI010/Key、EUI006/裸文本）。
    /// </summary>
    private static void ValidateSlotFill(UiElement parent, UiElementDef def, UiSlotNode fill,
        UiCatalog catalog, List<UiDiagnostic> diags)
    {
        UiAttribute? nameAttr = null;
        foreach (var attr in fill.Attributes)
        {
            var isName = attr.Name == "Name" && !attr.IsBind && !attr.IsLegacy;
            if (isName && nameAttr is null)
            {
                nameAttr = attr;
                continue;
            }
            diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                isName
                    ? "duplicate 'Name' attribute on <Slot>"
                    : $"'{attr.Name}' is not allowed on <Slot>; a named slot fill accepts only Name=<literal>",
                attr.NameSpan));
        }

        if (nameAttr is null)
        {
            diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                "named slot fill requires a Name attribute: <Slot Name=\"...\">", fill.Span));
            return;
        }
        if (nameAttr.Kind != AttrValueKind.Literal)
        {
            diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                "'Name' of <Slot> must be a string literal, not an expression", nameAttr.ValueSpan));
            return;
        }

        // 槽填充只挂在组件标签上（spec/01 §4.15）；控件容器内的 <Slot> 是位置错误
        if (def.Origin != UiElementOrigin.Component)
        {
            diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                "named slot fills are only allowed as direct children of a component tag", fill.Span));
            return;
        }

        var name = nameAttr.Value;
        if (!def.Slots.TryGetValue(name, out var slot))
        {
            var hint = Suggest.Nearest(name, def.Slots.Keys);
            diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                $"unknown slot '{name}' on component '{parent.Tag}'" + (hint is null ? "" : $"; did you mean '{hint}'?"),
                nameAttr.ValueSpan));
            return;
        }

        if (slot.Cardinality == UiSlotCardinality.Single)
        {
            var prior = false;
            foreach (var sibling in parent.Children)
            {
                if (ReferenceEquals(sibling, fill)) break;
                prior |= sibling is UiSlotNode s &&
                    s.NameAttribute is { Kind: AttrValueKind.Literal } a && a.Value == name;
            }
            if (prior)
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                    $"slot '{name}' of '{parent.Tag}' can be filled only once", nameAttr.ValueSpan));
                return;
            }

            var rootCount = fill.Children.Count(c => c is UiElement or UiIf or UiForeach);
            if (rootCount > 1)
                diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                    $"slot '{name}' of '{parent.Tag}' takes a single root ({slot.ClrTypeDisplayName}); " +
                    $"one child element or block is allowed, found {rootCount}",
                    fill.Span));
            // 多根仍解析：单根赋值取首个（与 §4.12.3 单根内容同口径），错误已报
        }

        fill.ResolvedSlot = slot;
        var childCtx = slot.Cardinality == UiSlotCardinality.Single ? Ctx.ContentSingle : Ctx.ContentMulti;
        foreach (var child in fill.Children)
            Visit(child, childCtx, catalog, diags, allowKey: false);
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
            case UiSlotNode:
                diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                    "named slot fills are only allowed as direct children of a component tag", node.Span));
                break;
            case UiContentSlotNode:
                if (ctx != Ctx.Container)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                        ctx == Ctx.Root
                            ? "'<ContentSlot>' cannot be the template root; place it inside an element of the component's own template"
                            : "'<ContentSlot>' is only allowed directly inside an element container of the component's own template",
                        node.Span));
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
            // 槽节点不进块：填充只允许组件标签直属；宿主 splice 是 build-once，块会按 refresh 重建内容
            if (child is UiSlotNode or UiContentSlotNode)
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                    child is UiSlotNode
                        ? "named slot fills are only allowed as direct children of a component tag"
                        : "'<ContentSlot>' cannot appear inside @if/@foreach blocks; place it directly in an element container",
                    child.Span));
                continue;
            }
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

    /// <summary><c>Ref={ident}</c> 的值必须是简单 C# 标识符（spec/01 §4.3 表）。</summary>
    private static bool IsValidIdentifier(string? name)
    {
        if (name is not { Length: > 0 }) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        foreach (var c in name)
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    /// <summary>
    /// 用点上的属性/事件名 ↔ 扫描侧元数据问题（def.Problems，design/14 §14.5.4）。
    /// 属性按名字精确匹配（EUI019）；<c>On{x}</c> 用点按 <c>xEvent</c> 字段名匹配（EUI020）。
    /// </summary>
    private static UiMetadataProblem? UnusableMemberProblem(UiElementDef def, UiAttribute attr)
    {
        if (attr.Name.Length > 2 && attr.Name.StartsWith("On", StringComparison.Ordinal))
        {
            var fieldName = attr.Name.Substring(2) + "Event";
            var eventProblem = def.Problems.FirstOrDefault(p =>
                p.Code == UiDiagnostics.EventFieldMissing && p.MemberName == fieldName);
            if (eventProblem is not null) return eventProblem;
        }
        return def.Problems.FirstOrDefault(p =>
            p.Code == UiDiagnostics.PropertyNotBindable && p.MemberName == attr.Name);
    }

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
            {
                // 未知标签下的槽节点仍要报位置/形状错误，不能静默漏报
                if (child is UiSlotNode or UiContentSlotNode)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                        "named slot fills are only allowed as direct children of a resolved component tag", child.Span));
                else
                    Visit(child, Ctx.Container, catalog, diags, allowKey: false);
            }
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

            // C5：Ref（spec/01 §4.3/§4.15）。值必须是简单标识符（code-behind 实例字段），
            // Build 时把元素/组件实例赋给它；非法 → EUI029。字段存在性/可写性/类型兼容由
            // 生成器探针校验（EUI029 语义级），分析器（无 Compilation语境）只报语法级。
            if (attr.Name == "Ref")
            {
                var target = attr.Kind == AttrValueKind.Expression ? attr.Value.Trim() : null;
                if (IsValidIdentifier(target))
                    attr.RefTarget = target;
                else
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.RefTargetInvalid,
                        "Ref= requires a simple identifier naming a code-behind instance field, e.g. Ref={saveButton}",
                        attr.ValueSpan));
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
                // C5：EUI019/EUI020 使用点（design/14 §14.5.4）。引用程序集控件无法定位声明，只能把
                // 扫描侧元数据问题（def.Problems）在使用点暴露（消息带完整类型名）。源项目控件的
                // 属性根本不进目录且用户撞的是 EUI002（声明处归 EUI026），所以不在这里触发。
                var metadata = def.IsFromReferencedAssembly ? UnusableMemberProblem(def, attr) : null;
                if (metadata is not null)
                {
                    var kind = metadata.Code == UiDiagnostics.EventFieldMissing ? "event" : "property";
                    diags.Add(UiDiagnostics.Error(metadata.Code,
                        $"{kind} '{attr.Name}' on '{element.Tag}' is not usable: {def.ClrTypeDisplayName} " +
                        $"is declared in a referenced assembly and {metadata.Message}",
                        attr.NameSpan));
                    continue;
                }

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
            // 具名插槽填充（spec/01 §4.15）：无论组件有无 [UiContent]，槽都按 def.Slots 解析；
            // 槽填充不参与默认内容的根计数与内容模型。
            if (child is UiSlotNode fill)
            {
                ValidateSlotFill(element, def, fill, catalog, diags);
                continue;
            }
            if (child is UiContentSlotNode && isComponent)
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                    "'<ContentSlot>' belongs to the component's own template, not to a component use; " +
                    "remove it here", child.Span));
                continue;
            }
            if (contentCtx == Ctx.None)
            {
                // C5：EUI021 使用点（design/14 §14.5.4）——引用程序集控件的 [UiContent] 目标非法时
                // 内容被降级为 None，只在用点暴露；源项目控件保留 EUI006 指引（声明处归 EUI026）。
                var contentProblem = def.IsFromReferencedAssembly
                    ? def.Problems.FirstOrDefault(p => p.Code == UiDiagnostics.ContentTargetInvalid)
                    : null;
                if (contentProblem is not null)
                {
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.ContentTargetInvalid,
                        $"'{element.Tag}' does not accept children: {def.ClrTypeDisplayName} " +
                        $"is declared in a referenced assembly and {contentProblem.Message}",
                        child.Span));
                    continue;
                }

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

    public static void Analyze(UiDocument doc, UiCatalog catalog, List<UiDiagnostic> diags,
        IReadOnlyDictionary<string, UiSlotDef>? ownSlots = null, string? ownTypeDisplay = null)
        => TemplateAnalyzer.Analyze(doc, catalog, diags, ownSlots, ownTypeDisplay);
}
