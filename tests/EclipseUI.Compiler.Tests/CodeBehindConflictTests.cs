using System;
using System.Linq;
using System.Threading;
using EclipseUI.Compiler.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

/// <summary>
/// D57 / EUI045（spec/02 §10）：code-behind 与生成成员冲突的编译期出口。
/// 诊断落在 code-behind 源声明；生成器不改名、仍按契约输出（错误诊断本身阻断构建）。
/// </summary>
public class CodeBehindConflictTests
{
    private const string UiPath = "C:\\templates\\Probe.ui";
    private const string Ui = "@using EclipseUI.Elements\n<StackPanel><TextBlock>hi</TextBlock></StackPanel>";

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

    private static GeneratorRunResult Run(string codeBehind, string ui = Ui)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Component).Assembly.Location));

        // 带 path 的语法树：诊断位置必须能定位到 code-behind 行，而不是 .ui。
        var compilation = CSharpCompilation.Create(
            "TemplateProbe",
            new[] { CSharpSyntaxTree.ParseText(codeBehind, path: "Probe.cs") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiAdditionalText(UiPath, ui) });
        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    private static Diagnostic SingleConflict(GeneratorRunResult result) =>
        Assert.Single(result.Diagnostics, d => d.Id == "EUI045");

    [Theory]
    [InlineData("public partial class Probe { }")]
    [InlineData("public partial class Probe : global::EclipseUI.Component { }")]
    [InlineData("public partial class Probe : EclipseUI.Component { }")]
    [InlineData("public partial class Probe : Component { }")]
    public void NoConflict_ProducesNoDiagnostics(string codeBehind)
    {
        var result = Run(codeBehind);

        Assert.Empty(result.Diagnostics);
        Assert.Single(result.GeneratedSources);
    }

    [Theory]
    [InlineData("Build")]
    [InlineData("Render")]
    public void BuildOrRenderDeclaration_ReportsEui045(string name)
    {
        var result = Run($"public partial class Probe {{ private void {name}() {{ }} }}");

        var diag = SingleConflict(result);
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains(name, diag.GetMessage(), StringComparison.Ordinal);
        Assert.Single(result.GeneratedSources); // EUI045 不阻止生成器输出
    }

    [Theory]
    [InlineData("__el0")]
    [InlineData("__cmp1")]
    [InlineData("__blk2")]
    [InlineData("__run3")]
    [InlineData("__bind4")]
    [InlineData("U5")]
    [InlineData("R6")]
    public void GeneratedMemberName_ReportsEui045(string name)
    {
        var result = Run($"public partial class Probe {{ public int {name} = 0; }}");

        var diag = SingleConflict(result);
        Assert.Contains(name, diag.GetMessage(), StringComparison.Ordinal);
        Assert.Single(result.GeneratedSources);
    }

    [Theory]
    [InlineData("Invalidate")]
    [InlineData("Mount")]
    [InlineData("Track")]
    [InlineData("TrackContent")]
    [InlineData("AddDisposable")]
    public void HiddenComponentDependency_ReportsEui045(string name)
    {
        var result = Run($"public partial class Probe {{ public void {name}() {{ }} }}");

        var diag = SingleConflict(result);
        Assert.Contains(name, diag.GetMessage(), StringComparison.Ordinal);
        Assert.Single(result.GeneratedSources);
    }

    [Fact]
    public void NonComponentBase_ReportsEui045()
    {
        const string codeBehind = """
            public class SomeBase : EclipseUI.Component { }
            public partial class Probe : SomeBase { }
            """;

        var result = Run(codeBehind);

        var diag = SingleConflict(result);
        Assert.Contains("SomeBase", diag.GetMessage(), StringComparison.Ordinal);
        Assert.Equal(1, diag.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void ConflictLocation_IsCodeBehindDeclaration()
    {
        const string codeBehind = """
            public partial class Probe
            {
                private void Build() { }
            }
            """;

        var result = Run(codeBehind);

        var diag = SingleConflict(result);
        var line = diag.Location.GetLineSpan();
        Assert.Equal("Probe.cs", line.Path);
        Assert.Equal(2, line.StartLinePosition.Line);
    }

    [Fact]
    public void MultipleConflicts_AreAllReported()
    {
        const string codeBehind = """
            public partial class Probe
            {
                private void Build() { }
                public int __el0 = 0;
            }
            """;

        var result = Run(codeBehind);

        Assert.Equal(2, result.Diagnostics.Count(d => d.Id == "EUI045"));
        Assert.Single(result.GeneratedSources);
    }

    [Fact]
    public void OtherErrorsStillSuppressGeneratedSource()
    {
        var result = Run("public partial class Probe { private void Build() { } }", "@using EclipseUI.Elements\n<UnknownTag />");

        Assert.Contains(result.Diagnostics, d => d.Id == "EUI001");
        Assert.Contains(result.Diagnostics, d => d.Id == "EUI045");
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void DeprecatedCodes_AreNeverEmitted()
    {
        string[] deprecated =
        {
            "EUI014", "EUI032", "EUI034", "EUI035", "EUI036", "EUI038", "EUI039",
            "EUI041", "EUI042", "EUI044",
        };

        var result = Run("public partial class Probe { private void Build() { } }");

        Assert.Contains(result.Diagnostics, d => d.Id == "EUI045");
        Assert.DoesNotContain(result.Diagnostics, d => deprecated.Contains(d.Id));
    }
}
