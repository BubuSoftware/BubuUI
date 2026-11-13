using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EclipseUI.Compiler;
using EclipseUI.Compiler.Generator;
using EclipseUI.Compiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

/// <summary>
/// design/20：窗口模板——根节点为保留 &lt;Window&gt; 的 .ui 编译为 WindowComponent 子类
/// （「根标签决定基类」），窗口属性 = 基类 [UiParameter] 面，窗口模板退出可组合表面（EUI060）。
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
        references.Add(MetadataReference.CreateFromFile(typeof(EclipseUI.Component).Assembly.Location));
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
    public void WindowTemplate_GeneratesWindowComponentBase()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Title="EclipseUI Gallery" Width={W}>
                <StackPanel Gap="8" />
            </Window>
            @code {
                public double W = 960;
            }
            """;

        var code = Generated(RunFiles(("C:\\templates\\Shell.ui", ui, null)));

        Assert.Contains("partial class Shell : global::EclipseUI.WindowComponent", code);
        // 内容根按普通根发射：局部变量构造并返回
        Assert.Contains("var el0 = new global::EclipseUI.Elements.StackPanel();", code);
        Assert.Contains("return el0;", code);
        // 字面量 build-once；表达式一次性求值（无缓存字段——守卫基准是基类属性）
        Assert.Contains("this.Title = \"EclipseUI Gallery\";", code);
        Assert.Contains("this.Width = (global::System.Double)(W);", code);
        Assert.DoesNotContain("__w", code);
    }

    [Fact]
    public void WindowTemplate_LiveProperties_PushViaRender()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Title={TitleText} Width={W} Height={H}>
                <StackPanel Gap="8" />
            </Window>
            @code {
                public string TitleText = "demo";
                public double W = 960;
                public double H = 640;
            }
            """;

        var code = Generated(RunFiles(("C:\\templates\\Shell.ui", ui, null)));

        // Title 尺寸各一路 U；Render 登记
        Assert.Contains("private void U0()", code);
        Assert.Contains("private void U1()", code);
        Assert.Contains("private void U2()", code);
        Assert.Contains("protected override void Render()", code);
        Assert.Contains("if (!global::System.Collections.Generic.EqualityComparer<global::System.Double>.Default.Equals(this.Width, v))", code);
        Assert.Contains("PushWindowTitle(this.Title);", code);
        Assert.Contains("PushWindowClientSize(this.Width, this.Height);", code);
    }

    [Fact]
    public void WindowTemplate_StaticOnly_HasNoRender()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Title="Static" CloseOnEscape="false">
                <StackPanel Gap="8" />
            </Window>
            """;

        var code = Generated(RunFiles(("C:\\templates\\Shell.ui", ui, null)));

        Assert.Contains("this.Title = \"Static\";", code);
        Assert.Contains("this.CloseOnEscape = false;", code);
        Assert.DoesNotContain("protected override void Render()", code);
    }

    [Fact]
    public void WindowTemplate_NonLiveExpression_IsBuildOnce()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window MaxFramesPerSecond={fps} AlwaysRender={always}>
                <StackPanel Gap="8" />
            </Window>
            @code {
                public int fps = 60;
                public bool always = true;
            }
            """;

        var code = Generated(RunFiles(("C:\\templates\\Shell.ui", ui, null)));

        Assert.Contains("this.MaxFramesPerSecond = (global::System.Int32)(fps);", code);
        Assert.Contains("this.AlwaysRender = (global::System.Boolean)(always);", code);
        // 帧调节项是创建期一次性量：不进 live 推送面
        Assert.DoesNotContain("PushWindowClientSize", code);
        Assert.DoesNotContain("protected override void Render()", code);
    }

    // ---- 组合限制（EUI060）----

    [Fact]
    public void WindowTemplate_UsedAsComponentTag_ReportsEUI060()
    {
        const string shell = """
            @using EclipseUI.Elements
            <Window Title="Shell">
                <StackPanel Gap="8" />
            </Window>
            """;
        const string app = """
            @using EclipseUI.Elements
            <StackPanel Gap="8">
                <Shell />
            </StackPanel>
            """;

        var result = RunFiles(
            ("C:\\templates\\Shell.ui", shell, null),
            ("C:\\templates\\App.ui", app, "public partial class App { public int gap = 4; }"));

        Assert.True(HasDiagnostic(result, "EUI060"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void WindowTag_NonRootPosition_ReportsEUI060()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Gap="8">
                <Window Title="Nested">
                    <StackPanel Gap="8" />
                </Window>
            </StackPanel>
            """;

        var result = RunFiles(("C:\\templates\\App.ui", ui, "public partial class App { public int gap = 4; }"));

        Assert.True(HasDiagnostic(result, "EUI060"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    // ---- 内容与属性形态（EUI061/EUI062）----

    [Fact]
    public void WindowTemplate_WithoutChild_ReportsEUI061()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Title="Empty" />
            """;

        var result = RunFiles(("C:\\templates\\Shell.ui", ui, null));

        Assert.True(HasDiagnostic(result, "EUI061"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void WindowTemplate_WithTwoChildren_ReportsEUI061()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Title="Two">
                <StackPanel Gap="8" />
                <StackPanel Gap="8" />
            </Window>
            """;

        var result = RunFiles(("C:\\templates\\Shell.ui", ui, null));

        Assert.True(HasDiagnostic(result, "EUI061"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void WindowTemplate_WithBlockContent_ReportsEUI061()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Title="Block">
                @if (flag) {
                    <StackPanel Gap="8" />
                }
            </Window>
            @code {
                public bool flag = true;
            }
            """;

        var result = RunFiles(("C:\\templates\\Shell.ui", ui, null));

        Assert.True(HasDiagnostic(result, "EUI061"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void WindowRoot_ClassAndBind_ReportsEUI062()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Title="A" Class="fancy">
                <StackPanel Gap="8" />
            </Window>
            """;

        var result = RunFiles(("C:\\templates\\Shell.ui", ui, null));

        Assert.True(HasDiagnostic(result, "EUI062"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void WindowRoot_UnknownAttribute_SuggestsNearest()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Ttile="Oops">
                <StackPanel Gap="8" />
            </Window>
            """;

        var result = RunFiles(("C:\\templates\\Shell.ui", ui, null));

        var diagnostic = Assert.Single(result.Diagnostics.Where(d => d.Id == "EUI002"));
        Assert.Contains("did you mean 'Title'?", diagnostic.GetMessage());
    }

    [Fact]
    public void WindowRoot_ThemeLiteral_RequiresExpression()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Theme="Dark">
                <StackPanel Gap="8" />
            </Window>
            """;

        var result = RunFiles(("C:\\templates\\Shell.ui", ui, null));

        // IHostTheme 无字面量转换器 → EUI023 指向表达式形式
        Assert.True(HasDiagnostic(result, "EUI023"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    // ---- code-behind 基类一致性（EUI063）----

    [Fact]
    public void WindowTemplate_CodeBehindDerivingComponent_ReportsEUI063()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Title="Shell">
                <StackPanel Gap="8" />
            </Window>
            """;

        var result = RunFiles(("C:\\templates\\Shell.ui", ui,
            "public partial class Shell : global::EclipseUI.Component { }"));

        Assert.True(HasDiagnostic(result, "EUI063"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void NormalTemplate_CodeBehindDerivingWindowComponent_ReportsEUI063()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Gap="8" />
            """;

        var result = RunFiles(("C:\\templates\\App.ui", ui,
            "public partial class App : global::EclipseUI.WindowComponent { }"));

        Assert.True(HasDiagnostic(result, "EUI063"),
            string.Join("\n", result.Diagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void WindowTemplate_CodeBehindDerivingWindowComponent_IsAccepted()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Title="Shell">
                <StackPanel Gap="8" />
            </Window>
            """;

        var result = RunFiles(("C:\\templates\\Shell.ui", ui,
            "public partial class Shell : global::EclipseUI.WindowComponent { }"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id is "EUI063" or "EUI045");
        Assert.Contains("partial class Shell : global::EclipseUI.WindowComponent", Generated(result));
    }

    // ---- 目录侧 ----

    [Fact]
    public void Catalog_WiresWindowRoot_AndRegistersWindowTemplateAsHidden()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Title="Shell">
                <StackPanel Gap="8" />
            </Window>
            """;
        var compilation = CreateCompilation("");
        var declaration = UiComponentScanner.FromFile(
            "C:\\templates\\Shell.ui", NamespaceDeclarationFixture.Ensure("Shell.ui", ui))!;

        Assert.True(declaration.IsWindowRoot);

        var catalog = UiCatalogFactory.Build(compilation, new[] { declaration });

        // WindowRoot def：WindowComponent 的 [UiParameter] 面
        Assert.NotNull(catalog.WindowRoot);
        Assert.Equal("global::EclipseUI.WindowComponent", catalog.WindowRoot!.ClrTypeDisplayName);
        Assert.Contains("Title", catalog.WindowRoot.Parameters.Keys);
        Assert.Contains("Width", catalog.WindowRoot.Parameters.Keys);
        Assert.Contains("Height", catalog.WindowRoot.Parameters.Keys);
        Assert.Contains("CloseOnEscape", catalog.WindowRoot.Parameters.Keys);
        Assert.Contains("Theme", catalog.WindowRoot.Parameters.Keys);
        Assert.DoesNotContain(catalog.WindowRoot, catalog.Elements);
        // 窗口模板声明 → HiddenTag（EUI060），不进 Elements
        Assert.DoesNotContain(catalog.Elements, e => e.Tag == "Shell");
        var hidden = Assert.Single(catalog.HiddenTags, h => h.Tag == "Shell");
        Assert.Equal("EUI060", hidden.ReasonCode);
    }

    [Fact]
    public void Catalog_WindowRootValueEquality_IsStable()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Window Title="Shell">
                <StackPanel Gap="8" />
            </Window>
            """;
        var compilation = CreateCompilation("");
        var declaration = UiComponentScanner.FromFile(
            "C:\\templates\\Shell.ui", NamespaceDeclarationFixture.Ensure("Shell.ui", ui))!;

        var first = UiCatalogFactory.Build(compilation, new[] { declaration });
        var second = UiCatalogFactory.Build(compilation, new[] { declaration });

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }
}
