using BubuUI.Compiler.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using BubuUI.Rendering;
using Button = BubuUI.Elements.Button;
using Xunit;

namespace BubuUI.Compiler.Tests;

public class LooklessTemplateTests
{
    private static System.Reflection.Assembly LoadCompiled(Compilation output)
    {
        using var bytes = new MemoryStream();
        var result = output.Emit(bytes);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return System.Reflection.Assembly.Load(bytes.ToArray());
    }

    [Fact]
    public void CompiledLegacyClassTransitionAnimatesOnHover()
    {
        const string sheet = """
            .animated {
                Background: #000000;
                @transition Background { Duration: 100ms; Easing: Linear; }
                &:hover { Background: #640000; }
            }
            """;
        var (result, output) = Compile(("transition.bss", sheet));
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var assembly = LoadCompiled(output);
        var registry = new StyleRegistry();
        assembly.GetTypes().Single(t => t.Name == "StyleBootstrap").GetMethod("Apply")!.Invoke(null, [registry]);
        var previous = Styling.Provider;
        try
        {
            Styling.Provider = registry;
            var button = new Button { Classes = ["animated"], Width = 100, Height = 40 };
            using var host = new HeadlessRoot(button);
            host.UpdateLayout(); host.PointerMove(new Point(10, 10));
            host.AdvanceAnimations(TimeSpan.FromMilliseconds(50));
            Assert.Equal(new Color(50, 0, 0), Visual.GetBackground(button));
        }
        finally { Styling.Provider = previous; }
    }

    [Fact]
    public void CompiledNewClassTransitionsFromExistingClassValue()
    {
        const string sheet = """
            .baseline { Background: #000000; }
            .target { Background: #640000; transition: Background 100ms Linear; }
            """;
        var (result, output) = Compile(("transition.bss", sheet));
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var assembly = LoadCompiled(output);
        var registry = new StyleRegistry();
        assembly.GetTypes().Single(t => t.Name == "StyleBootstrap").GetMethod("Apply")!.Invoke(null, [registry]);
        var previous = Styling.Provider;
        try
        {
            Styling.Provider = registry;
            var button = new Button { Classes = ["baseline"], Width = 100, Height = 40 };
            using var host = new HeadlessRoot(button);
            host.UpdateLayout();
            button.Classes = ["baseline", "target"];
            Assert.Equal(new Color(0, 0, 0), Visual.GetBackground(button));
            host.AdvanceAnimations(TimeSpan.FromMilliseconds(50));
            Assert.Equal(new Color(50, 0, 0), Visual.GetBackground(button));
        }
        finally { Styling.Provider = previous; }
    }

    [Fact]
    public void CompiledLegacyThemeResourceTransitionAnimatesOnHover()
    {
        const string sheet = """
            .animated {
                Background: theme(#000000, #000000);
                @transition Background { Duration: 100ms; Easing: Linear; }
                &:hover { Background: theme(#640000, #640000); }
            }
            """;
        var (result, output) = Compile(("transition.bss", sheet));
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var assembly = LoadCompiled(output);
        var registry = new StyleRegistry();
        assembly.GetTypes().Single(t => t.Name == "StyleBootstrap").GetMethod("Apply")!.Invoke(null, [registry]);
        var previous = Styling.Provider;
        try
        {
            Styling.Provider = registry;
            var button = new Button { Classes = ["animated"], Width = 100, Height = 40 };
            using var host = new HeadlessRoot(button);
            host.UpdateLayout(); host.PointerMove(new Point(10, 10));
            host.AdvanceAnimations(TimeSpan.FromMilliseconds(50));
            Assert.Equal(new ThemeColor(new Color(50, 0, 0), new Color(50, 0, 0)), Visual.GetBackgroundResource(button));
        }
        finally { Styling.Provider = previous; }
    }

    [Fact]
    public void CompiledControlThemeTransitionAnimatesOnHover()
    {
        var theme = Theme.Replace("Background: theme(#111111, #eeeeee);",
                "Background: #000000; @transition Background { Duration: 100ms; Easing: Linear; }")
            .Replace("&:hover /template/ Border.chrome { Background: #abcdef; }",
                "&:hover { Background: #640000; }");
        var (result, output) = Compile(("ButtonSkin.bui", Template), ("theme.bss", theme));
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var assembly = LoadCompiled(output);
        var controlTheme = (ControlTheme)assembly.GetTypes().Single(t => t.Name == "StyleBootstrap")
            .GetProperty("Rounded")!.GetValue(null)!;
        var button = new Button { Theme = controlTheme, Width = 100, Height = 40 };
        using var host = new HeadlessRoot(button);
        host.UpdateLayout();
        Assert.Equal(new UiColor(0, 0, 0), button.Background);
        host.PointerMove(new Point(10, 10));
        host.AdvanceAnimations(TimeSpan.FromMilliseconds(50));
        Assert.Equal(new UiColor(50, 0, 0), button.Background);
    }

    [Theory]
    [InlineData("@transition Background { Duration: 150ms; Easing: CubicOut; }")]
    [InlineData("transition: Background 150ms CubicOut;")]
    public void ThemeTransitionCompilesWithTypedProperty(string transition)
    {
        var theme = Theme.Replace("Background: theme(#111111, #eeeeee);", "Background: #111111; " + transition);
        var (result, output) = Compile(("ButtonSkin.bui", Template), ("theme.bss", theme));
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var generated = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
        Assert.Contains("ThemeSetter.Transition(global::BubuUI.Element.BackgroundProperty", generated);
    }

