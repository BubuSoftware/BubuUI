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

public class GeneratorContractTests
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

    private static GeneratorRunResult Run(string uiPath, string ui, string? codeBehind = null)
    {
        var compilation = CSharpCompilation.Create(
            "TemplateProbe",
            new[]
            {
                CSharpSyntaxTree.ParseText(codeBehind ??
                    "public partial class Probe { public int gap = 4; public string message = \"start\"; }"),
            },
            references: null,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiAdditionalText(uiPath, ui) });

        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    private static string Generated(GeneratorRunResult result)
    {
        Assert.Null(result.Exception);
        return result.GeneratedSources.Single().SourceText.ToString();
    }

    [Fact]
    public void Generation_IsDeterministic_WithStableHint()
    {
        const string ui = """
            <StackPanel Gap={gap}>
                <TextBlock>{message}</TextBlock>
            </StackPanel>
            """;

        var first = Run("C:\\templates\\Probe.ui", ui);
        var second = Run("C:\\templates\\Probe.ui", ui);

        var firstSource = first.GeneratedSources.Single();
        var secondSource = second.GeneratedSources.Single();
        Assert.Matches("^Probe\\.[0-9a-f]{16}\\.g\\.cs$", firstSource.HintName);
        Assert.Equal(firstSource.HintName, secondSource.HintName);
        Assert.Equal(firstSource.SourceText.ToString(), secondSource.SourceText.ToString());
    }

    [Fact]
    public void RootDynamic_PromotesFields_AndEmitsRenderWithLineMapping()
    {
        const string ui = """
            <StackPanel Gap={gap}>
                <TextBlock>{message}</TextBlock>
            </StackPanel>
            """;

        var code = Generated(Run("C:\\templates\\Probe.ui", ui));

        Assert.Contains("private global::EclipseUI.Elements.StackPanel __el0 = null!;", code);
        Assert.Contains("private global::EclipseUI.Elements.TextRun __run2 = null!;", code);
        Assert.Contains("protected override void Render()", code);
        Assert.Contains("private void U0()", code);
        Assert.Contains("private void U1()", code);
        Assert.Contains("#line 1 \"C:\\templates\\Probe.ui\"", code);
        Assert.Contains("#line 2 \"C:\\templates\\Probe.ui\"", code);
        Assert.Contains("__el0.Gap = gap;", code);
        Assert.Contains("__run2.Text = $\"{(message)}\";", code);
    }

    [Fact]
    public void TypeError_InDynamicBinding_MapsBackToUiLine()
    {
        const string uiPath = "C:\\templates\\Probe.ui";
        const string ui = """
            <StackPanel Gap={gap}>
                <TextBlock>{message}</TextBlock>
            </StackPanel>
            """;
        const string codeBehind = """
            public partial class Probe : global::EclipseUI.Component
            {
                public string gap = "oops";
                public string message = "start";
            }
            """;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Component).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "TemplateProbe",
            new[] { CSharpSyntaxTree.ParseText(codeBehind) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiAdditionalText(uiPath, ui) });
        var updatedDriver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var sources = updatedDriver.GetRunResult().Results.Single().GeneratedSources;
        Assert.Single(sources);

        var errors = output.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToList();
        var details = string.Join(Environment.NewLine,
            errors.Select(e => $"{e.Id} {e.Location.GetMappedLineSpan()} :: {e.GetMessage()}"));
        var typeError = Assert.Single(errors, d => d.Id == "CS0029");
        var lineSpan = typeError.Location.GetMappedLineSpan();
        Assert.Equal(uiPath, lineSpan.Path);
        Assert.Equal(0, lineSpan.StartLinePosition.Line);
    }

    [Fact]
    public void ControlEvent_EmitsHandlerLocal_AndInvalidateWrapper()
    {
        const string ui = "<StackPanel><Button OnClick={() => Bump()}>go</Button></StackPanel>";
        const string codeBehind = "public partial class Probe { private void Bump() { } }";

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, codeBehind));

        Assert.Contains("var h2 = (global::System.Action)(() => Bump());", code);
        Assert.Contains(
            "AddHandler(global::EclipseUI.Elements.Button.ClickEvent, () => { h2(); Invalidate(); });",
            code);
        Assert.DoesNotContain("Track(", code);
        Assert.DoesNotContain(".Subscribe(", code);
    }
}
