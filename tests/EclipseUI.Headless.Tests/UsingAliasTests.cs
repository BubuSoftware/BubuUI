using System.Collections.Generic;
using System.Linq;
using EclipseUI.Compiler;
using EclipseUI.TestKit;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// K31：<c>@using Btn = ThirdParty.Controls.Button</c> 的一体两用语义——
/// ① 生成 <c>using Btn = global::ThirdParty.Controls.Button;</c>，服务 <c>@code</c> 与表达式（Blazor 口径）；
/// ② 让 <c>&lt;Btn/&gt;</c> 在本模板内解析为该类型，且**优先于目录 tag**（本框架扩展）。
///
/// 存在理由：这是解决「第三方控件的类名与内置控件撞 tag」的唯一手段——`[UiTag]` 已退役（标签恒为类型短名，
/// 且它本来也只能改自己库里的类型），而 <c>@using</c> 命名空间在两边都导入时只会得到 EUI016。
/// </summary>
public class UsingAliasTests
{
    /// <summary>造出「内置 Button + Alt.Controls.Button 撞 tag」的目录，与真实重名场景同形。</summary>
    private static UiCatalog CatalogWithAltButton() =>
        UiTestCatalogs.Scan("namespace Alt.Controls { public class Button : StackPanel { } }");

    private static (UiDocument Doc, List<UiDiagnostic> Diags) Analyze(UiCatalog catalog, string text)
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(text, "Test.ui", diags);
        FrontEnd.Analyze(doc, catalog, diags);
        return (doc, diags);
    }

    private static UiElement Root(UiDocument doc) =>
        Assert.IsType<UiElement>(doc.Nodes.Single(node => node is UiElement));

    private static List<UiDiagnostic> ByCode(IEnumerable<UiDiagnostic> diags, string code) =>
        diags.Where(d => d.Code == code).ToList();

    // ------------------------------------------------------------------
    // 语法：两种形态落在同一指令节点的不同字段
    // ------------------------------------------------------------------

    [Fact]
    public void Alias_ParsesIntoAliasAndTarget_AndStaysOutOfNamespaceImports()
    {
        const string ui = """
            @using EclipseUI.Elements
            @using Btn = Alt.Controls.Button

            <StackPanel><Btn /></StackPanel>
            """;

        var (doc, diags) = Analyze(CatalogWithAltButton(), ui);
        Assert.Empty(diags);

        var aliases = doc.Nodes.OfType<UiUsingDirective>().Where(u => u.Alias is not null).ToList();
        var alias = Assert.Single(aliases);
        Assert.Equal("Btn", alias.Alias);
        Assert.Equal("Alt.Controls.Button", alias.AliasTarget);
        Assert.Equal("", alias.Namespace); // 别名不得混进命名空间集合（否则可见性过滤会静默失配）

        // K22 回归钉：命名空间的可见性集合只含真正的 @using 导入
        Assert.Equal(
            new[] { "EclipseUI.Elements" },
            doc.Nodes.OfType<UiUsingDirective>().Where(u => u.Alias is null).Select(u => u.Namespace));
    }

    [Fact]
    public void Alias_TargetWithGlobalPrefix_IsKeptVerbatim()
    {
        const string ui = """
            @using EclipseUI.Elements
            @using Btn = global::Alt.Controls.Button

            <StackPanel><Btn /></StackPanel>
            """;

        var (doc, diags) = Analyze(CatalogWithAltButton(), ui);
        Assert.Empty(diags);

        var alias = doc.Nodes.OfType<UiUsingDirective>().Single(u => u.Alias is not null);
        Assert.Equal("global::Alt.Controls.Button", alias.AliasTarget);
    }

    // ------------------------------------------------------------------
    // 标签解析：别名优先，且绕过命名空间可见性
    // ------------------------------------------------------------------

    [Fact]
    public void Alias_ResolvesTagToTargetType_WithoutImportingItsNamespace()
    {
        // Alt.Controls **没有** @using：这正是别名要解决的场景（目录候选全部不可见）
        const string ui = """
            @using EclipseUI.Elements
            @using Btn = Alt.Controls.Button

            <StackPanel><Btn /></StackPanel>
            """;

        var (doc, diags) = Analyze(CatalogWithAltButton(), ui);
        Assert.Empty(diags);

        var btn = Root(doc).Children.OfType<UiElement>().Single(e => e.Tag == "Btn");
        Assert.Equal("global::Alt.Controls.Button", btn.ResolvedDef!.ClrTypeDisplayName);
    }

    [Fact]
    public void Alias_WinsOverCatalogTag_WhenBothResolve()
    {
        // 别名名与内置 tag 同名：模板内声明是最具体的选择
        const string ui = """
            @using EclipseUI.Elements
            @using Button = Alt.Controls.Button

            <StackPanel><Button /></StackPanel>
            """;

        var (doc, diags) = Analyze(CatalogWithAltButton(), ui);
        Assert.Empty(diags);

        var button = Root(doc).Children.OfType<UiElement>().Single(e => e.Tag == "Button");
        Assert.Equal("global::Alt.Controls.Button", button.ResolvedDef!.ClrTypeDisplayName);
    }

    [Fact]
    public void Alias_DoesNotDisambiguateOtherTags_TheUnrelatedTagStillReportsEUI016()
    {
        const string ui = """
            @using EclipseUI.Elements
            @using Alt.Controls
            @using Btn = Alt.Controls.Button

            <StackPanel>
                <Button />
                <Btn />
            </StackPanel>
            """;

        var (doc, diags) = Analyze(CatalogWithAltButton(), ui);

        // <Button> 两个命名空间都导入 → 歧义；<Btn> 走别名，不参与歧义判断
        Assert.Single(ByCode(diags, UiDiagnostics.TagConflict));
        Assert.Empty(ByCode(diags, UiDiagnostics.UnknownTag));
        var button = Root(doc).Children.OfType<UiElement>().First();
        Assert.Equal("Button", button.Tag);
        Assert.Null(button.ResolvedDef);
    }

    // ------------------------------------------------------------------
    // EUI048：别名本身不合法
    // ------------------------------------------------------------------

    [Fact]
    public void Alias_UnknownTarget_ReportsEUI048Only()
    {
        const string ui = """
            @using EclipseUI.Elements
            @using Btn = Alt.Controls.Missing

            <StackPanel><Btn /></StackPanel>
            """;

        var (_, diags) = Analyze(CatalogWithAltButton(), ui);

        var alias = Assert.Single(ByCode(diags, UiDiagnostics.UsingAliasInvalid));
        Assert.Contains("Alt.Controls.Missing", alias.Message);
        // 使用点不再叠加「unknown tag 'Btn'」：同一处一条错就够了
        Assert.Empty(ByCode(diags, UiDiagnostics.UnknownTag));
    }

    [Fact]
    public void Alias_TargetIsNotAControl_ReportsEUI048()
    {
        const string ui = """
            @using EclipseUI.Elements
            @using Str = System.String

            <StackPanel />
            """;

        var (_, diags) = Analyze(CatalogWithAltButton(), ui);

        var alias = Assert.Single(ByCode(diags, UiDiagnostics.UsingAliasInvalid));
        Assert.Contains("System.String", alias.Message);
    }

    [Fact]
    public void Alias_Duplicate_ReportsEUI048()
    {
        const string ui = """
            @using EclipseUI.Elements
            @using Btn = Alt.Controls.Button
            @using Btn = EclipseUI.Elements.Button

            <StackPanel><Btn /></StackPanel>
            """;

        var (doc, diags) = Analyze(CatalogWithAltButton(), ui);

        Assert.Single(ByCode(diags, UiDiagnostics.UsingAliasInvalid));
        // 先声明者生效
        var btn = Root(doc).Children.OfType<UiElement>().Single(e => e.Tag == "Btn");
        Assert.Equal("global::Alt.Controls.Button", btn.ResolvedDef!.ClrTypeDisplayName);
    }

    [Fact]
    public void Alias_SameNameAsNamespaceImport_ReportsEUI048()
    {
        const string ui = """
            @using EclipseUI.Elements
            @using Btn
            @using Btn = Alt.Controls.Button

            <StackPanel><Btn /></StackPanel>
            """;

        var (_, diags) = Analyze(CatalogWithAltButton(), ui);

        var alias = Assert.Single(ByCode(diags, UiDiagnostics.UsingAliasInvalid));
        Assert.Contains("also imported as a namespace", alias.Message);
        Assert.Empty(ByCode(diags, UiDiagnostics.UnknownTag));
    }

    // ------------------------------------------------------------------
    // 词法错误出口：报专属消息，不落到文本模式（曾报成 EUI006「根层不允许文本」）
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("@using EclipseUI.Elements;\n\n<StackPanel />", "takes no trailing ';'")]
    [InlineData("@using static System.Math\n@using EclipseUI.Elements\n\n<StackPanel />", "'@using static' is not supported")]
    [InlineData("@using Btn = System.Collections.Generic.List<string>\n\n<StackPanel />", "generic type aliases are not supported")]
    [InlineData("@using Btn =\n\n<StackPanel />", "requires a type name after '='")]
    [InlineData("@using A.B = Alt.Controls.Button\n\n<StackPanel />", "must be a single identifier")]
    [InlineData("@using (System.Math)\n\n<StackPanel />", "'@using (...)' is not supported")]
    public void Using_MalformedForms_ReportDedicatedSyntaxError_WithoutTextCascade(string ui, string expected)
    {
        var (_, diags) = Analyze(CatalogWithAltButton(), ui);

        var syntax = Assert.Single(ByCode(diags, UiDiagnostics.Syntax));
        Assert.Contains(expected, syntax.Message);
        // 不得落到「根层不允许文本」或「missing </xxx>」这类与写法无关的级联错误
        Assert.DoesNotContain(diags, d => d.Message.Contains("text content is not allowed"));
        Assert.DoesNotContain(diags, d => d.Message.Contains("missing </"));
    }
}
