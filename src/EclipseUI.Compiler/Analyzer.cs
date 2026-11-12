using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI.Compiler;

/// <summary>语义检查：标签/属性解析到目录、字面量编译期转换、内容模型与 Key/块位置约束。</summary>
public static class TemplateAnalyzer
{
    private enum Ctx { Root, Container, TextRuns, Label, None, ContentSingle, ContentMulti }

    public static void Analyze(UiDocument doc, UiCatalog catalog, List<UiDiagnostic> diags,
        IReadOnlyDictionary<string, UiSlotDef>? ownSlots = null, string? ownTypeDisplay = null,
        IReadOnlyCollection<string>? styleClasses = null,
        IReadOnlyDictionary<string, string>? localStyleClasses = null)
    {
        int roots = doc.Nodes.Count(n => n is UiElement);
        if (roots != 1)
            diags.Add(UiDiagnostics.Error(UiDiagnostics.RootElementExpected,
                $"template must contain exactly one root element, found {roots}", TextSpan.Zero));
        foreach (var node in doc.Nodes)
        {
            if (node is UiIf or UiForeach or UiSwitch)
                diags.Add(UiDiagnostics.Error(UiDiagnostics.TextNotAllowed,
                    "control flow blocks are not allowed at the template root", node.Span));
            // K21/K22：@code 与 @using 是文档顶层的合法节点，与单根元素任意交错；
            // 出现在元素内容/块内时由 Visit 报错（下方 UiCodeBlock/UiUsingDirective case）。
        }

        // K22（D85）/K31：标签在「@using 导入的命名空间 + @using 别名」内解析。作用域在 Visit 链中
        // 显式传参（目录保持不可变，§14.10.3 值相等不受影响）。
        // K33（D111）：`@namespace` 声明的命名空间及其外层链是**隐式可见**的——C# 在命名空间内
        // 用短名引用同命名空间（或外层命名空间）的类型不需要 using，模板没有理由更严。
        var imports = CollectImports(doc.Nodes);
        var scope = new TagScope(imports, CollectAliases(doc.Nodes, imports, catalog, diags),
            EnclosingNamespaces(doc.Namespace));

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
            Visit(node, Ctx.Root, catalog, diags, allowKey: false, scope, styleClasses, localStyleClasses);
    }

    /// <summary>
    /// 根层 @using 收集（K22）：位置不限、按文档顺序去重。**只收命名空间形态**——
    /// K31 别名若混进来，<see cref="UiCatalog.Resolve(string, IReadOnlyList{string})"/> 会把
    /// 别名当成命名空间去匹配，导致所有候选静默不可见。
    /// </summary>
    internal static List<string> CollectImports(IEnumerable<UiNode> nodes)
    {
        var imports = new List<string>();
        foreach (var node in nodes)
            if (node is UiUsingDirective u && u.Namespace.Length > 0 && !imports.Contains(u.Namespace))
                imports.Add(u.Namespace);
        return imports;
    }

    /// <summary>
    /// K31：收集 <c>@using Alias = Ns.Type</c>（别名 → <c>global::Ns.Type</c>）。
    /// 三条校验都在这里一次性报 EUI048：目标不是目录里的已知元素/组件类型、同一模板重复定义、
    /// 与已导入的命名空间同名（C# 也会拒绝，但 CS0576 指到生成文件、指不回 .ui）。
    /// 非法别名以 <c>null</c> 入表——使用点据此抑制「未知标签」，避免同一处出两条错。
    /// </summary>
    internal static Dictionary<string, string?> CollectAliases(
        IEnumerable<UiNode> nodes, IReadOnlyList<string> imports, UiCatalog catalog, List<UiDiagnostic> diags)
    {
        var aliases = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is not UiUsingDirective { Alias: { Length: > 0 } alias } directive) continue;
            var target = AliasTargetDisplay(directive.AliasTarget);

