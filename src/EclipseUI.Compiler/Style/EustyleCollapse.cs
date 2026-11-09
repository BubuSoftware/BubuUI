using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI.Compiler;

/// <summary>
/// E4 嵌套塌缩（design/17 §4.5）：结构选择器在编译期塌缩为 BEM 命名的派生类，
/// 运行时仍只有「类名直配」一种机制——无选择器引擎。
/// <code>
/// .panel {
///   background: …;
///   &gt; Button { padding: 8; }        → Define("panel__button", …) + 注入规则
///   TextBlock.title { font-size: 20; } → Define("panel__text-block-title", …) + 注入规则
/// }
/// </code>
/// 命名规则：<c>{父派生名}__{kebab(类型名)}{-类约束}</c>（全小写）。同一派生名的多个嵌套段
/// **合并**（CSS 同选择器多块 = 声明合并，声明序后者胜；同名子嵌套同理递归聚合）；
/// 派生名与**显式类名**撞车才是真冲突，报 EUI053（第一版不提供显式改名——拆两个中间类规避）。
/// </summary>
public static class EustyleCollapse
{
    /// <summary>注入链的一段：<c>&gt;</c> 直接子约束 + 类型名（已 kebab 化，与匹配侧同一规则）+ 类约束。</summary>
    public sealed record ChainSeg(bool DirectChild, string? TypeName, string? ClassConstraint);

    /// <summary>
    /// 一条注入规则：挂 <paramref name="RootClass"/> 的元素的静态子树中，按
    /// <paramref name="Chain"/> 匹配到的元素追加 <paramref name="DerivedName"/> 类
    /// （排原类之后 → design/17 §2.2「派生类胜」）。<paramref name="Span"/> 是选择器
    /// 原文位置（诊断用，不进值相等数据）。
    /// </summary>
    public sealed record InjectRule(
        string RootClass, IReadOnlyList<ChainSeg> Chain, string DerivedName, TextSpan Span);

    /// <summary>塌缩产物：<paramref name="Rules"/> 含根类与全部派生类（直接发 Define），
    /// <paramref name="Injects"/> 交 .ui 编译器做静态子树注入，
    /// <paramref name="States"/> 是样式表的 @states 声明（E5——发启动期 Register 序列）。</summary>
    public sealed record Collapsed(
        IReadOnlyList<EustyleRule> Rules, IReadOnlyList<InjectRule> Injects,
        IReadOnlyList<EustyleStateDecl> States);

    public static Collapsed Flatten(EustyleStylesheet sheet, List<UiDiagnostic> diagnostics)
    {
        var rootRules = new List<EustyleRule>();
        // 派生类按名聚合：同名段合并 setters（声明序后者胜），子嵌套在同一派生名下递归聚合；
        // derivedOrder 保持首次产出序（Define 序列可预测）。
        var derivedOrder = new List<string>();
        var derivedSetters = new Dictionary<string, List<EustyleSetter>>(StringComparer.Ordinal);
        var derivedMeta = new Dictionary<string, (TextSpan Span, string RootClass, List<ChainSeg> Chain)>(
            StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in sheet.Rules)
        {
            if (!seen.Add(rule.ClassName))
                ReportConflict(diagnostics, rule.ClassName, rule.NameSpan);
            rootRules.Add(new EustyleRule(rule.ClassName, rule.NameSpan, rule.Setters, Array.Empty<EustyleNested>()));
            Walk(rule.ClassName, rule.ClassName, Array.Empty<ChainSeg>(), rule.Nested);
        }

        var rules = new List<EustyleRule>(rootRules);
        var injects = new List<InjectRule>();
        foreach (var name in derivedOrder)
        {
            var (span, rootClass, chain) = derivedMeta[name];
            rules.Add(new EustyleRule(name, span, derivedSetters[name], Array.Empty<EustyleNested>()));
            injects.Add(new InjectRule(rootClass, chain, name, span));
        }
        return new Collapsed(rules, injects, sheet.States);

        void Walk(string rootClass, string parentDerived, IReadOnlyList<ChainSeg> ancestorChain,
            IReadOnlyList<EustyleNested> nested)
        {
            foreach (var node in nested)
            {
                if (node.TypeName is null && node.ClassConstraint is null)
                    continue; // 解析器已报语法错误；无名的段没有匹配语义

                var derived = parentDerived + "__" + SegmentName(node.TypeName, node.ClassConstraint);
                var seg = new ChainSeg(node.DirectChild,
                    node.TypeName is null ? null : Kebab(node.TypeName), node.ClassConstraint);

                if (derivedSetters.TryGetValue(derived, out var setters))
                {
                    // 同名段合并（CSS 同选择器多块语义）：setters 拼接；子嵌套继续在同一派生名下聚合
                    setters.AddRange(node.Setters);
                }
                else
                {
                    if (!seen.Add(derived))
                        ReportConflict(diagnostics, derived, node.SelectorSpan); // 撞显式类名
                    derivedOrder.Add(derived);
                    derivedSetters[derived] = new List<EustyleSetter>(node.Setters);
                    var chain = new List<ChainSeg>(ancestorChain.Count + 1);
                    chain.AddRange(ancestorChain);
                    chain.Add(seg);
                    derivedMeta[derived] = (node.SelectorSpan, rootClass, chain);
                }

                var nextChain = new List<ChainSeg>(ancestorChain.Count + 1);
                nextChain.AddRange(ancestorChain);
                nextChain.Add(seg);
                Walk(rootClass, derived, nextChain, node.Nested);
            }
        }
    }

    /// <summary>段名：kebab 化类型名（<c>TextBlock</c> → <c>text-block</c>）+ 可选类约束
    /// （<c>TextBlock.title</c> → <c>text-block-title</c>；纯约束 <c>.title</c> → <c>title</c>）。</summary>
    private static string SegmentName(string? typeName, string? classConstraint)
    {
        var type = typeName is null ? "" : Kebab(typeName);
        if (classConstraint is null) return type;
        return type.Length == 0 ? classConstraint : type + "-" + classConstraint;
    }

    /// <summary>PascalCase → kebab-case（<c>TextBox</c> → <c>text-box</c>）；大写后随小写即断词
    /// 的标准算法。公开给模板生成器复用（注入匹配的类型名比较用同一规则）。</summary>
    public static string Kebab(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 8);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                var prevLower = i > 0 && (char.IsLower(pascal[i - 1]) || char.IsDigit(pascal[i - 1]));
                var nextLower = i + 1 < pascal.Length && char.IsLower(pascal[i + 1]);
                if (i > 0 && (prevLower || nextLower)) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static void ReportConflict(List<UiDiagnostic> diagnostics, string name, TextSpan span) =>
        diagnostics.Add(UiDiagnostics.Error(UiDiagnostics.StyleDerivedNameConflict,
            $"style class '{name}' is declared more than once (a nested selector collapses onto an " +
            "explicit class of the same name); rename one of them or split into two intermediate " +
            "classes (EUI053)", span));
}