    [Fact]
    public void ThemeTransitionRejectsUnmarkedBusinessProperty()
    {
        var theme = Theme.Replace("Background: theme(#111111, #eeeeee);", "@transition IsEnabled { Duration: 150ms; }");
        var (result, _) = Compile(("ButtonSkin.bui", Template), ("theme.bss", theme));
        Assert.Contains(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

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
        @using BubuUI.Elements
        @template Button
        <Border Class="chrome" Background={Owner.Background}>
            <ContentPresenter Part="PART_ContentPresenter" Content={Owner.Content} Label={Owner.Label} />
        </Border>
        """;
    private const string Theme = """
        @using BubuUI.Elements
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
            .Replace("@using BubuUI.Elements", "@using BubuUI.Elements" + ending)
            .Replace("@template Button", "@template Button" + ending);
        var theme = Theme.Replace("@using BubuUI.Elements", "@using BubuUI.Elements" + ending)
            .Replace("@using Probe", "@using Probe" + ending);
        var consumer = """
            @namespace Probe
            @using BubuUI.Elements
            @using Btn = BubuUI.Elements.Button
            <Btn Class="primary">Save</Btn>
            """;
        consumer = consumer.Replace("@namespace Probe", "@namespace Probe" + ending)
            .Replace("@using BubuUI.Elements", "@using BubuUI.Elements" + ending)
            .Replace("@using Btn = BubuUI.Elements.Button", "@using Btn = BubuUI.Elements.Button" + ending);
        var (result, output) = Compile(("ButtonSkin.bui", template), ("theme.bss", theme), ("Page.bui", consumer));
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var factory = output.GetTypeByMetadataName("Probe.ButtonSkin");
        Assert.Equal("BubuUI.ControlTemplate<BubuUI.Elements.Button>", factory!.BaseType!.ToDisplayString());
        Assert.NotNull(output.GetTypeByMetadataName("Probe.ButtonSkin__TemplateView")!.GetMembers("Owner").Single());
    }

    [Theory]
    [InlineData("@template Window", "@template requires")]
    [InlineData("@template Missing", "@template requires")]
    [InlineData("@template Button\n@template Button", "once at document")]
    public void InvalidTemplateDeclarationsGiveSourceDiagnostics(string directive, string message)
    {
        var (result, _) = Compile(("ButtonSkin.bui", Template.Replace("@template Button", directive)));
        Assert.Contains(result.Diagnostics, d => d.GetMessage().Contains(message));
    }

    [Theory]
    [InlineData("Background: theme(#111111, #eeeeee);", "Background: wrong;", "expects")]
    [InlineData("Background: theme(#111111, #eeeeee);", "MissingProperty: 4;", "not a styled property")]
    [InlineData("template(ButtonSkin)", "template(MissingSkin)", "Unknown or ambiguous template")]
    [InlineData("resource(Rounded)", "resource(Missing)", "Unknown or incompatible")]
    [InlineData("@theme Rounded", "@theme class", "valid C# identifier")]
    [InlineData("@using BubuUI.Elements", "@using BubuUI.Elements;;", "accepts a namespace")]
    [InlineData("@using Probe", "@using", "requires a namespace")]
    public void InvalidThemeValuesAreReportedInTheStylesheet(string oldText, string newText, string message)
    {
        var (result, _) = Compile(("ButtonSkin.bui", Template), ("theme.bss", Theme.Replace(oldText, newText)));
        Assert.Contains(result.Diagnostics, d => d.Location.GetLineSpan().Path == "theme.bss" &&
            d.GetMessage().Contains(message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TemplateTargetMismatchIsRejectedBeforeEmittingTheme()
    {
        var (result, _) = Compile(("ButtonSkin.bui", Template.Replace("@template Button", "@template ToggleButton")), ("theme.bss", Theme));
        Assert.Contains(result.Diagnostics, d => d.GetMessage().Contains("incompatible"));
    }

    [Fact]
    public void QualifiedTemplateFromReferencedLibraryCompiles()
    {
        var (_, library) = Compile(("ButtonSkin.bui", Template));
        using var stream = new MemoryStream();
        var emitted = library.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var compilation = CSharpCompilation.Create("Consumer", references: library.References.Append(MetadataReference.CreateFromImage(stream.ToArray())),
            options: new(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new TemplateGenerator().AsSourceGenerator()],
            [new Input("theme.bss", Theme.Replace("template(ButtonSkin)", "template(Probe.ButtonSkin)"))]);
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
            "@using\tBubuUI.Elements" + ending, "", "// imports do not consume the theme below",
            "@using Probe" + ending, "@theme Rounded for Button { Template: template(ButtonSkin); }",
            "Button.primary { Theme: resource(Rounded); }");
        var errors = new List<UiDiagnostic>();
        var parsed = ControlThemeSyntax.Parse(text, errors);
        Assert.Empty(errors);
        Assert.Equal(new[] { "BubuUI.Elements", "Probe" }, parsed.Imports);
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
        var parsed = ControlThemeSyntax.Parse("@using BubuUI.Elements" + ending, errors);
        Assert.Empty(errors);
        Assert.Equal(new[] { "BubuUI.Elements" }, parsed.Imports);
        Assert.Empty(parsed.Nodes);
    }

    [Theory]
    [InlineData("@using BubuUI.Elements;;", "accepts a namespace")]
    [InlineData("@using", "requires a namespace")]
    [InlineData("@using BubuUI..Elements", "requires a namespace")]
    [InlineData("@using static BubuUI.Elements", "static")]
    [InlineData("@using (BubuUI.Elements)", "not supported")]
    [InlineData("@using BubuUI.Elements extra", "accepts a namespace")]
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
