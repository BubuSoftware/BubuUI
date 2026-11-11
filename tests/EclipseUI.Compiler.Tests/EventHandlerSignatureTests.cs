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
/// EUI024 契约（spec/02 §4.6.1 规则 7、§2.4 规则 8；spec/01 §6.2）。
/// 关键边界：只有「可静态判定」的签名不匹配才由生成器报在 .ui 属性值上；
/// 符号未解析、表达式内部调用失败、dynamic 等情形必须静默，让生成代码经 #line 由 C# 报错。
/// 否则会与 C# 诊断重复，并因 Error 短路吞掉生成输出。
/// </summary>
public class EventHandlerSignatureTests
{
    private const string UiPath = "C:\\templates\\Probe.ui";

    private const string ControlCodeBehind = """
        public partial class Probe
        {
            private void Bump() { }
            private void Say() { }
            private void Handle() { }
            private void HandleInt(int n) { }
            private void HandleArgs(global::EclipseUI.RoutedEventArgs e) { }
        }
        """;

    private const string ComponentFixtures = """
        using System;
        using EclipseUI;

        public partial class Probe : Component
        {
            public int gap = 4;
            public void Close() { }
            private void HandleInt(int n) { }
            [UiParameter(Required = true)] public int Count { get; set; }
            [UiParameter] public Action? Closed { get; set; }
        }
        """;

