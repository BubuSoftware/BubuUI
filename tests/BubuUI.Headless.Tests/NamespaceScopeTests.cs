using System;
using System.Collections.Generic;
using System.Linq;
using BubuUI.Compiler;
using BubuUI.TestKit;
using Xunit;

namespace BubuUI.Headless.Tests;

/// <summary>
/// K33（D111）：<c>@namespace</c> 声明的命名空间链**隐式可见**——同命名空间（或外层命名空间）里的
/// 标签不需要 <c>@using</c>，与 C# 里用短名引用同命名空间类型一致；且内层优先于 <c>@using</c>。
///
/// 存在理由：K22 把「目录短名 = 标签」改成了「导入命名空间内解析」，却把模板当成例外——同一
/// 命名空间里的组件也要写一行 <c>@using</c>。C# 里没有这条规矩，用户每次都会被绊一下。
/// </summary>
public class NamespaceScopeTests
{
    /// <summary>同一 tag 在两个命名空间各有一个类型，用来验证「内层优先」与「歧义」分界。</summary>
    private static UiCatalog TwoCards() => UiTestCatalogs.Scan("""
        namespace App { public class Card : StackPanel { } }
        namespace Other { public class Card : StackPanel { } }
        """);

    private static (UiDocument Doc, List<UiDiagnostic> Diags) Analyze(UiCatalog catalog, string text)
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(text, "Test.bui", diags);
        FrontEnd.Analyze(doc, catalog, diags);
        return (doc, diags);
    }

    private static UiElement Root(UiDocument doc) =>
        Assert.IsType<UiElement>(doc.Nodes.Single(node => node is UiElement));

    private static string? ResolvedType(UiDocument doc) =>
        Root(doc).Children.OfType<UiElement>().Single(e => e.Tag == "Card").ResolvedDef?.ClrTypeDisplayName;

    // ------------------------------------------------------------------
    // 隐式可见：同命名空间 / 外层命名空间
    // ------------------------------------------------------------------

    [Fact]
    public void SameNamespace_ResolvesWithoutUsing()
    {
        const string ui = """
            @using BubuUI.Elements
            @namespace App

            <StackPanel><Card /></StackPanel>
            """;

        var (doc, diags) = Analyze(TwoCards(), ui);

        Assert.Empty(diags);
        Assert.Equal("global::App.Card", ResolvedType(doc));
    }

    [Fact]
    public void EnclosingNamespace_ResolvesWithoutUsing()
    {
        // C# 的简单名查找会逐级往外走：`namespace App.Gallery` 内可以直接用 `App.Card`
        const string ui = """
            @using BubuUI.Elements
            @namespace App.Gallery

            <StackPanel><Card /></StackPanel>
            """;

        var (doc, diags) = Analyze(TwoCards(), ui);

        Assert.Empty(diags);
        Assert.Equal("global::App.Card", ResolvedType(doc));
    }

    [Fact]
    public void SameNamespace_WinsOverImportedNamespace()
    {
        // 内层已声明同名类型时，外层/导入命名空间的同名类型在 C# 里根本不可见——
        // 所以这里必须是「唯一命中」，不能塌成 EUI016 歧义。
        const string ui = """
            @using BubuUI.Elements
            @using Other
            @namespace App

            <StackPanel><Card /></StackPanel>
            """;

        var (doc, diags) = Analyze(TwoCards(), ui);

        Assert.Empty(diags);
        Assert.Equal("global::App.Card", ResolvedType(doc));
    }

    // ------------------------------------------------------------------
    // 边界：隐式可见不等于放开一切
    // ------------------------------------------------------------------

    [Fact]
    public void Alias_StillWinsOverSameNamespace()
    {
        // K31：别名是模板内最具体的选择，优先级高于「同命名空间隐式可见」
        const string ui = """
            @using BubuUI.Elements
            @using Card = Other.Card
            @namespace App

            <StackPanel><Card /></StackPanel>
            """;

        var (doc, diags) = Analyze(TwoCards(), ui);

        Assert.Empty(diags);
        Assert.Equal("global::Other.Card", ResolvedType(doc));
    }

    [Fact]
    public void InnermostNamespace_BeatsOuterOne_WithTheSameTag()
    {
        // 外层也是候选：`App.Card` 与 `App.Gallery.Card` 同名。C# 取最内层，模板必须一致，
        // 而不是把两层当成歧义（EUI016）——那会让「在外层加一个同名控件」静默弄坏内层模板。
        var catalog = UiTestCatalogs.Scan("""
            namespace App { public class Card : StackPanel { } }
            namespace App.Gallery { public class Card : StackPanel { } }
            """);
        const string ui = """
            @using BubuUI.Elements
            @namespace App.Gallery

            <StackPanel><Card /></StackPanel>
            """;

        var (doc, diags) = Analyze(catalog, ui);

        Assert.Empty(diags);
        Assert.Equal("global::App.Gallery.Card", ResolvedType(doc));
    }

    [Fact]
    public void UnimportedNamespace_StillReportsMissingUsing()
    {
        var catalog = UiTestCatalogs.Scan("namespace Other { public class Card : StackPanel { } }");
        const string ui = """
            @using BubuUI.Elements
            @namespace App

            <StackPanel><Card /></StackPanel>
            """;

        var (doc, diags) = Analyze(catalog, ui);

        var unknown = Assert.Single(diags, d => d.Code == UiDiagnostics.UnknownTag);
        Assert.Contains("Other", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("add the matching '@using'", unknown.Message, StringComparison.Ordinal);
        Assert.Null(Root(doc).Children.OfType<UiElement>().Single(e => e.Tag == "Card").ResolvedDef);
    }

    [Fact]
    public void NoNamespaceDeclaration_FallsBackToPlainUsing()
    {
        // 未声明 @namespace 时（EUI055 的场合）不做隐式可见：否则「没写声明」会顺带改变
        // 标签解析口径，让人分不清错误来自哪一条规则。
        const string ui = """
            @using BubuUI.Elements
            @using App

            <StackPanel><Card /></StackPanel>
            """;

        var (doc, diags) = Analyze(TwoCards(), ui);

        Assert.Empty(diags);
        Assert.Equal("global::App.Card", ResolvedType(doc));
    }

    [Fact]
    public void EnclosingNamespaceChain_IsInnermostFirst()
    {
        Assert.Equal(new[] { "A.B.C", "A.B", "A" }, TemplateAnalyzer.EnclosingNamespaces("A.B.C"));
        Assert.Empty(TemplateAnalyzer.EnclosingNamespaces(null));
        Assert.Empty(TemplateAnalyzer.EnclosingNamespaces(""));
        Assert.Equal(new[] { "Single" }, TemplateAnalyzer.EnclosingNamespaces("Single"));
    }
}