            if (aliases.ContainsKey(alias))
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.UsingAliasInvalid,
                    $"duplicate '@using' alias '{alias}'; each alias may be declared once per template",
                    directive.Span));
                continue;
            }
            if (imports.Contains(alias, StringComparer.Ordinal))
            {
                aliases[alias] = null;
                diags.Add(UiDiagnostics.Error(UiDiagnostics.UsingAliasInvalid,
                    $"'{alias}' is also imported as a namespace; rename the alias: " +
                    $"'@using {alias} = {directive.AliasTarget}' conflicts with '@using {alias}'",
                    directive.Span));
                continue;
            }
            if (catalog.FindByTypeDisplayName(target) is null)
            {
                aliases[alias] = null;
                diags.Add(UiDiagnostics.Error(UiDiagnostics.UsingAliasInvalid,
                    $"alias '{alias}' targets '{directive.AliasTarget}', which is not a UI element or component " +
                    "exposed to templates; check the full type name (the type must derive Element/Component)",
                    directive.NamespaceSpan));
                continue;
            }
            aliases[alias] = target;
        }
        return aliases;
    }

    /// <summary>
    /// K22/K31：生成文件头的 using 子句正文（不含 <c>using</c> 与结尾分号），按模板顺序去重。
    /// 命名空间原样输出；别名输出 <c>Alias = global::Ns.Type</c>——目标必须全限定，
    /// 否则被生成类所在命名空间里的同名类型劫持。别名不进 <see cref="CollectImports"/>。
    /// public：LSP 的探针合成树复用同一份 using 行（与生成器口径逐字一致，design/06 §6.3）。
    /// </summary>
    public static List<string> CollectUsingLines(IEnumerable<UiNode> nodes)
    {
        var lines = new List<string>();
        foreach (var node in nodes)
        {
            if (node is not UiUsingDirective u) continue;
            var line = u.Alias is { Length: > 0 } alias
                ? $"{alias} = {AliasTargetDisplay(u.AliasTarget)}"
                : u.Namespace;
            if (line.Length > 0 && !lines.Contains(line)) lines.Add(line);
        }
        return lines;
    }

    /// <summary>K31：别名目标补 <c>global::</c>（已带则原样）。</summary>
    internal static string AliasTargetDisplay(string target) =>
        target.StartsWith("global::", StringComparison.Ordinal) ? target : "global::" + target;

    /// <summary>
    /// K33：<c>@namespace</c> 的 C# 名称查找链——自身在前，逐步去掉最后一段（<c>A.B.C</c> →
    /// <c>A.B.C</c>／<c>A.B</c>／<c>A</c>）。全局命名空间不进链（它的类型本来就始终可见）。
    /// 未声明 <c>@namespace</c>（<c>EUI055</c> 的场合）返回空链：解析退回纯 <c>@using</c> 口径，
    /// 让「没有命名空间声明」只报一条 EUI055，不连带一堆「未知标签」。
    /// </summary>
    public static IReadOnlyList<string> EnclosingNamespaces(string? ns)
    {
        // 不用 string.IsNullOrEmpty：netstandard2.0 的参考程序集没有 NotNullWhen，
        // 编译器无法据此收窄，会留下 CS8604。
        if (ns is null || ns.Length == 0) return Array.Empty<string>();
        var chain = new List<string>();
        var current = ns;                       // 上面已收窄为非 null
        while (true)
        {
            chain.Add(current);
            int dot = current.LastIndexOf('.');
            if (dot < 0) return chain;
            current = current.Substring(0, dot);
        }
    }

    /// <summary>
    /// K22/K31：单文件的标签解析作用域。别名优先于目录 tag（模板内声明是最具体的选择），
    /// 未命中别名时退回「只在 @using 导入的命名空间内解析」。
    /// </summary>
    internal sealed class TagScope
    {
        private readonly IReadOnlyList<string> _imports;
        private readonly IReadOnlyDictionary<string, string?> _aliases;
        private readonly IReadOnlyList<string> _enclosing;

        public TagScope(IReadOnlyList<string> imports, IReadOnlyDictionary<string, string?> aliases,
            IReadOnlyList<string> enclosingNamespaces)
        {
            _imports = imports;
            _aliases = aliases;
            _enclosing = enclosingNamespaces;
        }

        public UiTagResolution Resolve(UiCatalog catalog, string tag)
        {
            if (_aliases.TryGetValue(tag, out var target))
            {
                // null = 别名本身不合法（EUI048 已报），不再叠加第二条诊断
                var def = target is null ? null : catalog.FindByTypeDisplayName(target);
                return def is null
                    ? new UiTagResolution(UiTagResolutionKind.AliasUnresolved, null, null, null)
                    : new UiTagResolution(UiTagResolutionKind.Found, def, null, null);
            }
            return catalog.Resolve(tag, _imports, _enclosing);
        }
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
                    foreach (var elseIf in ifNode.ElseIfs)
                        foreach (var descendant in Nodes(elseIf.Children)) yield return descendant;
                    if (ifNode.ElseChildren is not null)
                        foreach (var descendant in Nodes(ifNode.ElseChildren)) yield return descendant;
                    break;
                case UiForeach foreachNode:
                    foreach (var descendant in Nodes(foreachNode.Children)) yield return descendant;
                    break;
                case UiSwitch switchNode:
                    foreach (var section in switchNode.Sections)
                        foreach (var descendant in Nodes(section.Children)) yield return descendant;
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
        UiCatalog catalog, List<UiDiagnostic> diags, TagScope scope, IReadOnlyCollection<string>? styleClasses,
        IReadOnlyDictionary<string, string>? localStyleClasses)
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

            var rootCount = fill.Children.Count(c => c is UiElement or UiIf or UiForeach or UiSwitch);
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
            Visit(child, childCtx, catalog, diags, allowKey: false, scope, styleClasses, localStyleClasses);
    }

    private static void Visit(UiNode node, Ctx ctx, UiCatalog catalog, List<UiDiagnostic> diags, bool allowKey,
        TagScope scope, IReadOnlyCollection<string>? styleClasses,
        IReadOnlyDictionary<string, string>? localStyleClasses)
    {
        switch (node)
        {
            case UiElement element:
                VisitElement(element, ctx, catalog, diags, allowKey, scope, styleClasses, localStyleClasses);
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
                VisitBlock(ifNode.ConditionSpan, ifNode.Children, ctx, catalog, diags, allowKeyRoot: false, scope, styleClasses, localStyleClasses);
                foreach (var elseIf in ifNode.ElseIfs)
                    VisitBlock(elseIf.ConditionSpan, elseIf.Children, ctx, catalog, diags, allowKeyRoot: false, scope, styleClasses, localStyleClasses);
                if (ifNode.ElseChildren is not null)
                    VisitBlock(ifNode.ConditionSpan, ifNode.ElseChildren, ctx, catalog, diags, allowKeyRoot: false, scope, styleClasses, localStyleClasses);
                break;
            case UiForeach foreachNode:
                VisitBlock(foreachNode.CollectionSpan, foreachNode.Children, ctx, catalog, diags, allowKeyRoot: true, scope, styleClasses, localStyleClasses);
                break;
            case UiSwitch switchNode:
                VisitSwitch(switchNode, ctx, catalog, diags, scope, styleClasses, localStyleClasses);
                break;
            case UiCodeBlock:
                // K21：文档顶层合法（与单根元素任意交错，Analyze 已放行）；块内/内容中报错
                if (ctx != Ctx.Root)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.Syntax,
                        "'@code' is only allowed at the top level of the document; move it outside the element (K21)",
                        node.Span));
                break;
            case UiStyleBlock:
                // E1（design/17 §4.8）：与 @code 同款「仅顶层」位置校验
                if (ctx != Ctx.Root)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.Syntax,
                        "'@style' is only allowed at the top level of the document; move it outside the element",
                        node.Span));
                break;
            case UiUsingDirective:
                // K22：同上，根层合法
                if (ctx != Ctx.Root)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.Syntax,
                        "'@using' is only allowed at the top level of the document (K22)",
                        node.Span));
                break;
        }
    }

    /// <summary>
    /// @switch（K29）：selector 表达式的类型推断与 case 常量校验在生成器探针完成；
    /// 这里做位置与内容模型校验。分支体是完整内容模板（元素/文本/嵌套块）。
    /// </summary>
    private static void VisitSwitch(UiSwitch switchNode, Ctx ctx, UiCatalog catalog,
        List<UiDiagnostic> diags, TagScope scope, IReadOnlyCollection<string>? styleClasses,
        IReadOnlyDictionary<string, string>? localStyleClasses)
    {
        if (ctx == Ctx.TextRuns || ctx == Ctx.Label)
            diags.Add(UiDiagnostics.Error(UiDiagnostics.TextNotAllowed,
                "control flow blocks are only allowed inside element containers", switchNode.ValueSpan));
        if (switchNode.ValueSpan.Length == 0)
            diags.Add(UiDiagnostics.Error(UiDiagnostics.Syntax, "expression expected", switchNode.ValueSpan));

        var childCtx = ctx is Ctx.ContentSingle or Ctx.ContentMulti ? ctx : Ctx.Container;
        foreach (var section in switchNode.Sections)
        {
            foreach (var child in section.Children)
            {
                if (child is UiSlotNode or UiContentSlotNode)
                {
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                        child is UiSlotNode
                            ? "named slot fills are only allowed as direct children of a component tag"
                            : "'<ContentSlot>' cannot appear inside @switch sections; place it directly in an element container",
                        child.Span));
                    continue;
                }
                Visit(child, childCtx, catalog, diags, allowKey: false, scope, styleClasses, localStyleClasses);
            }
        }
    }

    private static void VisitBlock(TextSpan exprSpan, List<UiNode> children, Ctx ctx, UiCatalog catalog,
        List<UiDiagnostic> diags, bool allowKeyRoot, TagScope scope, IReadOnlyCollection<string>? styleClasses,
        IReadOnlyDictionary<string, string>? localStyleClasses)
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
            Visit(child, childCtx, catalog, diags, isKeyAllowed, scope, styleClasses, localStyleClasses);
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

    /// <summary>
    /// E2（design/17 §4.8）：Class=/Style= 属性校验与 AST 回写。
    /// Class 字面量：空白拆分逐个解析——<see cref="UiAttribute.ClassNames"/> 回写**注册名**
    /// （本文件 @style 类经 <paramref name="localStyleClasses"/> 裸名→前缀名映射；§4.8① 的
    /// 「先本文件、再全局」解析序 = local 命中即遮蔽同名全局类）。
    /// <paramref name="styleClasses"/> 为 null 是无目录语境（探针/直调测试），不校验直接回写；
    /// 生成器语境传真实目录（可为空集 = 项目无任何样式类，写了就报）。未知类名报 EUI031。
    /// Class 表达式：合法入口，string/集合形态由生成器探针推断（ProbeSemantics）。
    /// Style=：只允许字面量声明序列——表达式报 EUI031（元素属性没有状态语境，变体走类名通道）；
    /// 字面量经 <see cref="Eustyle.ParseDeclarations"/> 解析，诊断 EUI050/051/052 的 span
    /// 已经 <c>attr.ValueSpan.Start</c> 偏移映射回宿主 .ui 文件。
    /// </summary>
    private static void ValidateStyleAttribute(UiAttribute attr, IReadOnlyCollection<string>? styleClasses,
        IReadOnlyDictionary<string, string>? localStyleClasses, StylePropertyCatalog properties,
        List<UiDiagnostic> diags)
    {
        if (attr.IsBind)
        {
            diags.Add(UiDiagnostics.Error(UiDiagnostics.BindTargetInvalid,
                $"'{attr.Name}' is a styling attribute; @bind-{attr.Name} is not allowed", attr.NameSpan));
            return;
        }

        if (attr.Kind == AttrValueKind.Expression)
        {
            if (attr.Name == "Style")
                diags.Add(UiDiagnostics.Error(UiDiagnostics.StyleAttributeInvalid,
                    "Style= only accepts a static declaration list; " +
                    "state-driven appearance goes through style classes (Class=)", attr.ValueSpan));
            return;
        }

        if (attr.Name == "Class")
        {
            var names = attr.Value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var resolved = new List<string>(names.Length);
            foreach (var name in names)
            {
                // ① 本文件 @style 块（裸名 → 带组件前缀的注册名，遮蔽同名全局类）
                if (localStyleClasses is not null && localStyleClasses.TryGetValue(name, out var registered))
                {
                    resolved.Add(registered);
                    continue;
                }
                // ② 全局样式目录
                if (styleClasses is null || styleClasses.Contains(name))
                {
                    resolved.Add(name);
                    continue;
                }
                diags.Add(UiDiagnostics.Error(UiDiagnostics.StyleAttributeInvalid,
                    $"unknown style class '{name}'; define it in a .eustyle stylesheet, " +
                    "an @style block, or via StyleRegistry.Define", attr.ValueSpan));
            }
            if (resolved.Count == names.Length)
                attr.ClassNames = resolved;
            return;
        }

        // Style= 字面量：解析声明序列（失败诊断已报，不回写 → 生成侧跳过）
        var styleDiags = new List<UiDiagnostic>();
        var declarations = Eustyle.ParseDeclarations(attr.Value, "", styleDiags, properties, attr.ValueSpan.Start);
        diags.AddRange(styleDiags);
        if (!styleDiags.Exists(static d => d.IsError))
            attr.StyleSetters = declarations.Setters;
    }

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

    private static string UnknownTagMessage(string tag, UiTagResolution resolution, UiCatalog catalog) =>
        resolution.Kind switch
        {
            UiTagResolutionKind.Ambiguous =>
                $"tag '{tag}' is ambiguous between {string.Join(" or ", resolution.Candidates!)}; " +
                $"pick one with a template alias, e.g. '@using {tag}Alt = {WithoutGlobalPrefix(resolution.Candidates![0])}' " +
                $"then use <{tag}Alt/>",
            UiTagResolutionKind.Unavailable =>
                $"tag '{tag}' is not usable: {resolution.Hidden!.Reason}",
            // K22（D85）：目录里有同名标签但其命名空间未 @using 导入——给出缺失 @using 建议
            UiTagResolutionKind.NotFound when resolution.Candidates is { Count: > 0 } =>
                $"tag '{tag}' exists in {string.Join(" or ", resolution.Candidates.Select(NamespaceOfClrType).Where(n => n is not null).Distinct())}; " +
                "add the matching '@using' directive at the top of the template",
            _ => $"unknown tag '{tag}'" +
                (Suggest.Nearest(tag, catalog.ElementTags) is { } hint ? $"; did you mean '{hint}'?" : ""),
        };

    /// <summary>目录 CLR 类型显示名（<c>global::Ns.Type</c>）→ 命名空间；全局命名空间类型返回 null。</summary>
    internal static string? NamespaceOfClrType(string clrTypeDisplayName)
    {
        const string prefix = "global::";
        if (!clrTypeDisplayName.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = clrTypeDisplayName.Substring(prefix.Length);
        int dot = rest.LastIndexOf('.');
        return dot < 0 ? null : rest.Substring(0, dot);
    }

    /// <summary>目录 CLR 类型显示名（<c>global::Ns.Type</c>）→ 可直接写进 <c>@using Alias = …</c> 的类型名。</summary>
    private static string WithoutGlobalPrefix(string clrTypeDisplayName)
        => clrTypeDisplayName.StartsWith("global::", StringComparison.Ordinal)
            ? clrTypeDisplayName.Substring("global::".Length)
            : clrTypeDisplayName;

    private static void VisitElement(UiElement element, Ctx ctx, UiCatalog catalog, List<UiDiagnostic> diags,
        bool allowKey, TagScope scope, IReadOnlyCollection<string>? styleClasses,
        IReadOnlyDictionary<string, string>? localStyleClasses)
    {
        var resolution = scope.Resolve(catalog, element.Tag);
        if (resolution.Kind != UiTagResolutionKind.Found)
        {
            // K31：别名命中但目标不在目录——EUI048 已在 CollectAliases 报过且是 Error，
            // 这里不再叠加「未知标签」，否则用户会看到两条错、其中一条还指向 tag 本身。
            if (resolution.Kind != UiTagResolutionKind.AliasUnresolved)
            {
                // D74 ②：标签解析失败的专属出口（design/14 §14.9.1）。冲突报 EUI016；
                // 不可用标签按目录给出的 ReasonCode 报 EUI017/EUI018/EUI030/EUI031；
                // 只有真正未知的标签才落 EUI001，消息与 span 保持原样。
                var code = resolution.Kind switch
                {
                    UiTagResolutionKind.Ambiguous => UiDiagnostics.TagConflict,
                    UiTagResolutionKind.Unavailable => resolution.Hidden!.ReasonCode,
                    _ => UiDiagnostics.UnknownTag,
                };
                diags.Add(UiDiagnostics.Error(code,
                    UnknownTagMessage(element.Tag, resolution, catalog), element.TagSpan));
            }
            element.ResolvedDef = null;
            foreach (var child in element.Children)
            {
                // 未知标签下的槽节点仍要报位置/形状错误，不能静默漏报
                if (child is UiSlotNode or UiContentSlotNode)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.SlotInvalid,
                        "named slot fills are only allowed as direct children of a resolved component tag", child.Span));
                else
                    Visit(child, Ctx.Container, catalog, diags, allowKey: false, scope, styleClasses, localStyleClasses);
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

            // E2 转正（design/17 §4.8/§5）：Class/Style 属性——@bind 保留（EUI040）；
            // Class 字面量拆分查样式目录（未知类名 EUI031），表达式形态由生成器探针校验；
            // Style= 只允许字面量声明序列（Eustyle 解析，EUI050–052 定位到属性值 span）。
            if (attr.Name is "Class" or "Style")
            {
                ValidateStyleAttribute(attr, styleClasses, localStyleClasses,
                    catalog.StyleProperties, diags);
                continue;
            }
            // G4：附加属性（design/14 §14.5.5）——Owner.Prop 点号形式查目录附加属性表。
            // 已注册 → 正常解析（字面量按值类型转换、表达式动态写入）；未注册 → EUI002
            // 「未知附加属性」（附加属性已实现，拼写错误按未知处理，不再占 EUI031 保留位）。
            if (attr.Name.IndexOf('.') >= 0)
            {
                var dot = attr.Name.IndexOf('.');
                var owner = attr.Name.Substring(0, dot);
                var propName = attr.Name.Substring(dot + 1);
                var attached = propName.IndexOf('.') < 0 ? catalog.FindAttached(owner, propName) : null;
                if (attached is null)
                {
                    var hint = Suggest.Nearest(attr.Name, catalog.AttachedKeys);
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.UnknownProperty,
                        $"unknown attached property '{attr.Name}' on '{element.Tag}'" +
                        (hint is null ? "" : $"; did you mean '{hint}'?"),
                        attr.NameSpan));
                    continue;
                }

                attr.AttachedDef = attached;
                if (attr.IsBind)
                {
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.BindTargetInvalid,
                        $"'{attr.Name}' on '{element.Tag}' is an attached property; @bind-{attr.Name} is not supported — " +
                        "attached properties are written once from the template",
                        attr.NameSpan));
                }
                else if (attr.Kind == AttrValueKind.Literal)
                {
                    var parser = catalog.FindLiteralParser(attached.TypeShape.CanonicalKey);
                    if (!UiLiterals.HasConverter(attached.TypeShape, parser))
                    {
                        diags.Add(UiDiagnostics.Error(UiDiagnostics.NoLiteralConverter,
                            $"attached property '{attr.Name}' of '{element.Tag}' has no literal converter for " +
                            $"'{attached.TypeShape.CanonicalKey}'; write an {{expression}} instead", attr.ValueSpan));
                    }
                    else if (UiLiterals.TryConvert(attached.TypeShape, attr.Value, parser,
                                 out var attachedConverted, out var attachedError))
                    {
                        attr.ConvertedLiteral = attachedConverted;
                    }
                    else
                    {
                        diags.Add(UiDiagnostics.Error(UiDiagnostics.LiteralFormat,
                            $"attached property '{attr.Name}' of '{element.Tag}' {attachedError}", attr.ValueSpan));
                    }
                }
                continue;
            }

            // K27（EUI046）：旧绑定语法 P:bind / P:value 已在 S6 移除（EUI043 停用）。
            // 报错后跳过该属性，不再进入属性/事件/参数解析。
            if (attr.IsLegacy)
            {
                diags.Add(UiDiagnostics.Error(UiDiagnostics.LegacySyntaxRemoved,
                    attr.BindForm == UiBindForm.LegacyTwoWay
                        ? $"'{attr.Name}:bind' has been removed; write @bind-{attr.Name}={{expression}}"
                        : $"'{attr.Name}:value' has been removed; write {attr.Name}=... (a literal) or @bind-{attr.Name}={{expression}}",
                    attr.NameSpan));
                continue;
            }

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
                // spec/01 §4.5：`@bind-P` 的目标必须是可绑属性。名字在类型上**存在**但不可绑
                // （只读、无 public setter、没有 {P}Property、组件上不是 [UiParameter]……）
                // 与「压根没有这个属性」是两种错：前者要换目标或补声明，后者是拼写问题。
                // 目录把前者记进 NonBindableProperties，这里给出原因 → EUI040；拼错仍走 EUI002。
                if (attr.IsBind && def.BindRejection(attr.Name) is { } rejection)
                {
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.BindTargetInvalid,
                        $"@bind-{attr.Name} is not allowed on '{element.Tag}': '{attr.Name}' {rejection}",
                        attr.NameSpan));
                    continue;
                }

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

                // spec/01 §4.2：名字在类型上**存在**但不在模板表面时，说出原因（只读 / 没有
                // {P}Property / 不是 [UiParameter]），而不是给一个 "did you mean"——那在暗示拼写错误。
                // 代码仍是 EUI002（"不可用"与"不存在"对赋值而言是同一个结果，spec/01 §4.4 口径）。
                if (def.BindRejection(attr.Name) is { } notExposed)
                {
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.UnknownProperty,
                        $"'{attr.Name}' of '{element.Tag}' is not a template " +
                        $"{(isComponent ? "parameter" : "property")}: it {notExposed}",
                        attr.NameSpan));
                    continue;
                }

                // 放在 EUI019 之后：引用程序集的破损 `{P}Property` 有更精确的声明级说明。
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
                    // 形状问题的原因来自目录（扫描期按符号判过 arity 与类型身份，D110）：
                    // 只说「缺少回调」会把人引去加一个已经存在的成员。
                    var why = !paramDef.CanWrite
                        ? "; the parameter must be a public property with a public setter, otherwise the write-back cannot compile"
                        : paramDef.IsContent
                            ? "; the content parameter is build-once and cannot be bound"
                            : paramDef.IsDelegate
                                ? "; delegate parameters are events, not two-way sources"
                                : paramDef.ChangedShapeProblem is { } shapeProblem
                                    ? "; " + shapeProblem
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
                var parser = catalog.FindLiteralParser(shape.CanonicalKey);
                if (!UiLiterals.HasConverter(shape, parser))
                {
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.NoLiteralConverter,
                        $"{source} '{attr.Name}' of '{element.Tag}' has no literal converter for '{shape.CanonicalKey}'; " +
                        "write an {expression} instead", attr.ValueSpan));
                }
                else if (UiLiterals.TryConvert(shape, attr.Value, parser, out var converted, out var error))
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
                var rootCount = element.Children.Count(c => c is UiElement or UiIf or UiForeach or UiSwitch);
                if (rootCount > 1)
                    diags.Add(UiDiagnostics.Error(UiDiagnostics.ContentTargetInvalid,
                        $"'{element.Tag}' has a single-root content parameter ({contentParam!.ClrTypeDisplayName}); " +
                        $"only one child element or block is allowed, found {rootCount}",
                        element.Children.First(c => c is UiElement or UiIf or UiForeach or UiSwitch).Span));
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
                ValidateSlotFill(element, def, fill, catalog, diags, scope, styleClasses, localStyleClasses);
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
            Visit(child, contentCtx, catalog, diags, allowKey: false, scope, styleClasses, localStyleClasses);
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
        IReadOnlyDictionary<string, UiSlotDef>? ownSlots = null, string? ownTypeDisplay = null,
        IReadOnlyCollection<string>? styleClasses = null,
        IReadOnlyDictionary<string, string>? localStyleClasses = null)
        => TemplateAnalyzer.Analyze(doc, catalog, diags, ownSlots, ownTypeDisplay, styleClasses, localStyleClasses);
}
