using BubuUI.Compiler;
using BubuUI.Compiler.Metadata;
using BubuUI.LanguageServer.Analysis;
using BubuUI.LanguageServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using UiSpan = BubuUI.Compiler.TextSpan;

namespace BubuUI.Ide.Tests;

public class DirectiveImportCompletionTests
{
    private static readonly Lazy<Compilation> NamespaceCompilation = new(() =>
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(Window).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("DirectiveImports", new[]
        {
            CSharpSyntaxTree.ParseText("""
                namespace Plain.Models { public class Options { } }
                namespace Plain.Models
                {
                    public class Widget : BubuUI.Elements.StackPanel { }
                    public abstract class AbstractWidget : BubuUI.Elements.StackPanel { }
                    public class GenericWidget<T> : BubuUI.Elements.StackPanel { }
                    [BubuUI.UiIgnore] public class IgnoredWidget : BubuUI.Elements.StackPanel { }
                }
                namespace Plain.Services.Transport { public class Channel { } }
                namespace Plain.Models
                {
                    public class Container
                    {
                        private class Hidden : BubuUI.Elements.StackPanel { }
                        public class Visible : BubuUI.Elements.StackPanel { }
                    }
                }
                """),
        }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    });

    private static readonly Lazy<UiCatalog> DirectiveCatalog = new(() => UiCatalogFactory.Build(NamespaceCompilation.Value));

    private static (DocumentAnalysis Analysis, int Offset) Analyze(string marked)
    {
        var offset = marked.IndexOf('|');
        Assert.True(offset >= 0);
        var text = marked.Remove(offset, 1);
        var analysis = DocumentAnalysis.Create(text, "DirectiveImports.bui", DirectiveCatalog.Value,
            NamespaceCompilation.Value);
        return (analysis, offset);
    }

    private static UiCompletionItem Item(UiCompletionList list, string label)
        => Assert.Single(list.Items, item => item.Label == label);

    private static string Apply(string text, UiCompletionList list, UiCompletionItem item)
    {
        var changes = new List<UiTextEdit> { new(item.Replacement ?? list.Replacement, item.Text) };
        if (item.AdditionalEdits is not null) changes.AddRange(item.AdditionalEdits);
        foreach (var edit in changes.OrderByDescending(static edit => edit.Span.Start))
            text = text[..edit.Span.Start] + edit.NewText + text[edit.Span.End..];
        return text;
    }

    [Fact]
    public void EmptyUsingArgumentOffersFrameworkAndNonControlNamespaces()
    {
        var (analysis, offset) = Analyze("@using |\n<UnknownTag />");
        var list = CompletionProvider.Complete(analysis, DirectiveCatalog.Value, offset);

        foreach (var name in new[] { "System", "System.Collections", "System.Threading.Tasks", "Plain.Models", "Plain.Services.Transport" })
            Assert.Equal(UiCompletionKind.Namespace, Item(list, name).Kind);
        Assert.Equal(new UiSpan(offset, 0), list.Replacement);
    }

    [Theory]
    [InlineData("@using System.Col|", "System.Collections", "@using System.Collections")]
    [InlineData("@using System.Col|lections", "System.Collections", "@using System.Collections")]
    [InlineData("@using System.Collections.|", "System.Collections.Generic", "@using System.Collections.Generic")]
    [InlineData("@using Plain.Serv|", "Plain.Services.Transport", "@using Plain.Services.Transport")]
    [InlineData("@using global::System.Col|", "global::System.Collections", "@using System.Collections")]
    public void NamespaceCompletionReplacesTheWholeQualifiedTarget(string marked, string label, string expected)
    {
        var (analysis, offset) = Analyze(marked);
        var list = CompletionProvider.Complete(analysis, DirectiveCatalog.Value, offset);

        Assert.Equal(expected, Apply(analysis.Text, list, Item(list, label)));
    }

