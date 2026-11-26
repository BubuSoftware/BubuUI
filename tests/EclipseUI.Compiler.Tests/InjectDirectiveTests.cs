using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EclipseUI.Compiler.Generator;
using EclipseUI.TestKit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

/// <summary>
/// K37（spec/01 §4.18 / spec/02 §4.7）：<c>@inject</c> 服务注入——解析、诊断与生成契约。
/// 诊断口径：形态/位置错误 EUI100（语法层）、重复属性名 EUI065（短路生成）、
/// 与生成保留名/@code/code-behind 成员冲突 EUI045（不短路生成，由 C# 给权威错误）。
/// </summary>
public class InjectDirectiveTests
{
    private const string UiPath = "/templates/Probe.ui";

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

    private static List<MetadataReference> References()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(UiObject).Assembly.Location));
        return references;
    }

    private static GeneratorRunResult Run(string ui, string codeBehind = "namespace Demo;\npublic partial class Probe { }")
    {
        var compilation = CSharpCompilation.Create(
            "InjectProbe",
            new[] { CSharpSyntaxTree.ParseText(codeBehind) },
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiText(UiPath, ui) });
        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    private static string Generated(GeneratorRunResult result) =>
        result.GeneratedSources.Single().SourceText.ToString();

    // ---- 解析 ----

    [Theory]
    [InlineData("@inject ICache Cache", "ICache")]
    [InlineData("@inject ICache Cache;", "ICache")]
    [InlineData("@inject My.Services.ICache Cache", "My.Services.ICache")]
    [InlineData("@inject global::My.Services.ICache Cache;", "global::My.Services.ICache")]
    public void Parse_CapturesTypeNameAndPropertyName(string directive, string expectedType)
    {
        var text = directive + "\n<StackPanel />\n";

        var doc = FrontEnd.Parse(text, "Probe.ui", new List<UiDiagnostic>());

        var inject = Assert.Single(doc.Nodes.OfType<UiInjectDirective>());
        Assert.Equal(expectedType, inject.TypeName);
        Assert.Equal("Cache", inject.PropertyName);
        Assert.Equal(expectedType, text.Substring(inject.TypeNameSpan.Start, inject.TypeNameSpan.Length));
        Assert.Equal("Cache", text.Substring(inject.PropertyNameSpan.Start, inject.PropertyNameSpan.Length));
    }

    [Fact]
    public void Lexer_InjectAcceptsOptionalSemicolonsAndKeepsSpans()
    {
        foreach (var ending in new[] { "", ";", " \t; \t" })
        foreach (var newline in new[] { "", "\n", "\r\n" })
        {
            var text = "@inject ICache Cache" + ending + newline;

            var token = new Lexer(text).Next();

            Assert.Equal(TokenKind.Directive, token.Kind);
            Assert.Equal("inject", token.Text);
            Assert.Equal("ICache", token.Aux);
            Assert.Equal("Cache", token.Aux2);
            Assert.Equal("ICache", text.Substring(token.InnerSpan.Start, token.InnerSpan.Length));
            Assert.Equal("Cache", text.Substring(token.InnerSpan2.Start, token.InnerSpan2.Length));
        }
    }

    [Fact]
    public void Parse_MultipleInjects_CollectedInDeclarationOrder()
    {
        var doc = FrontEnd.Parse("@inject IA A\n@inject IB B;\n<StackPanel />\n", "Probe.ui", new List<UiDiagnostic>());

        var injects = doc.Nodes.OfType<UiInjectDirective>().ToList();
        Assert.Equal(new[] { "IA", "IB" }, injects.Select(i => i.TypeName));
        Assert.Equal(new[] { "A", "B" }, injects.Select(i => i.PropertyName));
    }

    // ---- 诊断：形态 / 位置（EUI100）----

    [Theory]
    [InlineData("@inject\n")]                    // 无参数
    [InlineData("@inject ICache\n")]             // 缺属性名（单参数）
    [InlineData("@inject Cache\n")]              // 只有类型名
    [InlineData("@inject ICache Cache extra\n")] // 行尾残余
    [InlineData("@inject ICache 9Bad\n")]        // 非法属性名
    [InlineData("@inject 9Type Name\n")]         // 非法类型名
    public void Malformed_ReportsSyntax(string directive)
    {
        var diags = new List<UiDiagnostic>();

        FrontEnd.Parse(directive + "<StackPanel />\n", "Probe.ui", diags);

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax);
    }

    [Fact]
    public void GenericTypeName_ReportsSyntax_WithDedicatedMessage()
    {
        var diags = new List<UiDiagnostic>();

        FrontEnd.Parse("@inject System.Collections.Generic.IEnumerable<string> Items\n<StackPanel />\n",
            "Probe.ui", diags);

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax && d.Message.Contains("generic"));
    }

    [Fact]
    public void NestedDirective_ReportsSyntax()
    {
        var diags = new List<UiDiagnostic>();
        var doc = FrontEnd.Parse(
            "@using EclipseUI.Elements\n@if (true) {\n@inject ICache Cache\n}\n<StackPanel />\n",
            "Probe.ui", diags);
        Assert.DoesNotContain(diags, d => d.IsError);

        FrontEnd.Analyze(doc, UiTestCatalogs.BuiltIn(), diags);

        Assert.Contains(diags, d => d.Code == UiDiagnostics.Syntax && d.Message.Contains("top level"));
    }

    // ---- 诊断：重复属性名（EUI065，短路生成）----

    [Fact]
    public void DuplicatePropertyName_ReportsEui065_AndProducesNoSource()
    {
        var result = Run(
            "@namespace Demo\n@using EclipseUI.Elements\n" +
            "@inject System.IServiceProvider Cache\n@inject System.IServiceProvider Cache\n<StackPanel />\n");

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI065");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("Cache", diag.GetMessage());
        Assert.Empty(result.GeneratedSources);
    }

    // ---- 诊断：成员冲突（EUI045，不短路生成）----

    [Fact]
    public void GeneratedNameCollision_ReportsEui045_ButKeepsGeneratedCode()
    {
        var result = Run(
            "@namespace Demo\n@using EclipseUI.Elements\n@inject System.IServiceProvider Render\n<StackPanel />\n");

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI045");
        Assert.Contains("Render", diag.GetMessage());
        Assert.Single(result.GeneratedSources);
    }

    [Fact]
    public void CodeMemberConflict_ReportsEui045_ButKeepsGeneratedCode()
    {
        var result = Run(
            "@namespace Demo\n@using EclipseUI.Elements\n@inject System.IServiceProvider Cache\n<StackPanel />\n" +
            "@code { public int Cache; }\n");

        Assert.Contains(result.Diagnostics, d => d.Id == "EUI045" && d.GetMessage().Contains("Cache"));
        Assert.Single(result.GeneratedSources);
    }

    [Fact]
    public void CodeBehindConflict_ReportsEui045_ButKeepsGeneratedCode()
    {
        var result = Run(
            "@namespace Demo\n@using EclipseUI.Elements\n@inject System.IServiceProvider Cache\n<StackPanel />\n",
            "namespace Demo;\npublic partial class Probe { public int Cache; }");

        Assert.Contains(result.Diagnostics, d => d.Id == "EUI045" && d.GetMessage().Contains("Cache"));
        Assert.Single(result.GeneratedSources);
    }

    // ---- 生成形态（spec/02 §4.7 / R11）----

    [Fact]
    public void Generated_EmitsPropertyAndFrontLoadedResolution()
    {
        var result = Run(
            "@namespace Demo\n@using EclipseUI.Elements\n@inject System.IServiceProvider Cache\n<StackPanel />\n");

        Assert.Empty(result.Diagnostics);
        var code = Generated(result);

        // 属性形态：get-only 对外、private set 供类内解析赋值
        Assert.Contains("public System.IServiceProvider Cache { get; private set; } = null!;", code);
        // 解析形态：Application.Current?.Services.GetService(typeof(T)) ?? throw fail-fast
        Assert.Contains("Cache = (System.IServiceProvider)(global::EclipseUI.Application.Current?.Services", code);
        Assert.Contains("GetService(typeof(System.IServiceProvider))", code);
        Assert.Contains("throw new global::System.InvalidOperationException", code);

        // 语句序（R11）：解析 → 构造作用域 → base.InitializeUi()（根输入随后）
        var resolve = code.IndexOf("Cache = (System.IServiceProvider)", StringComparison.Ordinal);
        var scope = code.IndexOf("using var __euiConstruction", StringComparison.Ordinal);
        var baseInit = code.IndexOf("base.InitializeUi();", StringComparison.Ordinal);
        Assert.True(resolve >= 0 && resolve < scope, "resolution must precede the construction scope");
        Assert.True(scope >= 0 && scope < baseInit, "construction scope must precede base.InitializeUi()");

        // 两处 #line 映射：属性声明行指向类型名、解析语句行指向 @inject 声明
        var lines = code.Split('\n');
        var propertyLine = Array.FindIndex(lines,
            l => l.Contains("public System.IServiceProvider Cache { get; private set; } = null!;"));
        Assert.True(propertyLine > 0 && lines[propertyLine - 1].StartsWith("#line (", StringComparison.Ordinal),
            "the property declaration must carry a span #line pointing at the type name");
        var resolutionLine = Array.FindIndex(lines,
            l => l.Contains("Cache = (System.IServiceProvider)"));
        Assert.True(resolutionLine > 0 && lines[resolutionLine - 1].StartsWith("#line (", StringComparison.Ordinal),
            "the resolution statement must carry a span #line pointing at the @inject declaration");
    }

    [Fact]
    public void Generated_MultipleInjects_PropertiesAndResolutionsFollowDeclarationOrder()
    {
        var result = Run(
            "@namespace Demo\n@using EclipseUI.Elements\n" +
            "@inject System.IServiceProvider Cache\n@inject System.IServiceProvider Second\n<StackPanel />\n");

        Assert.Empty(result.Diagnostics);
        var code = Generated(result);

        Assert.Contains("public System.IServiceProvider Cache { get; private set; } = null!;", code);
        Assert.Contains("public System.IServiceProvider Second { get; private set; } = null!;", code);
        var first = code.IndexOf("Cache = (System.IServiceProvider)", StringComparison.Ordinal);
        var second = code.IndexOf("Second = (System.IServiceProvider)", StringComparison.Ordinal);
        Assert.True(0 <= first && first < second, "resolution statements must follow declaration order");
    }

    [Fact]
    public void Generated_TemplateView_InjectsIntoTemplateViewClass()
    {
        var result = Run(
            "@namespace Demo\n@using EclipseUI.Elements\n@template Button\n@inject System.IServiceProvider Cache\n<StackPanel />\n",
            "public partial class Other { }");

        Assert.Empty(result.Diagnostics);
        var code = Generated(result);

        Assert.Contains("__TemplateView", code);
        Assert.Contains("public System.IServiceProvider Cache { get; private set; } = null!;", code);
        Assert.Contains("Cache = (System.IServiceProvider)", code);
    }

    // ---- 生成代码真实编译（不只比较文本，spec/02 §10）----

    [Fact]
    public void Generated_CompilesClean_AgainstCodeBehind()
    {
        const string ui =
            "@namespace Demo\n@using EclipseUI.Elements\n@inject System.IServiceProvider Cache\n<StackPanel />\n";
        const string codeBehind = "namespace Demo;\npublic partial class Probe { }";
        var result = Run(ui, codeBehind);
        Assert.Empty(result.Diagnostics);

        var compilation = CSharpCompilation.Create("InjectCompileCheck",
            new[] { CSharpSyntaxTree.ParseText(Generated(result)), CSharpSyntaxTree.ParseText(codeBehind) },
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }
}