    private sealed class UiAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public UiAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(NamespaceDeclarationFixture.Ensure(path, content));
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }

    private static GeneratorRunResult Run(string ui, string codeBehind)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(global::EclipseUI.Component).Assembly.Location));

        var fixture = NamespaceDeclarationFixture.Prepare(UiPath, codeBehind);

        var compilation = CSharpCompilation.Create(
            "TemplateProbe",
            new[] { CSharpSyntaxTree.ParseText(fixture.CodeBehind, path: "Probe.cs") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiAdditionalText(UiPath, fixture.Ui(ui)) });
        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    private static string Generated(GeneratorRunResult result)
    {
        Assert.Null(result.Exception);
        return result.GeneratedSources.Single().SourceText.ToString();
    }

    // ---- 可静态判定：方法组签名不匹配 → EUI024 ----

    [Fact]
    public void MethodGroup_WrongSignature_ReportsEUI024()
    {
        var result = Run("@using EclipseUI.Elements\n<StackPanel><Button OnClick={HandleInt} /></StackPanel>", ControlCodeBehind);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI024");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("global::System.Action", diag.GetMessage());
        Assert.Empty(result.GeneratedSources); // Error 短路，与 EUI004 同口径
    }

    [Fact]
    public void MethodGroup_SignatureIsReportedAtTheUiValueSpan()
    {
        const string ui = "@using EclipseUI.Elements\n<StackPanel><Button OnClick={HandleArgs} /></StackPanel>";
        var result = Run(ui, ControlCodeBehind);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI024");
        var line = diag.Location.GetLineSpan();
        Assert.Equal(UiPath, line.Path);
        Assert.Equal(1, line.StartLinePosition.Line);   // 第 0 行是 @using 前缀（K22 迁移）
        // 列 = 绝对偏移减去 @using 前缀行
        Assert.Equal(ui.IndexOf("HandleArgs", StringComparison.Ordinal) - "@using EclipseUI.Elements\n".Length,
            line.StartLinePosition.Character);
    }

    [Fact]
    public void ComponentDelegateParameter_WrongSignature_ReportsEUI024()
    {
        var result = Run("@using EclipseUI.Elements\n<Probe Count={gap} Closed={HandleInt} />", ComponentFixtures);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI024");
        Assert.Contains("System.Action", diag.GetMessage());
        Assert.Empty(result.GeneratedSources);
    }

    // ---- 合法处理器：零诊断、源码照常生成 ----

    [Fact]
    public void MethodGroup_CorrectSignature_GeneratesHandlerLocal()
    {
        var result = Run("@using EclipseUI.Elements\n<StackPanel><Button OnClick={Handle} /></StackPanel>", ControlCodeBehind);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI024");
        Assert.Contains("(global::System.Action)(Handle)", Generated(result));
    }

    [Fact]
    public void ComponentDelegateParameter_CorrectSignature_GeneratesHandlerLocal()
    {
        var result = Run("@using EclipseUI.Elements\n<Probe Count={gap} Closed={Close} />", ComponentFixtures);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI024");
        Assert.Contains("(global::System.Action)", Generated(result));
    }

    [Theory]
    [InlineData("@using EclipseUI.Elements\n<StackPanel><Button OnClick={() => Bump()} /></StackPanel>")]
    [InlineData("@using EclipseUI.Elements\n<StackPanel><Button OnClick={(async () => Say())} /></StackPanel>")]
    [InlineData("@using EclipseUI.Elements\n<StackPanel><Button OnClick={(e => Say())} /></StackPanel>")]
    [InlineData("@using EclipseUI.Elements\n<StackPanel><Button OnClick={delegate { Say(); }} /></StackPanel>")]
    [InlineData("@using EclipseUI.Elements\n<StackPanel><Button OnClick={new global::System.Action(() => Say())} /></StackPanel>")]
    public void LambdaAndAnonymousForms_AreNotMistakenForSignatureMismatch(string ui)
    {
        var result = Run(ui, ControlCodeBehind);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI024");
        Assert.NotEmpty(result.GeneratedSources); // tokenizer 四个形态测试不能被短路吞掉
    }

    // ---- 静态不可判定：必须静默，交回 C# 经 #line ----

    [Fact]
    public void UnresolvedSymbol_DoesNotReportEUI024()
    {
        var result = Run("@using EclipseUI.Elements\n<StackPanel><Button OnClick={Missing} /></StackPanel>", ControlCodeBehind);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI024");
        Assert.NotEmpty(result.GeneratedSources); // C# 的 CS0103 仍由生成代码报告
    }

    [Fact]
    public void ExpressionInternalCallFailure_DoesNotReportEUI024()
    {
        var result = Run("@using EclipseUI.Elements\n<StackPanel><Button OnClick={Say(1)} /></StackPanel>", ControlCodeBehind);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI024");
        Assert.NotEmpty(result.GeneratedSources); // CS1501 由 C# 经 #line 报告
    }

    [Fact]
    public void ReferencedAssemblyBrokenEvent_StillOnlyReportsEUI020()
    {
        // 目录侧使用点诊断已短路，签名探针不得再补一条 EUI024。
        var lib = EmitLibrary("ProbeBrokenEvent", """
            using EclipseUI;
            namespace ProbeLib;
            public class FaultyBox : global::EclipseUI.Elements.StackPanel
            {
                public static readonly StyledProperty<int> ClickEvent = StyledProperty<int>.Register<FaultyBox>("Click", 0);
            }
            """);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(global::EclipseUI.Component).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(lib));
        var compilation = CSharpCompilation.Create("TemplateProbe",
            new[] { CSharpSyntaxTree.ParseText(ControlCodeBehind, path: "Probe.cs") }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiAdditionalText(UiPath, "@using ProbeLib\n@using EclipseUI.Elements\n<FaultyBox OnClick={Handle} />") });
        var result = driver.RunGenerators(compilation).GetRunResult().Results.Single();

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI024");
        Assert.Contains(result.Diagnostics, d => d.Id == "EUI020");
        Assert.Empty(result.GeneratedSources);
    }

    // ---- 与其它事件诊断互斥 ----

    [Fact]
    public void LiteralEvent_ReportsOnlyEUI004()
    {
        var result = Run("@using EclipseUI.Elements\n<StackPanel><Button OnClick=\"Handle\" /></StackPanel>", ControlCodeBehind);

        Assert.Contains(result.Diagnostics, d => d.Id == "EUI004");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI024");
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void BindOnEvent_ReportsOnlyEUI040()
    {
        var result = Run("@using EclipseUI.Elements\n<StackPanel><Button @bind-OnClick={() => flag}>x</Button></StackPanel>",
            "public partial class Probe { public bool flag; }");

        Assert.Contains(result.Diagnostics, d => d.Id == "EUI040");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI024");
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void CleanUi_ReportsNoDiagnosticsAndNoRetiredCodes()
    {
        var result = Run("@using EclipseUI.Elements\n<StackPanel><Button OnClick={() => Bump()}>go</Button></StackPanel>", ControlCodeBehind);

        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Id is "EUI014" or "EUI032" or "EUI034" or "EUI035" or "EUI036"
                or "EUI038" or "EUI039" or "EUI041" or "EUI042" or "EUI044");
        Assert.NotEmpty(result.GeneratedSources);
    }

    private static string EmitLibrary(string name, string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(global::EclipseUI.Component).Assembly.Location));
        var compilation = CSharpCompilation.Create(name,
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N") + ".dll");
        var emit = compilation.Emit(path);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        return path;
    }
}