    [Theory]
    [InlineData("@us|")]
    [InlineData("@using|")]
    public void AnIncompleteUsingKeywordRemainsADirective(string marked)
    {
        var (analysis, offset) = Analyze(marked);
        var context = CursorScanner.Scan(analysis.Text, offset);
        var list = CompletionProvider.Complete(analysis, DirectiveCatalog.Value, offset);

        Assert.Equal(CursorKind.Directive, context.Kind);
        Assert.Equal(0, list.Replacement.Start);
        Assert.Equal(UiCompletionKind.Directive, Item(list, "@using").Kind);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void EmptyArgumentBeforeAnotherLineDoesNotConsumeThatLine(string newline)
    {
        var (analysis, offset) = Analyze("@using \t|" + newline + "@namespace Demo" + newline + "<UnknownTag />");
        var list = CompletionProvider.Complete(analysis, DirectiveCatalog.Value, offset);

        Assert.Equal(new UiSpan(offset, 0), list.Replacement);
        Assert.Equal("@using \tSystem.Threading.Tasks" + newline + "@namespace Demo" + newline + "<UnknownTag />",
            Apply(analysis.Text, list, Item(list, "System.Threading.Tasks")));
    }

    [Theory]
    [InlineData("@using Control = BubuUI.Elements.But|", "BubuUI.Elements.Button", "@using Control = BubuUI.Elements.Button")]
    [InlineData("@using UI = BubuUI.Ele|", "BubuUI.Elements", "@using UI = BubuUI.Elements")]
    [InlineData("@using Value=Plain.Models.Wid|", "Plain.Models.Widget", "@using Value=Plain.Models.Widget")]
    [InlineData("@using Shell=BubuUI.Win|", "BubuUI.Window", "@using Shell=BubuUI.Window")]
    public void AliasTargetPathCompletionPreservesTheDeclaration(string marked, string label, string expected)
    {
        var (analysis, offset) = Analyze(marked);
        var context = CursorScanner.Scan(analysis.Text, offset);
        var list = CompletionProvider.Complete(analysis, DirectiveCatalog.Value, offset);

        Assert.True(context.IsUsingAliasTarget);
        Assert.Equal(expected, Apply(analysis.Text, list, Item(list, label)));
    }

    [Fact]
    public void NestedUiTypesAreNotOfferedAsTemplateAliasTargets()
    {
        var (analysis, offset) = Analyze("@using Item = Plain.Models.Container.|");
        var list = CompletionProvider.Complete(analysis, DirectiveCatalog.Value, offset);

        Assert.DoesNotContain(list.Items, item => item.Label == "Plain.Models.Container.Visible");
        Assert.DoesNotContain(list.Items, item => item.Label.EndsWith(".Hidden", StringComparison.Ordinal));
    }

    [Fact]
    public void AliasTargetsCanUseTypesFromPreviouslyImportedNamespaces()
    {
        var (analysis, offset) = Analyze("@using Plain.Models\n@using Item = Wid|");
        var list = CompletionProvider.Complete(analysis, DirectiveCatalog.Value, offset);

        Assert.Equal("@using Plain.Models\n@using Item = Plain.Models.Widget",
            Apply(analysis.Text, list, Item(list, "Plain.Models.Widget")));
    }

    [Fact]
    public void AliasNamesAreNotReplacedByNamespaceCandidates()
    {
        var (analysis, offset) = Analyze("@using Bui|lder = System.Text.StringBuilder");

        Assert.Empty(CompletionProvider.Complete(analysis, DirectiveCatalog.Value, offset).Items);
    }

    [Fact]
    public void AliasTargetsExcludeNonUiAndNonExposedUiTypes()
    {
        var (analysis, offset) = Analyze("@using Item = Plain.Models.|");
        var list = CompletionProvider.Complete(analysis, DirectiveCatalog.Value, offset);

        Assert.Contains(list.Items, item => item.Label == "Plain.Models.Widget");
        Assert.DoesNotContain(list.Items, item => item.Label is "Plain.Models.Options"
            or "Plain.Models.GenericWidget<T>" or "Plain.Models.AbstractWidget" or "Plain.Models.IgnoredWidget");
        var (framework, frameworkOffset) = Analyze("@using Item = System.Text.Str|");
        Assert.DoesNotContain(CompletionProvider.Complete(framework, DirectiveCatalog.Value, frameworkOffset).Items,
            item => item.Label == "System.Text.StringBuilder");
    }

    [Fact]
    public void AcceptedUiAliasTargetCompilesWithoutEui048()
    {
        var (analysis, offset) = Analyze("@namespace Demo\n@using Selected = Plain.Models.Wid|\n<Selected />");
        var list = CompletionProvider.Complete(analysis, DirectiveCatalog.Value, offset);
        var accepted = Apply(analysis.Text, list, Item(list, "Plain.Models.Widget"));
        var path = Path.Combine(Path.GetTempPath(), "bubuui-directive-imports", "DirectiveImports.bui");
        var parseOptions = Assert.IsType<CSharpParseOptions>(NamespaceCompilation.Value.SyntaxTrees.First().Options);
        var generated = GeneratorHost.Run(NamespaceCompilation.Value,
            new[] { (path, accepted) }, parseOptions, "Demo");

        Assert.DoesNotContain(generated.Diagnostics, diagnostic => diagnostic.Id == UiDiagnostics.UsingAliasInvalid);
        var errors = generated.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(static diagnostic => diagnostic.ToString())));
    }

    [Theory]
    [InlineData("@namespace Demo\n@using global::System.Te|\n@using BubuUI.Elements\n<StackPanel />\n@code { public StringBuilder Value = new(); }",
        "global::System.Text")]
    [InlineData("@namespace global::Plain.Mod|\n@using BubuUI.Elements\n<StackPanel />", "global::Plain.Models")]
    public void AcceptingGlobalQualifiedNamespaceInputCommitsValidTemplateSyntax(string marked, string label)
    {
        var (analysis, offset) = Analyze(marked);
        var list = CompletionProvider.Complete(analysis, DirectiveCatalog.Value, offset);
        var item = Item(list, label);
        var accepted = Apply(analysis.Text, list, item);

        Assert.Equal(label, item.FilterText);
        Assert.DoesNotContain("@using global::", accepted);
        Assert.DoesNotContain("@namespace global::", accepted);
        var path = Path.Combine(Path.GetTempPath(), "bubuui-directive-imports", "DirectiveImports.bui");
        var parseOptions = Assert.IsType<CSharpParseOptions>(NamespaceCompilation.Value.SyntaxTrees.First().Options);
        var generated = GeneratorHost.Run(NamespaceCompilation.Value, new[] { (path, accepted) }, parseOptions, "Demo");
        Assert.DoesNotContain(generated.Diagnostics, diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.True(generated.GeneratedDocuments.ContainsKey(path));
        var errors = generated.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(static diagnostic => diagnostic.ToString())));
    }

    [Theory]
    [InlineData("")]
    [InlineData(";")]
    [InlineData(" \t; \t")]
    public void GlobalQualifiedImportRequestsDoNotDuplicateAnExistingNamespaceImport(string ending)
    {
        var (analysis, _) = Analyze("@namespace Demo" + ending + "\n@using System.Text" + ending + "\n@using BubuUI.Elements" + ending + "\n<StackPanel />|");

        Assert.Null(UiImportService.CreateImportEdit(analysis, "global::System.Text"));
        Assert.Null(UiImportService.CreateImportEdit(analysis, "global::BubuUI.Elements"));
    }

    [Fact]
    public void AGlobalQualifiedTypeAliasDoesNotReplaceANamespaceImport()
    {
        var (analysis, _) = Analyze("@namespace Demo\n@using Selected = global::Plain.Models.Widget\n<Selected />|");
        var edit = UiImportService.CreateImportEdit(analysis, "global::Plain.Models");

        Assert.NotNull(edit);
        Assert.Equal("@using Plain.Models\n", edit!.NewText);
    }

    [Fact]
    public void SelectingAnUnimportedTagAddsItsNamespaceAlongsideTheTagEdit()
    {
        var (service, uri, offset) = TestHost.Open("@namespace Demo\n<But|");
        var list = service.Complete(uri, offset);
        var item = Item(list, "Button");
        Assert.True(service.Documents.TryGet(uri, out var document));

        Assert.Single(item.AdditionalEdits!);
        Assert.Equal("@namespace Demo\n@using BubuUI.Elements\n<Button", Apply(document.Text, list, item));
    }

    [Theory]
    [InlineData("@namespace Demo\n@using BubuUI.Elements\n<But|")]
    [InlineData("@namespace BubuUI.Elements.Demo\n<But|")]
    public void VisibleTagNamespacesDoNotProduceDuplicateImports(string marked)
    {
        var (service, uri, offset) = TestHost.Open(marked);
        var item = Item(service.Complete(uri, offset), "Button");

        Assert.Null(item.AdditionalEdits);
    }

    [Fact]
    public async Task ProjectNamespaceCompletionSurvivesAnUnrelatedUnknownTag()
    {
        var root = FindRepositoryRoot();
        var projects = new ProjectWorkspace(new CatalogOptions { ProbeRoot = root });
        var catalogs = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "BubuUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        });
        var service = new LanguageService(catalogs, new DocumentStore(), projects);
        var path = Path.Combine(root, "tests", "GalleryFixtures", "TaskList.bui");
        var uri = DocumentStore.PathToUri(path);
        const string marked = "@namespace Gallery\n@using System.Threading.Ta|\n<NotARealTag />\n@code { public int Value; }";
        var offset = marked.IndexOf('|');
        var text = marked.Remove(offset, 1);
        service.ApplyBuffer(uri, text);
        service.Documents.Open(uri, text, 1);
        try
        {
            var list = await service.CompleteAsync(uri, offset);

            Assert.Equal(UiCompletionKind.Namespace, Item(list, "System.Threading.Tasks").Kind);
            Assert.Contains(service.GetDiagnostics(uri), diagnostic => diagnostic.Code == "EUI001");
        }
        finally { service.Forget(uri); }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubuUI.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
