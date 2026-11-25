using EclipseUI.Compiler;
using EclipseUI.Compiler.Metadata;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EclipseUI.Ide.Tests;

public class EditingCSharpCompletionTests
{
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Append(typeof(UiObject).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToArray());

    private static (DocumentAnalysis Analysis, ProjectSnapshot Snapshot, string Path) Create(string text, string source = "")
    {
        var directory = Path.Combine(Path.GetTempPath(), "eclipseui-editing-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "EditingProbe.ui");
        var compilation = CSharpCompilation.Create("EditingProbe",
            syntaxTrees: source.Length == 0 ? Array.Empty<SyntaxTree>() : new[] { CSharpSyntaxTree.ParseText(source) },
            references: References.Value,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var declarations = UiTemplateScanner.FromFiles(new[] { (path, text) });
        var catalog = UiCatalogFactory.Build(compilation, declarations);
        var snapshot = new ProjectSnapshot
        {
            Evaluation = new ProjectEvaluation
            {
                ProjectPath = Path.Combine(directory, "EditingProbe.csproj"),
                Directory = directory,
                AssemblyName = "EditingProbe",
                RootNamespace = "Demo",
                Nullable = "enable",
                LangVersion = "latest",
                OutputType = "Library",
                TargetFramework = "net11.0",
                CompileFiles = Array.Empty<string>(),
                AdditionalFiles = new[] { path },
                UsingNamespaces = Array.Empty<string>(),
                DefineConstants = Array.Empty<string>(),
                References = Array.Empty<string>(),
                Notes = Array.Empty<string>(),
            },
            Compilation = compilation,
            Catalog = catalog,
            GeneratedTrees = Array.Empty<SyntaxTree>(),
            GeneratedDocuments = new Dictionary<string, GeneratedDocument>(),
            Diagnostics = Array.Empty<EclipseUI.LanguageServer.Workspace.ProjectDiagnostic>(),
            Notes = Array.Empty<string>(),
        };
        var analysis = DocumentAnalysis.Create(text, Path.GetFileName(path), catalog, compilation,
            project: snapshot, documentPath: path);
        return (analysis, snapshot, path);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task InvalidMarkupDoesNotRemoveCSharpMemberCompletion(string newline)
    {
        var text = string.Join(newline,
            "@namespace Demo", "@using EclipseUI.Elements", "<StackPanel MissingProperty=\"1\" />",
            "@code {", "    string value = \"\";", "    void Tick() { value. }", "}");
        var (analysis, snapshot, path) = Create(text);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "EUI002");
        Assert.Null(analysis.GeneratedDocument);
        var editing = EditingCSharpDocument.For(analysis, path);

        Assert.NotNull(editing);
        var offset = text.IndexOf("value. }", StringComparison.Ordinal) + "value.".Length;
        Assert.True(editing!.Mappings.TryMapPositionToGenerated(offset, out var generatedOffset));
        var service = CompletionService.GetService(editing.Document);
        Assert.NotNull(service);
        var completions = await service!.GetCompletionsAsync(editing.Document, generatedOffset);
        Assert.NotNull(completions);
        Assert.Contains(completions!.ItemsList, item => item.DisplayText == "Length");
        var model = await editing.Document.GetSemanticModelAsync();
        Assert.NotNull(model);
        Assert.Equal("EclipseUI.Elements.StackPanel", model!.Compilation
            .GetTypeByMetadataName("Demo.EditingProbe")!.BaseType!.ToDisplayString());

        // Completion's recovery document must not turn failed build output into a success.
        Assert.Empty(snapshot.Compilation.SyntaxTrees);
        Assert.Empty(snapshot.GeneratedDocuments);
        Assert.Empty(snapshot.GeneratedTrees);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "EUI002");
        Assert.Same(editing, EditingCSharpDocument.For(analysis, path));
    }

    [Fact]
    public async Task InvalidControlTemplateRetainsTypedOwnerInCodeCompletion()
    {
        const string text = "@namespace Demo\n@using EclipseUI.Elements\n@template Button\n<Border MissingProperty=\"1\" />\n@code { void Tick() { Owner. } }";
        var (analysis, _, path) = Create(text);
        var editing = Assert.IsType<EditingCSharpDocument>(EditingCSharpDocument.For(analysis, path));
        var offset = text.IndexOf("Owner.", StringComparison.Ordinal) + "Owner.".Length;
        Assert.True(editing.Mappings.TryMapPositionToGenerated(offset, out var generatedOffset));
        var completions = await CompletionService.GetService(editing.Document)!.GetCompletionsAsync(editing.Document, generatedOffset);
        Assert.NotNull(completions);
        Assert.Contains(completions.ItemsList, item => item.DisplayText == "Background");
        Assert.Contains(completions.ItemsList, item => item.DisplayText == "Label");
    }

    [Fact]
    public async Task RepeatedCodeBlockTextHasDistinctExactMappings()
    {
        const string text = """
            @namespace Demo
            @using EclipseUI.Elements
            <StackPanel MissingProperty="1" />
            @code { public int value; }
            @code { public int value; }
            """;
        var (analysis, _, path) = Create(text);
        var editing = EditingCSharpDocument.For(analysis, path);
        Assert.NotNull(editing);
        var generated = (await editing!.Document.GetTextAsync()).ToString();
        var mappings = editing.Mappings.Mappings;
        Assert.Equal(2, mappings.Count);
        Assert.NotEqual(mappings[0].Original.Start, mappings[1].Original.Start);
        Assert.NotEqual(mappings[0].Generated.Start, mappings[1].Generated.Start);
        foreach (var mapping in mappings)
        {
            Assert.Equal(text.Substring(mapping.Original.Start, mapping.Original.Length),
                generated.Substring(mapping.Generated.Start, mapping.Generated.Length));
            Assert.True(editing.Mappings.TryMapToGenerated(mapping.Original, out var mapped));
            Assert.Equal(mapping.Generated, mapped);
            Assert.True(editing.Mappings.TryMapToOriginal(mapped, out var original));
            Assert.Equal(mapping.Original, original);
        }
    }

    [Fact]
    public void EmptyCodeBlockKeepsAnInsertionMapping()
    {
        const string text = "@namespace Demo\n@using EclipseUI.Elements\n<StackPanel MissingProperty=\"1\" />\n@code {}";
        var (analysis, _, path) = Create(text);
        var editing = EditingCSharpDocument.For(analysis, path);

        Assert.NotNull(editing);
        var block = Assert.Single(analysis.Ast!.Nodes.OfType<UiCodeBlock>());
        Assert.Equal(0, block.CodeSpan.Length);
        Assert.True(editing!.Mappings.TryMapPositionToGenerated(block.CodeSpan.Start, out var generated));
        Assert.True(editing.Mappings.TryMapToOriginal(new TextSpan(generated, 0), out var original));
        Assert.Equal(block.CodeSpan, original);
    }

    [Fact]
    public void UnknownRootDoesNotInventAReplacementBaseType()
    {
        const string text = "@namespace Demo\n<UnknownRoot />\n@code { public string value; }";
        var (analysis, snapshot, path) = Create(text);

        Assert.Null(EditingCSharpDocument.For(analysis, path));
        Assert.Empty(snapshot.Compilation.SyntaxTrees);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "EUI001");
    }

