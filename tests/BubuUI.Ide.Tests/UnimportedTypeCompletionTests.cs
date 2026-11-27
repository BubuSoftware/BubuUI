using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using BubuUI.Compiler;
using BubuUI.Compiler.Metadata;
using BubuUI.LanguageServer.Analysis;
using BubuUI.LanguageServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BubuUI.Ide.Tests;

public class UnimportedTypeCompletionTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly string UiPath = Path.Combine(Path.GetTempPath(), "bubuui-import-completion", "ImportProbe.bui");
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat(new[]
            {
                typeof(Window).Assembly.Location,
                typeof(StringBuilder).Assembly.Location,
                typeof(HttpClient).Assembly.Location,
                typeof(ObservableCollection<>).Assembly.Location,
            })
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return paths.Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToArray();
    });

    private static readonly Lazy<MetadataReference> ExternalTypes = new(() =>
    {
        var compilation = CSharpCompilation.Create("ImportCompletionExternal", new[]
        {
            CSharpSyntaxTree.ParseText("""
                namespace External.Widgets
                {
                    public class AccessibleWidget { public int Value => 7; }
                    internal class InternalWidget { }
                    public class PublicContainer { private class PrivateWidget { } }
                }
                namespace External.One { public class SharedWidget { } }
                namespace External.Two { public class SharedWidget { } }
                """, ParseOptions),
        }, References.Value, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });

    private sealed record Fixture(DocumentAnalysis Analysis, Compilation Input, string Text, int Offset);

    private static Fixture Create(string members, string imports = "", string extraSource = "")
    {
        var marked = "@namespace ProbeApp\n@using BubuUI.Elements\n" + imports
            + "<StackPanel />\n@code {\n" + members + "\n}\n";
        var offset = marked.IndexOf('|');
        Assert.True(offset >= 0, "The fixture needs one cursor marker.");
        var text = marked.Remove(offset, 1);
        var source = CSharpSyntaxTree.ParseText(extraSource, ParseOptions, "Source.cs");
        var input = CSharpCompilation.Create("ImportCompletionProbe", new[] { source },
            References.Value.Append(ExternalTypes.Value),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var generated = Generate(input, text);
        var catalog = UiCatalogFactory.Build(input);
        Assert.True(generated.GeneratedDocuments.ContainsKey(UiPath), string.Join("\n", generated.Diagnostics));
        var project = new ProjectSnapshot
        {
            Evaluation = new ProjectEvaluation
            {
                ProjectPath = Path.Combine(Path.GetDirectoryName(UiPath)!, "ImportCompletionProbe.csproj"),
                Directory = Path.GetDirectoryName(UiPath)!,
                AssemblyName = "ImportCompletionProbe",
                RootNamespace = "ProbeApp",
                Nullable = "disable",
                LangVersion = "preview",
                OutputType = "Library",
                TargetFramework = "net11.0",
                CompileFiles = Array.Empty<string>(),
                AdditionalFiles = new[] { UiPath },
                UsingNamespaces = Array.Empty<string>(),
                DefineConstants = Array.Empty<string>(),
                References = Array.Empty<string>(),
                Notes = Array.Empty<string>(),
            },
            Compilation = generated.Compilation,
            Catalog = catalog,
            GeneratedTrees = generated.GeneratedTrees,
            GeneratedDocuments = generated.GeneratedDocuments,
            Diagnostics = Array.Empty<BubuUI.LanguageServer.Workspace.ProjectDiagnostic>(),
            Notes = Array.Empty<string>(),
        };
        var analysis = DocumentAnalysis.Create(text, Path.GetFileName(UiPath), catalog,
            generated.Compilation, project: project, documentPath: UiPath);
        return new Fixture(analysis, input, text, offset);
    }

    private static GeneratorResult Generate(Compilation input, string text)
        => GeneratorHost.Run(input, new[] { (UiPath, text) }, ParseOptions, "ProbeApp");

    private static Task<UiCompletionList> Complete(Fixture fixture)
        => EmbeddedCSharpCompletionProvider.CompleteAsync(fixture.Analysis, UiPath, fixture.Offset, null, CancellationToken.None);

    private static UiCompletionItem ImportedItem(UiCompletionList list, string label, string ns)
        => Assert.Single(list.Items, item => item.Label == label && item.AdditionalEdits is not null
            && item.AdditionalEdits.Any(edit => edit.NewText.Contains("@using " + ns + "\n", StringComparison.Ordinal)));

    private static string Apply(Fixture fixture, UiCompletionList list, UiCompletionItem item)
    {
        var edits = new List<UiTextEdit> { new(item.Replacement ?? list.Replacement, item.Text) };
        if (item.AdditionalEdits is not null) edits.AddRange(item.AdditionalEdits);
        var ordered = edits.OrderBy(static edit => edit.Span.Start).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            Assert.True(ordered[i - 1].Span.End <= ordered[i].Span.Start, "Completion edits must not overlap.");
        var text = fixture.Text;
        foreach (var edit in edits.OrderByDescending(static edit => edit.Span.Start))
            text = text[..edit.Span.Start] + edit.NewText + text[edit.Span.End..];
        return text;
    }

    private static void AssertCompiles(Fixture fixture, string text)
    {
        var rebuilt = Generate(fixture.Input, text);
        Assert.DoesNotContain(rebuilt.Diagnostics, diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var errors = rebuilt.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(static error => error.ToString())));
    }

    [Theory]
    [InlineData("void Probe() { StringBu| value = new(); _ = value.Length; }", "StringBuilder", "System.Text")]
    [InlineData("void Probe() { HttpC| value = new(); _ = value.Timeout; }", "HttpClient", "System.Net.Http")]
    [InlineData("void Probe() { ObservableCol|<int> values = new(); _ = values.Count; }", "ObservableCollection<>", "System.Collections.ObjectModel")]
    public async Task AcceptingAnUnimportedFrameworkTypeAddsTheImportAndCompiles(string members, string label, string ns)
    {
        var fixture = Create(members);
        var list = await Complete(fixture);
        var item = ImportedItem(list, label, ns);

        Assert.Single(item.AdditionalEdits!);
        var applied = Apply(fixture, list, item);
        Assert.Contains("@using " + ns + "\n", applied);
        AssertCompiles(fixture, applied);
    }

    [Fact]
    public async Task CompletingInTheMiddleReplacesTheExistingIdentifierSuffix()
    {
        var fixture = Create("void Probe() { Stri|ngBuilder value = new(); _ = value.Length; }");
        var list = await Complete(fixture);
        var item = ImportedItem(list, "StringBuilder", "System.Text");
        Assert.Equal("StringBuilder", fixture.Text.Substring(item.Replacement!.Value.Start, item.Replacement.Value.Length));

        var applied = Apply(fixture, list, item);

        Assert.DoesNotContain("StringBuilderngBuilder", applied);
        AssertCompiles(fixture, applied);
    }

    [Fact]
    public async Task AnAlreadyImportedTypeDoesNotAddASecondImport()
    {
        var fixture = Create("void Probe() { StringBu| value = new(); _ = value.Length; }", "@using System.Text\n");
        var list = await Complete(fixture);
        var item = Assert.Single(list.Items, candidate => candidate.Label == "StringBuilder");

        Assert.True(item.AdditionalEdits is null || item.AdditionalEdits.Count == 0);
        var applied = Apply(fixture, list, item);
        Assert.Equal(1, applied.Split("@using System.Text", StringSplitOptions.None).Length - 1);
        AssertCompiles(fixture, applied);
    }

    [Theory]
    [InlineData("", "namespace ProbeApp { public class StringBuilder { } }")]
    [InlineData("@using Existing.Types\n", "namespace Existing.Types { public class StringBuilder { } }")]
    [InlineData("@using StringBuilder = ProbeApp.Existing\n", "namespace ProbeApp { public class Existing : BubuUI.Elements.StackPanel { } }")]
    public async Task AVisibleTypeOrAliasNameUsesAQualifiedAlternativeWithoutAConflictingImport(string imports, string source)
    {
        var fixture = Create("void Probe() { StringBu| value = new(); _ = value; }", imports, source);
        var list = await Complete(fixture);

        Assert.DoesNotContain(list.Items, item => item.Label == "StringBuilder" && item.AdditionalEdits is { Count: > 0 });
        var candidates = list.Items.Where(item => item.Label == "StringBuilder").ToArray();
        Assert.Contains(candidates, item => item.Text == "StringBuilder");
        Assert.Contains(candidates, item => item.Text == "global::System.Text.StringBuilder");
        foreach (var candidate in candidates) AssertCompiles(fixture, Apply(fixture, list, candidate));
    }

    [Fact]
    public async Task ImportingATypeDoesNotMakeAnotherPreviouslyBoundTypeAmbiguous()
    {
        var fixture = Create("Payload _payload = new();\nvoid Probe() { Clie| value = new(); _ = value; _ = _payload; }",
            "@using ExistingNamespace\n", """
            namespace ExistingNamespace { public class Payload { } }
            namespace IncomingNamespace { public class Client { } public class Payload { } }
            """);
        var list = await Complete(fixture);
        var candidate = Assert.Single(list.Items, item => item.Label == "Client"
            && item.Text == "global::IncomingNamespace.Client");

        Assert.True(candidate.AdditionalEdits is null || candidate.AdditionalEdits.Count == 0);
        AssertCompiles(fixture, Apply(fixture, list, candidate));
    }

    [Fact]
    public async Task SameNamedUnimportedTypesRemainSeparateAndEachImportCompiles()
    {
        var fixture = Create("void Probe() { SharedWid| value = new(); _ = value; }");
        var list = await Complete(fixture);
        var first = ImportedItem(list, "SharedWidget", "External.One");
        var second = ImportedItem(list, "SharedWidget", "External.Two");

        Assert.NotEqual(first.Detail, second.Detail);
        AssertCompiles(fixture, Apply(fixture, list, first));
        AssertCompiles(fixture, Apply(fixture, list, second));
    }

    [Theory]
    [InlineData("void Probe() { object value = new(); value.StringBu|; }")]
    [InlineData("void Probe() { System.Text.StringBu| value = new(); }")]
    [InlineData("void Probe() { // StringBu|\n }")]
    [InlineData("void Probe() { /* StringBu| */ }")]
    [InlineData("void Probe() { var text = \"StringBu|\"; }")]
    [InlineData("void Probe() { var text = $\"StringBu|\"; }")]
    public async Task MemberAccessCommentsAndStringTextDoNotOfferUnimportedTypes(string members)
    {
        var fixture = Create(members);
        var list = await Complete(fixture);

        Assert.DoesNotContain(list.Items, item => item.AdditionalEdits is { Count: > 0 });
    }

    [Theory]
    [InlineData("InternalWid|")]
    [InlineData("PrivateWid|")]
    public async Task InaccessibleTypesFromReferencedAssembliesAreNotOffered(string prefix)
    {
        var fixture = Create("void Probe() { " + prefix + " value = new(); }");
        var list = await Complete(fixture);

        Assert.DoesNotContain(list.Items, item => item.Label is "InternalWidget" or "PrivateWidget");
    }

    [Fact]
    public async Task AccessibleReferencedTypesCanBeImportedAndCompiled()
    {
        var fixture = Create("void Probe() { AccessibleWid| value = new(); _ = value.Value; }");
        var list = await Complete(fixture);
        var item = ImportedItem(list, "AccessibleWidget", "External.Widgets");

        AssertCompiles(fixture, Apply(fixture, list, item));
    }

    [Fact]
    public async Task InternalTypesInTheCurrentAssemblyRemainAccessible()
    {
        var fixture = Create("void Probe() { SourceInternalWid| value = new(); _ = value; }",
            extraSource: "namespace Internal.Source { internal class SourceInternalWidget { } }");
        var list = await Complete(fixture);
        var item = ImportedItem(list, "SourceInternalWidget", "Internal.Source");

        AssertCompiles(fixture, Apply(fixture, list, item));
    }
}
