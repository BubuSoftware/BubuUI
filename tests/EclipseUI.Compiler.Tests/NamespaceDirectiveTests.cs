using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EclipseUI.Compiler.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

/// <summary>
/// spec/02 §2.2：<c>@namespace</c> 是必填契约。解析层只收集与形状校验（EUI100），
/// 「缺失」与「与 code-behind 不一致」由生成器报（EUI055 / EUI056）——这样内存态解析
/// （IDE 的 Tier1、单测内联模板）不会被强制，而真实构建一定拦得住。
/// </summary>
public class NamespaceDirectiveTests
{
    private const string UiPath = "C:\\templates\\Probe.ui";

    private sealed class UiText : AdditionalText
    {
        private readonly SourceText _text;

        public UiText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }
        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }

    private static GeneratorRunResult Run(string ui, string codeBehind = "public partial class Probe { }")
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(UiObject).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "NamespaceProbe",
            new[] { CSharpSyntaxTree.ParseText(codeBehind) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiText(UiPath, ui) });
        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    private static string Generated(GeneratorRunResult result) =>
        result.GeneratedSources.Single().SourceText.ToString();

    // ---- 解析 ----

    [Fact]
    public void Parse_CapturesNamespaceAndSpan()
    {
        const string text = "@namespace Gallery.Samples\n<StackPanel />\n";
        var diags = new List<UiDiagnostic>();

        var doc = FrontEnd.Parse(text, "Probe.ui", diags);

        Assert.Equal("Gallery.Samples", doc.Namespace);
        Assert.Equal(text.IndexOf("Gallery.Samples", StringComparison.Ordinal), doc.NamespaceSpan.Start);
        Assert.Equal("Gallery.Samples".Length, doc.NamespaceSpan.Length);
        Assert.DoesNotContain(diags, d => d.IsError);
    }

    [Fact]
    public void Parse_NoDeclaration_LeavesNamespaceNull()
    {
        var doc = FrontEnd.Parse("<StackPanel />\n", "Probe.ui", new List<UiDiagnostic>());

        Assert.Null(doc.Namespace);
    }

    [Fact]
    public void Duplicate_ReportsSyntax_AndKeepsTheFirst()
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse("@namespace A\n@namespace B\n<StackPanel />\n", "Probe.ui", diags);

        Assert.Equal("A", doc.Namespace);
        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax && d.Message.Contains("duplicate"));
    }

    [Fact]
    public void NestedDeclaration_ReportsSyntax()
    {
        var diags = new List<UiDiagnostic>();
        FrontEnd.Parse("@if (true) {\n@namespace A\n}\n<StackPanel />\n", "Probe.ui", diags);

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax && d.Message.Contains("top level"));
    }

    [Theory]
    [InlineData("@namespace Gallery;\n<StackPanel />\n")]  // 分号：与 @using 同口径，不接受
    [InlineData("@namespace\n<StackPanel />\n")]           // 缺值
    [InlineData("@namespace 9Bad\n<StackPanel />\n")]      // 非标识符起始
    public void Malformed_ReportsSyntax(string ui)
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(ui, "Probe.ui", diags);

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax);
        Assert.Null(doc.Namespace);
    }

    // ---- 生成器：必填 ----

    [Fact]
    public void Missing_ReportsEui055_AndProducesNoSource()
    {
        var result = Run("@using EclipseUI.Elements\n<StackPanel />\n");

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI055");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("@namespace", diag.GetMessage());
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Missing_WithoutCodeBehind_StillReportsEui055()
    {
        var result = Run("<StackPanel />\n@code { public int n; }\n",
            "public partial class Other { }");

        Assert.Contains(result.Diagnostics, d => d.Id == "EUI055");
    }

    // ---- 生成器：一致性 ----

    [Fact]
    public void Mismatch_ReportsEui056_AtDeclaration()
    {
        var result = Run("@using EclipseUI.Elements\n@namespace Gallery\n<StackPanel />\n",
            "namespace Demo;\npublic partial class Probe { }");

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI056");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("Gallery", diag.GetMessage());
        Assert.Contains("Demo", diag.GetMessage());
        Assert.Equal(11, diag.Location.GetLineSpan().StartLinePosition.Character);
    }

    [Fact]
    public void Match_GeneratesInDeclaredNamespace()
    {
        var result = Run("@using EclipseUI.Elements\n@namespace Demo\n<StackPanel />\n",
            "namespace Demo;\npublic partial class Probe { }");

        Assert.Empty(result.Diagnostics);
        Assert.Contains("namespace Demo;", Generated(result));
    }

    [Fact]
    public void NoCodeBehind_DeclaredNamespaceWinsOverRootNamespace()
    {
        // 无 Probe 类但有 @code（K23 ④）→ 不该报 EUI005；命名空间必须取声明值而非 RootNamespace。
        var result = Run("@using EclipseUI.Elements\n@namespace Gallery.Samples\n<StackPanel />\n@code { public int n; }\n",
            "public partial class Other { }");

        Assert.Empty(result.Diagnostics);
        Assert.Contains("namespace Gallery.Samples;", Generated(result));
    }

    // ---- 无保留值：`global` 只是普通名字 ----

    [Fact]
    public void GlobalNamespaceCodeBehind_ReportsEui056_AndAsksToMoveItIntoANamespace()
    {
        // 全局命名空间的 code-behind 无法与任何声明相等——C# 写不出 global namespace 声明，
        // 所以契约在这里没有可写形式，只能把类搬进命名空间。
        var result = Run("@using EclipseUI.Elements\n@namespace Demo\n<StackPanel />\n");

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI056");
        Assert.Contains("global namespace", diag.GetMessage());
        Assert.Contains("move", diag.GetMessage());
    }

    [Fact]
    public void GlobalKeyword_IsAnOrdinaryName_NotAnEscapeHatch()
    {
        // `@namespace global` 不表示「无命名空间」，对全局 code-behind 依然不一致。
        var result = Run("@using EclipseUI.Elements\n@namespace global\n<StackPanel />\n");

        Assert.Contains(result.Diagnostics, d => d.Id == "EUI056");
    }
}
