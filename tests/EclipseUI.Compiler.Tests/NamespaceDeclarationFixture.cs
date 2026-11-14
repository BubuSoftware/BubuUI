using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EclipseUI.Compiler.Tests;

/// <summary>
/// 夹具助手：<c>.ui</c> 的 <c>@namespace</c> 是必填契约（spec/02 §2.2），但内联测试模板有八十多处，
/// 逐条改会把断言淹掉。这里一次调用把「<c>.ui</c> 的声明」与「code-behind 的命名空间」成对规整好。
///
/// 契约没有保留值：<c>global</c> 只是普通名字，处于全局命名空间的 code-behind 无法与任何声明相等
/// （C# 写不出 global namespace 声明）。这类夹具被包进合成命名空间 <see cref="SyntheticNamespace"/>。
///
/// 内存文件路径使用正斜杠（如 /templates/Probe.ui），让 Path 的文件名解析在 Windows 与 Unix 一致。
///
/// 幂等：已有 <c>@namespace</c> 的文本原样返回，所以 Run 里按真实 code-behind 规整一次之后，
/// AdditionalText 构造时的兜底不会再动它。
/// </summary>
internal static class NamespaceDeclarationFixture
{
    /// <summary>夹具给「原本位于全局命名空间」的 code-behind 套的合成命名空间。</summary>
    public const string SyntheticNamespace = "TestFixtures";

    /// <summary>
    /// 按 code-behind 规整一个 <c>.ui</c> 夹具。返回值同时给出改写后的 code-behind 与应声明的命名空间，
    /// 调用方必须把两者一起交给生成器，否则 partial 合并不上。
    /// </summary>
    public static UiFixture Prepare(string path, string? codeBehind)
    {
        if (!path.EndsWith(".ui", StringComparison.OrdinalIgnoreCase))
            return new UiFixture(path, codeBehind ?? "", SyntheticNamespace);

        var className = Path.GetFileNameWithoutExtension(path);
        var resolved = ResolveNamespace(codeBehind, className);
        if (resolved is not null)
            return new UiFixture(path, codeBehind!, resolved);

        // 目标类在全局命名空间：整个包进合成命名空间。文本里已有命名空间声明时不动——
        // 外层再套一层会改变 `namespace Alt.Controls { … }` 这类内联块的语义。
        if (codeBehind is null || !TryParse(codeBehind, out var root))
            return new UiFixture(path, codeBehind ?? "", SyntheticNamespace);
        if (root!.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Any())
            return new UiFixture(path, codeBehind, SyntheticNamespace);

        // 行数守恒：前缀接在第一行行首（不插换行），收尾的 } 另起一行追加在末尾，
        // 原有内容的行号与列号（第 0 行除外）都不变，code-behind 位置断言无需改动。
        return new UiFixture(path,
            "namespace " + SyntheticNamespace + " { " + codeBehind + "\n}",
            SyntheticNamespace,
            importSelf: true);
    }

    /// <summary>给 <c>.ui</c> 文本补 <c>@namespace</c>；非 .ui（如 .eustyle）原样返回。</summary>
    public static string Ensure(string path, string content, string? codeBehind = null)
        => Prepare(path, codeBehind).Ui(content);

    /// <summary>按已知命名空间给 <c>.ui</c> 文本补 <c>@namespace</c>（调用方自己决定了空间名）。</summary>
    public static string Declare(string path, string content, string namespaceName)
        => new UiFixture(path, "", namespaceName).Ui(content);

    /// <summary>code-behind 里 <paramref name="className"/> 的命名空间。</summary>
    /// <remarks>
    /// 用语法结构而非正则：同一个夹具常同时含 <c>namespace Alt.Controls { … }</c> 与 global 的
    /// <c>Probe</c>，「取第一个 namespace」会认错。
    /// </remarks>
    private static string? ResolveNamespace(string? codeBehind, string className)
    {
        if (codeBehind is null) return null;
        if (!TryParse(codeBehind, out var root)) return null;

        var declaration = root!.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(type => type.Identifier.Text == className);
        if (declaration is null) return null;

        var parts = new List<string>();
        for (var node = declaration.Parent; node is not null; node = node.Parent)
            if (node is BaseNamespaceDeclarationSyntax ns)
                parts.Insert(0, ns.Name.ToString());
        return parts.Count == 0 ? null : string.Join(".", parts);
    }

    private static bool TryParse(string codeBehind, out SyntaxNode? root)
    {
        try
        {
            root = CSharpSyntaxTree.ParseText(codeBehind).GetRoot();
            return true;
        }
        catch (Exception)
        {
            root = null;
            return false;
        }
    }
}

/// <summary>一个 <c>.ui</c> 夹具：规整后的 code-behind 与它应声明的命名空间。</summary>
internal readonly struct UiFixture
{
    private readonly string _path;
    private readonly bool _importSelf;

    internal UiFixture(string path, string codeBehind, string namespaceName, bool importSelf = false)
    {
        _path = path;
        CodeBehind = codeBehind;
        NamespaceName = namespaceName;
        _importSelf = importSelf;
    }

    /// <summary>改写后的 code-behind（原本在全局命名空间的已被套上合成命名空间）。</summary>
    public string CodeBehind { get; }

    /// <summary>该 <c>.ui</c> 必须声明的命名空间。</summary>
    public string NamespaceName { get; }

    /// <summary>给 <c>.ui</c> 文本补上 <c>@namespace</c> 声明。</summary>
    /// <remarks>
    /// 尾巴上追加而不插在开头：行号断言全部落在前置内容上，追加不移动它们。
    /// 被套进合成命名空间的 code-behind 还要补一条 <c>@using</c>——同模板里引用该命名空间下的
    /// 组件标签（含自引用）与在 C# 里一样，需要它不是「全局可见」的。
    /// </remarks>
    public string Ui(string content)
    {
        if (!_path.EndsWith(".ui", StringComparison.OrdinalIgnoreCase)) return content;
        if (content.Contains("@namespace ", StringComparison.Ordinal)) return content;
        var declared = content + "\n@namespace " + NamespaceName;
        return _importSelf ? declared + "\n@using " + NamespaceName : declared;
    }
}
