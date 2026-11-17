using EclipseUI.Compiler.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

public class LooklessTemplateTests
{
    private sealed class Input(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
    private static (GeneratorRunResult Result, Compilation Output) Compile(params (string Path, string Text)[] files)
    {
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p)).ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(Element).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(Rendering.StyleRegistry).Assembly.Location));
        var compilation = CSharpCompilation.Create("LooklessProbe", references: refs,
            options: new(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new TemplateGenerator().AsSourceGenerator()],
            files.Select(f => new Input(f.Path, f.Text)));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var result = driver.GetRunResult().Results.Single();
        Assert.Null(result.Exception);
        return (result, output);
    }
    private const string Template = """
        @namespace Probe
        @using EclipseUI.Elements
        @template Button
        <Border Class="chrome" Background={Owner.Background}>
            <ContentPresenter Part="PART_ContentPresenter" Content={Owner.Content} Label={Owner.Label} />
        </Border>
        """;
    private const string Theme = """
        @using EclipseUI.Elements
        @using Probe
        @theme Rounded for Button {
            Template: template(ButtonSkin);
            Background: theme(#111111, #eeeeee);
            &:hover /template/ Border.chrome { Background: #abcdef; }
        }
        Button.primary { Theme: resource(Rounded); }
        """;

    [Theory]
    [InlineData("")]
    [InlineData(";")]
    [InlineData(" \t; \t")]
    public void TemplateThemeAndConsumerCompileTogether(string ending)
    {
        var template = Template.Replace("@namespace Probe", "@namespace Probe" + ending)
            .Replace("@using EclipseUI.Elements", "@using EclipseUI.Elements" + ending)
            .Replace("@template Button", "@template Button" + ending);
        var theme = Theme.Replace("@using EclipseUI.Elements", "@using EclipseUI.Elements" + ending)
            .Replace("@using Probe", "@using Probe" + ending);
        var consumer = """
            @namespace Probe
            @using EclipseUI.Elements
            @using Btn = EclipseUI.Elements.Button
            <Btn Class="primary">Save</Btn>
            """;
        consumer = consumer.Replace("@namespace Probe", "@namespace Probe" + ending)
            .Replace("@using EclipseUI.Elements", "@using EclipseUI.Elements" + ending)
            .Replace("@using Btn = EclipseUI.Elements.Button", "@using Btn = EclipseUI.Elements.Button" + ending);
        var (result, output) = Compile(("ButtonSkin.ui", template), ("theme.eustyle", theme), ("Page.ui", consumer));
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var factory = output.GetTypeByMetadataName("Probe.ButtonSkin");
        Assert.Equal("EclipseUI.ControlTemplate<EclipseUI.Elements.Button>", factory!.BaseType!.ToDisplayString());
        Assert.NotNull(output.GetTypeByMetadataName("Probe.ButtonSkin__TemplateView")!.GetMembers("Owner").Single());
    }

    [Theory]
    [InlineData("@template Window", "@template requires")]
    [InlineData("@template Missing", "@template requires")]
    [InlineData("@template Button\n@template Button", "once at document")]
    public void InvalidTemplateDeclarationsGiveSourceDiagnostics(string directive, string message)
    {
        var (result, _) = Compile(("ButtonSkin.ui", Template.Replace("@template Button", directive)));
        Assert.Contains(result.Diagnostics, d => d.GetMessage().Contains(message));
    }

    [Theory]
    [InlineData("Background: theme(#111111, #eeeeee);", "Background: wrong;", "expects")]
    [InlineData("Background: theme(#111111, #eeeeee);", "MissingProperty: 4;", "not a styled property")]
    [InlineData("template(ButtonSkin)", "template(MissingSkin)", "Unknown or ambiguous template")]
    [InlineData("resource(Rounded)", "resource(Missing)", "Unknown or incompatible")]
    [InlineData("@theme Rounded", "@theme class", "valid C# identifier")]
    [InlineData("@using EclipseUI.Elements", "@using EclipseUI.Elements;;", "accepts a namespace")]
    [InlineData("@using Probe", "@using", "requires a namespace")]
    public void InvalidThemeValuesAreReportedInTheStylesheet(string oldText, string newText, string message)
    {
        var (result, _) = Compile(("ButtonSkin.ui", Template), ("theme.eustyle", Theme.Replace(oldText, newText)));
        Assert.Contains(result.Diagnostics, d => d.Location.GetLineSpan().Path == "theme.eustyle" &&
            d.GetMessage().Contains(message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TemplateTargetMismatchIsRejectedBeforeEmittingTheme()
    {
        var (result, _) = Compile(("ButtonSkin.ui", Template.Replace("@template Button", "@template ToggleButton")), ("theme.eustyle", Theme));
        Assert.Contains(result.Diagnostics, d => d.GetMessage().Contains("incompatible"));
    }

    [Fact]
    public void QualifiedTemplateFromReferencedLibraryCompiles()
    {
        var (_, library) = Compile(("ButtonSkin.ui", Template));
        using var stream = new MemoryStream();
        var emitted = library.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var compilation = CSharpCompilation.Create("Consumer", references: library.References.Append(MetadataReference.CreateFromImage(stream.ToArray())),
            options: new(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new TemplateGenerator().AsSourceGenerator()],
            [new Input("theme.eustyle", Theme.Replace("template(ButtonSkin)", "template(Probe.ButtonSkin)"))]);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.DoesNotContain(diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void ThemeParserKeepsLegacySheetsUnchangedAndHandlesCommentBraces()
    {
        const string legacy = ".primary { Background: #abcdef; &:hover { Background: #111111; } }";
        var errors = new List<UiDiagnostic>();
        Assert.Equal(legacy, ControlThemeSyntax.Parse(legacy, errors).LegacyText);
        var parsed = ControlThemeSyntax.Parse("/* { } */\n" + Theme, errors);
        Assert.Empty(errors);
        Assert.Equal(2, parsed.Nodes.Count);
    }

    [Theory]
    [InlineData("\n", "")]
    [InlineData("\r\n", "")]
    [InlineData("\r", "")]
    [InlineData("\n", ";")]
    [InlineData("\r\n", " \t; \t")]
    public void ThemeImportsUseLineBoundariesAndPreserveFollowingRules(string newline, string ending)
    {
        var text = string.Join(newline,
            "@using\tEclipseUI.Elements" + ending, "", "// imports do not consume the theme below",
            "@using Probe" + ending, "@theme Rounded for Button { Template: template(ButtonSkin); }",
            "Button.primary { Theme: resource(Rounded); }");
        var errors = new List<UiDiagnostic>();
        var parsed = ControlThemeSyntax.Parse(text, errors);
        Assert.Empty(errors);
        Assert.Equal(new[] { "EclipseUI.Elements", "Probe" }, parsed.Imports);
        Assert.Equal(new[] { "@theme Rounded for Button", "Button.primary" }, parsed.Nodes.Select(n => n.Header));
        Assert.Equal(text.Length, parsed.LegacyText.Length);
        Assert.Equal(text.Where(c => c is '\r' or '\n'), parsed.LegacyText.Where(c => c is '\r' or '\n'));
    }

    [Theory]
    [InlineData("")]
    [InlineData(";")]
    [InlineData(" \t; \t")]
    public void ThemeImportCanEndAtEndOfFile(string ending)
    {
        var errors = new List<UiDiagnostic>();
        var parsed = ControlThemeSyntax.Parse("@using EclipseUI.Elements" + ending, errors);
        Assert.Empty(errors);
        Assert.Equal(new[] { "EclipseUI.Elements" }, parsed.Imports);
        Assert.Empty(parsed.Nodes);
    }

    [Theory]
    [InlineData("@using EclipseUI.Elements;;", "accepts a namespace")]
    [InlineData("@using", "requires a namespace")]
    [InlineData("@using EclipseUI..Elements", "requires a namespace")]
    [InlineData("@using static EclipseUI.Elements", "static")]
    [InlineData("@using (EclipseUI.Elements)", "not supported")]
    [InlineData("@using EclipseUI.Elements extra", "accepts a namespace")]
    public void InvalidThemeImportReportsItsLineAndPreservesTheNextRule(string directive, string message)
    {
        var errors = new List<UiDiagnostic>();
        var parsed = ControlThemeSyntax.Parse(directive + "\n@using Probe\nButton.primary { Background: #abcdef; }", errors);
        var error = Assert.Single(errors);
        Assert.Equal("EUI050", error.Code);
        Assert.Contains(message, error.Message);
        Assert.Equal(new TextSpan(0, directive.Length), error.Span);
        Assert.Equal(new[] { "Probe" }, parsed.Imports);
        Assert.Equal("Button.primary", Assert.Single(parsed.Nodes).Header);
    }
}
