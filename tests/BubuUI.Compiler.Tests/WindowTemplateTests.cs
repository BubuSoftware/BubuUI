using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BubuUI.Compiler;
using BubuUI.Compiler.Generator;
using BubuUI.Compiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace BubuUI.Compiler.Tests;

/// <summary>
/// Root inheritance shares object initialization and content typing between windows and controls.
/// </summary>
public class WindowTemplateTests
{
    private sealed class UiAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public UiAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }

    private static CSharpCompilation CreateCompilation(string codeBehind)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(BubuUI.UiObject).Assembly.Location));
        return CSharpCompilation.Create(
            "TemplateProbe",
            codeBehind.Length == 0
                ? Array.Empty<SyntaxTree>()
                : new[] { CSharpSyntaxTree.ParseText(codeBehind) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static GeneratorRunResult RunFiles(params (string Path, string Ui, string? CodeBehind)[] files)
    {
        var texts = new List<AdditionalText>();
        var codeBehind = "";
        foreach (var (path, ui, behind) in files)
        {
            var fixture = NamespaceDeclarationFixture.Prepare(path, behind);
            texts.Add(new UiAdditionalText(path, fixture.Ui(ui)));
            if (behind is not null) codeBehind = fixture.CodeBehind;
        }
        var driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() }, texts.ToArray());
        return driver.RunGenerators(CreateCompilation(codeBehind)).GetRunResult().Results.Single();
    }

    private static string Generated(GeneratorRunResult result)
    {
        Assert.Null(result.Exception);
        return string.Join("\n----\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
    }

    private static bool HasDiagnostic(GeneratorRunResult result, string code) =>
        result.Diagnostics.Any(d => d.Id == code);

    // ---- 生成契约 ----

    [Fact]
    public void WindowTemplate_GeneratesRootTypeBase()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Title="BubuUI Gallery" Width={W}>
                <StackPanel Gap="8" />
            </Window>
            @code {
                public double W = 960;
            }
            """;

        var code = Generated(RunFiles(("/templates/Shell.bui", ui, null)));

        Assert.Contains("partial class Shell : global::BubuUI.Window", code);
        // 内容根按普通根发射：局部变量构造并返回
        Assert.Contains("var el1 = new global::BubuUI.Elements.StackPanel();", code);
        Assert.Contains("this.Content = el1;", code);
        Assert.DoesNotContain("return el0;", code);
        // 字面量 build-once；表达式一次性求值（无缓存字段——守卫基准是基类属性）
        Assert.Contains("this.Title = \"BubuUI Gallery\";", code);
        Assert.Contains("(global::System.Double)(W)", code);
        Assert.Contains("this.Width = v;", code);
        Assert.DoesNotContain("__w", code);
    }

    [Fact]
    public void WindowTemplate_LiveProperties_PushViaRender()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Title={TitleText} Width={W} Height={H}>
                <StackPanel Gap="8" />
            </Window>
            @code {
                public string TitleText = "demo";
                public double W = 960;
                public double H = 640;
            }
            """;

        var code = Generated(RunFiles(("/templates/Shell.bui", ui, null)));

        // Title 尺寸各一路 U；Render 登记
        Assert.Contains("private void U0()", code);
        Assert.Contains("private void U1()", code);
        Assert.Contains("private void U2()", code);
        Assert.Contains("protected override void Render()", code);
        Assert.Contains("if (!global::System.Collections.Generic.EqualityComparer<global::System.Double>.Default.Equals(this.Width, v))", code);
        Assert.Contains("this.Title = v;", code);
        Assert.DoesNotContain("PushWindowTitle", code);
        Assert.Contains("this.Width = v;", code);
        Assert.DoesNotContain("PushWindowClientSize", code);
    }

    [Fact]
    public void WindowTemplate_StaticOnly_HasNoRender()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Title="Static" CloseOnEscape="false">
                <StackPanel Gap="8" />
            </Window>
            """;

        var code = Generated(RunFiles(("/templates/Shell.bui", ui, null)));

        Assert.Contains("this.Title = \"Static\";", code);
        Assert.Contains("this.CloseOnEscape = false;", code);
        Assert.DoesNotContain("protected override void Render()", code);
    }

    [Fact]
    public void WindowTemplate_AllValueExpressionsUseSharedUpdatePipeline()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window MaxFramesPerSecond={fps} AlwaysRender={always}>
                <StackPanel Gap="8" />
            </Window>
            @code {
                public int fps = 60;
                public bool always = true;
            }
            """;

        var code = Generated(RunFiles(("/templates/Shell.bui", ui, null)));

        Assert.Contains("this.MaxFramesPerSecond = v;", code);
        Assert.Contains("this.AlwaysRender = v;", code);
        // 帧调节项是创建期一次性量：不进 live 推送面
        Assert.DoesNotContain("PushWindowClientSize", code);
        Assert.Contains("protected override void Render()", code);
    }

    // ---- 组合限制（EUI060）----

    [Fact]
    public void WindowTemplate_UsedAsComponentTag_ReportsEUI060()
    {
        const string shell = """
            @using BubuUI.Elements
            <Window Title="Shell">
                <StackPanel Gap="8" />
            </Window>
            """;
        const string app = """
            @using BubuUI.Elements
            <StackPanel Gap="8">
                <Shell />
            </StackPanel>
            """;

        var result = RunFiles(
            ("/templates/Shell.bui", shell, null),
            ("/templates/App.bui", app, "public partial class App { public int gap = 4; }"));

        Assert.True(HasDiagnostic(result, "EUI060"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void WindowTag_NonRootPosition_ReportsEUI060()
    {
        const string ui = """
            @using BubuUI.Elements
            <StackPanel Gap="8">
                <Window Title="Nested">
                    <StackPanel Gap="8" />
                </Window>
            </StackPanel>
            """;

        var result = RunFiles(("/templates/App.bui", ui, "public partial class App { public int gap = 4; }"));

        Assert.True(HasDiagnostic(result, "EUI060"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    // ---- 内容与属性能力检查 ----

    [Fact]
    public void WindowTemplate_WithoutChild_IsValidSingleContentObject()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Title="Empty" />
            """;

        var result = RunFiles(("/templates/Shell.bui", ui, null));

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void WindowTemplate_WithTwoChildren_ReportsContentTypeError()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Title="Two">
                <StackPanel Gap="8" />
                <StackPanel Gap="8" />
            </Window>
            """;

        var result = RunFiles(("/templates/Shell.bui", ui, null));

        Assert.True(HasDiagnostic(result, "EUI021"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void WindowTemplate_WithBlockContent_UsesOrdinaryElementContent()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Title="Block">
                @if (flag) {
                    <StackPanel Gap="8" />
                }
            </Window>
            @code {
                public bool flag = true;
            }
            """;

        var result = RunFiles(("/templates/Shell.bui", ui, null));

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void WindowRoot_ElementOnlyAttribute_ReportsTargetTypeError()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Title="A" Class="fancy">
                <StackPanel Gap="8" />
            </Window>
            """;

        var result = RunFiles(("/templates/Shell.bui", ui, null));

        Assert.True(HasDiagnostic(result, "EUI002"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void WindowRoot_UnknownAttribute_SuggestsNearest()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Ttile="Oops">
                <StackPanel Gap="8" />
            </Window>
            """;

        var result = RunFiles(("/templates/Shell.bui", ui, null));

        var diagnostic = Assert.Single(result.Diagnostics.Where(d => d.Id == "EUI002"));
        Assert.Contains("did you mean 'Title'?", diagnostic.GetMessage());
    }

    [Fact]
    public void WindowRoot_ThemeLiteral_RequiresExpression()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Theme="Dark">
                <StackPanel Gap="8" />
            </Window>
            """;

        var result = RunFiles(("/templates/Shell.bui", ui, null));

        // IHostTheme 无字面量转换器 → EUI023 指向表达式形式
        Assert.True(HasDiagnostic(result, "EUI023"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    // ---- code-behind 基类一致性 ----

    [Fact]
    public void WindowTemplate_CodeBehindDerivingComponent_ReportsBaseMismatch()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Title="Shell">
                <StackPanel Gap="8" />
            </Window>
            """;

        var result = RunFiles(("/templates/Shell.bui", ui,
            "public partial class Shell : global::BubuUI.Elements.StackPanel { }"));

        Assert.True(HasDiagnostic(result, "EUI045"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void NormalTemplate_CodeBehindDerivingWindow_ReportsBaseMismatch()
    {
        const string ui = """
            @using BubuUI.Elements
            <StackPanel Gap="8" />
            """;

        var result = RunFiles(("/templates/App.bui", ui,
            "public partial class App : global::BubuUI.Window { }"));

        Assert.True(HasDiagnostic(result, "EUI045"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void WindowTemplate_CodeBehindDerivingWindow_IsAccepted()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Title="Shell">
                <StackPanel Gap="8" />
            </Window>
            """;

        var result = RunFiles(("/templates/Shell.bui", ui,
            "public partial class Shell : global::BubuUI.Window { }"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI045");
        Assert.Contains("partial class Shell : global::BubuUI.Window", Generated(result));
    }

    // ---- 目录侧 ----

    [Fact]
    public void Catalog_UsesRootTypeAndContentAssignability()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Title="Shell">
                <StackPanel Gap="8" />
            </Window>
            """;
        var compilation = CreateCompilation("");
        var declaration = UiTemplateScanner.FromFile(
            "/templates/Shell.bui", NamespaceDeclarationFixture.Ensure("Shell.bui", ui))!;

        Assert.Equal("Window", declaration.RootTag);

        var catalog = UiCatalogFactory.Build(compilation, new[] { declaration });

        var root = Assert.Single(catalog.Elements, e => e.Tag == "Window");
        Assert.Equal("global::BubuUI.Window", root.ClrTypeDisplayName);
        Assert.Contains("Title", root.Parameters.Keys);
        Assert.Contains("Width", root.Parameters.Keys);
        Assert.Contains("Height", root.Parameters.Keys);
        Assert.Contains("CloseOnEscape", root.Parameters.Keys);
        Assert.Contains("Theme", root.Parameters.Keys);
        Assert.True(root.ContentIsSingle);
        Assert.False(root.IsEmbeddable);
        var shell = Assert.Single(catalog.Elements, e => e.Tag == "Shell");
        Assert.False(shell.IsEmbeddable);
        Assert.DoesNotContain(catalog.HiddenTags, h => h.Tag == "Shell");
    }

    [Fact]
    public void Catalog_WindowRootValueEquality_IsStable()
    {
        const string ui = """
            @using BubuUI.Elements
            <Window Title="Shell">
                <StackPanel Gap="8" />
            </Window>
            """;
        var compilation = CreateCompilation("");
        var declaration = UiTemplateScanner.FromFile(
            "/templates/Shell.bui", NamespaceDeclarationFixture.Ensure("Shell.bui", ui))!;

        var first = UiCatalogFactory.Build(compilation, new[] { declaration });
        var second = UiCatalogFactory.Build(compilation, new[] { declaration });

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }
}
