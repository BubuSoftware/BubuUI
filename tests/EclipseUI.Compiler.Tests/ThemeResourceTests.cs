using EclipseUI.Compiler.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

public class ThemeResourceTests
{
    private const string Resources = """
        using EclipseUI;
        namespace Probe {
            public class Palette {
                public static readonly ThemeResource<UiColor> ControlBackground = new(e => e.IsLightTheme ? new(1, 2, 3) : new(4, 5, 6));
                public static readonly ThemeResource<double> Padding = new(e => e.IsLightTheme ? 12d : 10d);
                public static readonly ThemeResource<string> Caption = new(e => e.IsLightTheme ? "Light" : "Dark");
                private static readonly ThemeResource<UiColor> Hidden = new(e => new(1, 2, 3));
                public readonly ThemeResource<UiColor> Instance = new(e => new(1, 2, 3));
                public static readonly UiColor Raw = new(1, 2, 3);
                public static ThemeResource<UiColor> Property => ControlBackground;
                public static class Nested {
                    public static readonly ThemeResource<UiColor> Color = new(e => new(7, 8, 9));
                }
            }
        }
        """;
    private sealed class Input(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
    private static IEnumerable<MetadataReference> References()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path)).ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Element).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Rendering.StyleRegistry).Assembly.Location));
        return references;
    }
    private static CSharpCompilation Source(string code, IEnumerable<MetadataReference>? references = null) =>
        CSharpCompilation.Create("ResourceProbe" + Guid.NewGuid().ToString("N"),
            string.IsNullOrEmpty(code) ? [] : [CSharpSyntaxTree.ParseText(code)], references ?? References(),
            new(OutputKind.DynamicallyLinkedLibrary));
    private static (GeneratorRunResult Result, Compilation Output) Compile(string style, string source = Resources,
        IEnumerable<MetadataReference>? references = null, string? template = null)
    {
        var files = new List<AdditionalText> { new Input("resources.eustyle", style) };
        if (template is not null) files.Add(new Input("ButtonSkin.ui", template));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new TemplateGenerator().AsSourceGenerator()], files);
        driver = driver.RunGeneratorsAndUpdateCompilation(Source(source, references), out var output, out _);
        var result = Assert.Single(driver.GetRunResult().Results);
        Assert.Null(result.Exception);
        return (result, output);
    }
    [Theory]
    [InlineData("Palette.ControlBackground", "@using Probe")]
    [InlineData("Probe.Palette.ControlBackground", "")]
    [InlineData("Palette.Nested.Color", "@using Probe")]
    [InlineData("Probe.Palette.Nested.Color", "")]
    public void ImportedQualifiedAndNestedResourceFieldsCompileToDirectTypedCalls(string name, string import)
    {
        var (result, output) = Compile($"@using EclipseUI.Elements\n{import}\nButton {{ Background: resource({name}); }}");
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var generated = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
        Assert.Contains("ThemeSetter.Dynamic", generated);
        Assert.Contains(name.Contains("Nested") ? "global::Probe.Palette.Nested.Color.Resolve" : "global::Probe.Palette.ControlBackground.Resolve", generated);
    }
    [Fact]
    public void DifferentPropertyTypesUseTheirOwnTypedResources()
    {
        var (result, output) = Compile("""
            @using EclipseUI.Elements
            @using Probe
            Button { Background: resource(Palette.ControlBackground); HorizontalPadding: resource(Palette.Padding); Label: resource(Palette.Caption); }
            """);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }
    [Fact]
    public void UntypedClassSelectorsUseCommonElementPropertiesWithoutAnElementImport()
    {
        var (result, output) = Compile("""
            @using EclipseUI.Elements
            @using Probe
            .primary:hover { Background: resource(Palette.ControlBackground); CornerRadius: theme(2, 4); }
            .primary /template/ Border { Background: resource(Palette.ControlBackground); }
            """);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var generated = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
        Assert.Contains("e is global::EclipseUI.Element", generated);
        Assert.Contains("global::EclipseUI.Element.BackgroundProperty", generated);
    }
    [Fact]
    public void UntypedSelectorsDoNotExposePropertiesSpecificToADerivedControl()
    {
        var (result, _) = Compile("""
            @using Probe
            .primary:hover { HorizontalPadding: resource(Palette.Padding); }
            """);
        var diagnostic = Assert.Single(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Equal("EUI050", diagnostic.Id);
        Assert.Contains("not a styled property on Element", diagnostic.GetMessage());
    }
    [Theory]
    [InlineData("Palette.Padding", "requires")]
    [InlineData("Palette.Raw", "public static ThemeResource<T>")]
    [InlineData("Palette.Property", "public static ThemeResource<T>")]
    [InlineData("Palette.Hidden", "public static ThemeResource<T>")]
    [InlineData("Palette.Instance", "public static ThemeResource<T>")]
    [InlineData("Palette.Missing", "public static ThemeResource<T>")]
    [InlineData("Missing.Color", "Unknown or ambiguous")]
    public void InvalidResourceFieldsProduceStylesheetDiagnostics(string name, string expected)
    {
        var (result, _) = Compile($"@using EclipseUI.Elements\n@using Probe\nButton {{ Background: resource({name}); }}");
        var diagnostic = Assert.Single(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Equal("EUI050", diagnostic.Id);
        Assert.Contains(expected, diagnostic.GetMessage());
        Assert.EndsWith("resources.eustyle", diagnostic.Location.GetLineSpan().Path);
    }
    [Fact]
    public void AmbiguousImportedResourceTypesAreRejected()
    {
        var source = Resources + "\nnamespace Other { public class Palette { public static readonly EclipseUI.ThemeResource<EclipseUI.UiColor> ControlBackground = new(e => default); } }";
        var (result, _) = Compile("@using EclipseUI.Elements\n@using Probe\n@using Other\nButton { Background: resource(Palette.ControlBackground); }", source);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.GetMessage().Contains("Unknown or ambiguous"));
    }
    [Fact]
    public void ReferencedAssemblyResourcesAreResolvedWithoutExecutingTheirInitializers()
    {
        var source = Resources.Replace("new(e => e.IsLightTheme ? new(1, 2, 3) : new(4, 5, 6))", "Throw()")
            .Replace("public class Palette {", "public class Palette { private static ThemeResource<UiColor> Throw() => throw new System.Exception(\"must not execute\");");
        using var stream = new MemoryStream();
        var emitted = Source(source).Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var references = References().Append(MetadataReference.CreateFromImage(stream.ToArray()));
        var (result, output) = Compile("@using EclipseUI.Elements\n@using Probe\nButton { Background: resource(Palette.ControlBackground); }", "", references);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }
    [Fact]
    public void SharedResourcesWorkInsideThemesAlongsideExistingControlThemeResources()
    {
        const string template = """
            @namespace Probe
            @using EclipseUI.Elements
            @template Button
            <Border Background={Owner.Background} />
            """;
        var (result, output) = Compile("""
            @using EclipseUI.Elements
            @using Probe
            @theme Rounded for Button {
                Template: template(ButtonSkin);
                Background: resource(Palette.ControlBackground);
                &:hover /template/ Border { Background: resource(Palette.ControlBackground); }
            }
            Button { Theme: resource(Rounded); }
            """, template: template);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var generated = string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
        Assert.Contains("ThemeSetter.Value(global::EclipseUI.Element.ThemeProperty, Rounded)", generated);
    }
}