    [Theory]
    [InlineData("System.Exception", "")]
    [InlineData("ClosedControl", "namespace Demo { public sealed class ClosedControl : EclipseUI.Elements.StackPanel { } }")]
    [InlineData("StaticRoot", "namespace Demo { public static class StaticRoot { } }")]
    public void InvalidRootSemanticsDoNotProduceARecoveryDocument(string root, string source)
    {
        var text = "@namespace Demo\n<" + root + " />\n@code { public string value; }";
        var (analysis, snapshot, path) = Create(text, source);
        var original = snapshot.Compilation;

        Assert.Null(EditingCSharpDocument.For(analysis, path));
        Assert.Same(original, snapshot.Compilation);
        Assert.Empty(snapshot.GeneratedDocuments);
        Assert.Equal(source.Length == 0 ? 0 : 1, snapshot.Compilation.SyntaxTrees.Count());
    }

    [Fact]
    public async Task MissingNamespaceUsesProjectNamespaceOnlyForEditing()
    {
        const string text = "@using EclipseUI.Elements\n<StackPanel MissingProperty=\"1\" />\n@code { public string value; }";
        var (analysis, snapshot, path) = Create(text);
        var editing = EditingCSharpDocument.For(analysis, path);

        Assert.NotNull(editing);
        var model = await editing!.Document.GetSemanticModelAsync();
        Assert.NotNull(model!.Compilation.GetTypeByMetadataName("Demo.EditingProbe"));
        Assert.Null(analysis.Ast!.Namespace);
        Assert.Empty(snapshot.GeneratedDocuments);
        Assert.Empty(snapshot.Compilation.SyntaxTrees);
    }
}
