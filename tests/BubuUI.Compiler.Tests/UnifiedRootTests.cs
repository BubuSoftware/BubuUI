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

public class UnifiedRootTests
{
    private sealed class UiFile(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private static CSharpCompilation Compile(string source = "", IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Element).Assembly.Location));
        if (additionalReferences is not null) references.AddRange(additionalReferences);
        return CSharpCompilation.Create("UnifiedRootProbe",
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static (GeneratorRunResult Result, Compilation Output) Generate(string source,
        params (string Name, string Text)[] files)
        => GenerateWithReferences(source, Array.Empty<MetadataReference>(), files);

    private static (GeneratorRunResult Result, Compilation Output) GenerateWithReferences(string source,
        IEnumerable<MetadataReference> references, params (string Name, string Text)[] files)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            files.Select(f => (AdditionalText)new UiFile(f.Name + ".bui", f.Text)).ToArray());
        driver = driver.RunGeneratorsAndUpdateCompilation(Compile(source, references), out var output, out _);
        return (driver.GetRunResult().Results.Single(), output);
    }

    [Theory]
    [InlineData("__euiConstruction")]
    [InlineData("__euiConstruction_scope1")]
    [InlineData("__euiCustom")]
    public void ForeachItemWithGeneratedScopePrefixHasATemplateDiagnostic(string itemName)
    {
        var (result, output) = Generate("", ("Card", """
            @namespace Demo
            @using BubuUI.Elements
            <StackPanel>
                @foreach (var ITEM in Items) {
                    <TextBlock>{ITEM}</TextBlock>
                }
            </StackPanel>
            @code { public string[] Items = ["one"]; }
            """.Replace("ITEM", itemName)));

        Assert.Null(result.Exception);
        Assert.Contains(result.Diagnostics, d => d.Id == UiDiagnostics.CodeBehindConflict
            && d.GetMessage().Contains("reserved '__eui' prefix"));
        Assert.Empty(result.GeneratedSources);
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Id == "CS0136");
    }

    [Fact]
    public void OrdinaryForeachItemDoesNotConflictWithGeneratedConstructionScopes()
    {
        var (result, output) = Generate("", ("Card", """
            @namespace Demo
            @using BubuUI.Elements
            <StackPanel>
                @foreach (var item in Items) {
                    <TextBlock>{item}</TextBlock>
                }
            </StackPanel>
            @code { public string[] Items = ["one"]; }
            """));

        Assert.Null(result.Exception);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Single(result.GeneratedSources);
    }

    [Fact]
    public void RootTypeIsIdentity_AndGeneratedOutputCompiles()
    {
        var (result, output) = Generate("", ("Card", """
            @namespace Demo
            @using BubuUI.Elements
            <StackPanel Gap="8"><Button>OK</Button></StackPanel>
            """));
        Assert.Null(result.Exception);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var code = Assert.Single(result.GeneratedSources).SourceText.ToString();
        Assert.Contains("class Card : global::BubuUI.Elements.StackPanel", code);
        Assert.Contains("InitializeUi()", code);
        Assert.DoesNotContain("new global::BubuUI.Elements.StackPanel", code);
        Assert.DoesNotContain("global::BubuUI.Component", code);
    }

    [Fact]
    public void WindowUsesOrdinaryRootInheritance_AndIsNotEmbeddable()
    {
        var files = new[]
        {
            ("Shell", "@namespace Demo\n@using BubuUI.Elements\n<Window Title=\"Hello\"><StackPanel/></Window>"),
            ("Host", "@namespace Demo\n@using BubuUI.Elements\n<StackPanel><Shell/></StackPanel>"),
        };
        var declarations = files.Select(f => UiTemplateScanner.FromFile(f.Item1 + ".bui", f.Item2)!).ToArray();
        var catalog = UiCatalogFactory.Build(Compile(), declarations);
        var shell = catalog.FindByTypeDisplayName("global::Demo.Shell");
        Assert.NotNull(shell);
        Assert.False(shell.IsEmbeddable);
        Assert.Equal("global::BubuUI.Window", shell.BaseTypeDisplayName);
        var (result, _) = Generate("", files);
        Assert.Contains(result.Diagnostics, d => d.Id == UiDiagnostics.TemplateRootMisuse);
        var code = result.GeneratedSources.Single(s => s.HintName.Contains("Shell")).SourceText.ToString();
        Assert.Contains("class Shell : global::BubuUI.Window", code);
        Assert.DoesNotContain("WindowComponent", code);
    }

    [Fact]
    public void TemplateAndCodeRootsShareInheritedPropertiesAndSlots()
    {
        const string code = """
            namespace Demo;
            public class CardBase : BubuUI.Elements.StackPanel
            {
                public string Caption { get; set; } = "";
                [BubuUI.UiSlot("Body")] public BubuUI.Element? Body { get; set; }
            }
            """;
        var (result, output) = Generate(code,
            ("Card", """
                @namespace Demo
                @using BubuUI.Elements
                <CardBase Gap="4"><ContentSlot Name="Body"/></CardBase>
                @code { public int Count { get; set; } }
                """),
            ("Host", """
                @namespace Demo
                @using BubuUI.Elements
                <StackPanel>
                    <Card Caption="Hello" Count="2" Gap="8">
                        <Slot Name="Body"><Button>OK</Button></Slot>
                    </Card>
                </StackPanel>
                """));
        Assert.Null(result.Exception);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void SealedRootAndMismatchedCodeBehindAreRejected()
    {
        var (result, _) = Generate("""
            namespace Demo;
            public sealed class SealedPanel : BubuUI.Elements.StackPanel { }
            public partial class Card : BubuUI.Elements.WrapPanel { }
            """,
            ("Bad", "@namespace Demo\n<SealedPanel/>"),
            ("Card", "@namespace Demo\n@using BubuUI.Elements\n<StackPanel/>"));
        Assert.Contains(result.Diagnostics, d => d.Id == UiDiagnostics.TemplateBaseInvalid);
        Assert.Contains(result.Diagnostics, d => d.Id == UiDiagnostics.CodeBehindConflict);
    }

    [Fact]
    public void AliasedCodeRootAndTemplateBaseUseInheritedExpressionScope()
    {
        var (result, output) = Generate("""
            namespace Demo;
            public class CodePanel : BubuUI.Elements.StackPanel
            {
                public string Caption { get; set; } = "base";
            }
            """,
            ("BaseView", """
                @namespace Demo
                @using P = Demo.CodePanel
                @using BubuUI.Elements
                <P><TextBlock>{Caption}</TextBlock></P>
                """),
            ("DerivedView", """
                @namespace Demo
                @using BubuUI.Elements
                <BaseView Caption={CaptionText}><TextBlock>{Caption}</TextBlock></BaseView>
                @code { public string CaptionText = "derived"; }
                """));
        Assert.Null(result.Exception);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains(result.GeneratedSources,
            s => s.SourceText.ToString().Contains("class DerivedView : global::Demo.BaseView"));
    }

    [Fact]
    public void PureTemplateCanInferForeachFromItsCodeRootBase()
    {
        var (result, output) = Generate("""
            namespace Demo;
            public class CodePanel : BubuUI.Elements.StackPanel
            {
                public string[] Items { get; } = ["one"];
            }
            """,
            ("Card", """
                @namespace Demo
                @using BubuUI.Elements
                <CodePanel>
                    @foreach (var item in Items) {
                        <TextBlock>{item.Length}</TextBlock>
                    }
                </CodePanel>
                """));
        Assert.Null(result.Exception);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("ForEachBlock<string>", Assert.Single(result.GeneratedSources).SourceText.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootInheritanceCyclesHaveDeterministicDiagnostics(bool mutual)
    {
        var files = mutual
            ? new[] { ("A", "@namespace Demo\n<B/>"), ("B", "@namespace Demo\n<A/>") }
            : new[] { ("A", "@namespace Demo\n<A/>") };
        var (first, _) = Generate("", files);
        var (second, _) = Generate("", files);
        Assert.Null(first.Exception);
        Assert.Null(second.Exception);
        Assert.Contains(first.Diagnostics, d => d.Id == UiDiagnostics.TemplateBaseInvalid
            && d.GetMessage().Contains("cycle"));
        Assert.Equal(first.Diagnostics.Select(d => d.ToString()), second.Diagnostics.Select(d => d.ToString()));
        Assert.Empty(first.GeneratedSources);
    }

    [Fact]
    public void NonWindowSingleContentRootUsesDeclaredMemberAndRejectsEmbedding()
    {
        const string source = """
            namespace Demo;
            [BubuUI.UiContent(BubuUI.UiContentKind.Children, Target = nameof(Body))]
            public class Surface : BubuUI.UiObject
            {
                public BubuUI.Element? Body { get => SingleContent; set => SingleContent = value; }
            }
            """;
        var (result, output) = Generate(source,
            ("SurfaceView", """
                @namespace Demo
                @using BubuUI.Elements
                <Surface><Button>OK</Button></Surface>
                """));
        Assert.Null(result.Exception);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var code = Assert.Single(result.GeneratedSources).SourceText.ToString();
        Assert.Contains("this.Body =", code);
        Assert.DoesNotContain("this.Children", code);
        var (invalid, _) = Generate(source,
            ("Host", "@namespace Demo\n@using BubuUI.Elements\n<StackPanel><Surface/></StackPanel>"));
        Assert.Contains(invalid.Diagnostics, d => d.Id == UiDiagnostics.TemplateRootMisuse);
    }

    [Fact]
    public void CustomCollectionContentUsesDeclaredTarget()
    {
        var (result, output) = Generate("""
            namespace Demo;
            [BubuUI.UiContent(BubuUI.UiContentKind.Children, Target = nameof(Items))]
            public class ItemPanel : BubuUI.Elements.StackPanel
            {
                public BubuUI.ElementCollection Items => Children;
            }
            """,
            ("ItemView", """
                @namespace Demo
                @using BubuUI.Elements
                <ItemPanel><Button>OK</Button></ItemPanel>
                """));
        Assert.Null(result.Exception);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("this.Items.Add(", Assert.Single(result.GeneratedSources).SourceText.ToString());
    }

    [Fact]
    public void RootInReferencedAssemblyProvidesPropertiesAndExpressionScope()
    {
        using var image = new System.IO.MemoryStream();
        var library = Compile("""
            namespace Referenced;
            public class SharedPanel : BubuUI.Elements.StackPanel
            {
                public string Caption { get; set; } = "shared";
            }
            """).WithAssemblyName("RootTypes");
        var emitted = library.Emit(image);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var (result, output) = GenerateWithReferences("", new[] { MetadataReference.CreateFromImage(image.ToArray()) },
            ("Card", """
                @namespace Demo
                @using Referenced
                @using BubuUI.Elements
                <SharedPanel Caption="hello"><TextBlock>{Caption}</TextBlock></SharedPanel>
                """));
        Assert.Null(result.Exception);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("class Card : global::Referenced.SharedPanel", Assert.Single(result.GeneratedSources).SourceText.ToString());
    }
}
