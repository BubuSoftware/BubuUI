using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EclipseUI.Ide.Tests;

public class OverrideCompletionTests
{
    private static readonly Lazy<ProjectWorkspace> Projects = new(() =>
        new ProjectWorkspace(new CatalogOptions { ProbeRoot = RepositoryRoot() }));
    private static readonly Lazy<CatalogProvider> Legacy = new(() =>
        new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "EclipseUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        }));

    private sealed record Fixture(LanguageService Service, string Path, string Uri, string Text, int Offset);

    private static Fixture Create(string declaration, string root = "StackPanel", string baseCode = "",
        string otherMembers = "")
    {
        var projects = Projects.Value;
        var directory = Path.Combine(RepositoryRoot(), "tests", "GalleryFixtures");
        // The user's optional code-behind must not override a lifecycle member under test.
        // Buffer overlays never alter that file, and are harmless when it does not exist.
        projects.ApplyBuffer(DocumentStore.PathToUri(Path.Combine(directory, "Counter.cs")),
            "namespace Gallery; public partial class Counter { }");
        var modelPath = Path.Combine(directory, "TaskItem.cs");
        projects.ApplyBuffer(DocumentStore.PathToUri(modelPath), File.ReadAllText(modelPath) + "\n" + baseCode);
        var path = Path.Combine(directory, "Counter.ui");
        var uri = DocumentStore.PathToUri(path);
        var marked = "@namespace Gallery\n@using EclipseUI.Elements\n<" + root + " />\n@code {\n"
            + otherMembers + "    " + declaration + "|\n}\n";
        var offset = marked.IndexOf('|');
        var text = marked.Remove(offset, 1);
        var service = new LanguageService(Legacy.Value, new DocumentStore(), projects);
        service.ApplyBuffer(uri, text);
        service.Documents.Open(uri, text, 1);
        return new Fixture(service, path, uri, text, offset);
    }

    [Theory]
    [InlineData("override ")]
    [InlineData("protected override ")]
    [InlineData("protected override void ")]
    [InlineData("protected override void On")]
    public async Task LifecycleOverridesUseRoslynGeneratedMembersWithoutLeakingDirectives(string declaration)
    {
        var fixture = Create(declaration);
        var completions = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        foreach (var name in new[] { "OnInitialized", "OnAttached", "OnDetached", "OnUpdating", "OnUpdated", "OnDispose" })
        {
            var item = Assert.Single(completions.Items, candidate => candidate.Label.StartsWith(name + "(", StringComparison.Ordinal));
            Assert.Contains("protected override void " + name + "()", item.Text);
            Assert.Contains("base." + name + "();", item.Text);
            Assert.DoesNotContain("#line", item.Text);
            Assert.DoesNotContain("__eui", item.Text);
            Assert.NotNull(item.Replacement);
            Assert.NotNull(item.CaretOffset);
            Assert.InRange(item.CaretOffset!.Value, item.Text.IndexOf('{') + 1, item.Text.LastIndexOf('}'));
            Assert.StartsWith(declaration, item.FilterText, StringComparison.Ordinal);
            AssertCompiles(fixture, item);
        }
        Assert.DoesNotContain(completions.Items, item => item.Label.StartsWith("InitializeUi(", StringComparison.Ordinal));
        Assert.DoesNotContain(completions.Items, item => item.Label.StartsWith("Render(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExistingOverridesAndSealedPrivateOrStaticMembersAreNotOffered()
    {
        var fixture = Create("override ", "OverrideBase", """

            public class OverrideBase : EclipseUI.Elements.StackPanel
            {
                private void PrivateMethod() { }
                public static void StaticMethod() { }
                public sealed override string ToString() => "sealed";
                protected virtual int Available() => 1;
            }
            """, "    protected override void OnAttached() { base.OnAttached(); }\n");
        var completions = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        Assert.Contains(completions.Items, item => item.Label.StartsWith("Available(", StringComparison.Ordinal));
        foreach (var name in new[] { "OnAttached", "PrivateMethod", "StaticMethod", "ToString" })
            Assert.DoesNotContain(completions.Items, item => item.Label.StartsWith(name + "(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenericRefOutParametersAndBaseInvocationComeFromRoslyn()
    {
        var fixture = Create("protected override ", "OverrideBase", """

            public class OverrideBase : EclipseUI.Elements.StackPanel
            {
                protected virtual T Transform<T>(ref T value, out int count) where T : class
                { count = 1; return value; }
                public virtual string Label { get; set; } = "base";
            }
            """);
        var completions = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        var method = Assert.Single(completions.Items,
            item => item.Label.StartsWith("Transform<", StringComparison.Ordinal));
        Assert.Contains("ref T value", method.Text);
        Assert.Contains("out int count", method.Text);
        Assert.Contains("base.Transform", method.Text);
        Assert.Contains("ref value", method.Text);
        Assert.Contains("out count", method.Text);
        AssertCompiles(fixture, method);
    }

    [Fact]
    public async Task VirtualPropertyUsesRoslynAccessorBodies()
    {
        var fixture = Create("public override ", "OverrideBase", """

            public class OverrideBase : EclipseUI.Elements.StackPanel
            {
                public virtual string Label { get; set; } = "base";
            }
            """);
        var completions = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        var property = Assert.Single(completions.Items, item => item.Label == "Label");
        Assert.Contains("override string Label", property.Text);
        Assert.Contains("base.Label", property.Text);
        AssertCompiles(fixture, property);
    }

    [Fact]
    public async Task AbstractPropertyDoesNotGenerateAnIllegalBaseCall()
    {
        var fixture = Create("public override ", "OverrideBase", """

            public abstract class OverrideBase : EclipseUI.Elements.StackPanel
            {
                public abstract int Required { get; set; }
            }
            """);
        var completions = await fixture.Service.CompleteAsync(fixture.Uri, fixture.Offset);
        var property = Assert.Single(completions.Items, item => item.Label == "Required");
        Assert.Contains("override int Required", property.Text);
        Assert.DoesNotContain("base.Required", property.Text);
        // Abstract CLR roots are currently rejected as template tags. The recovery editor
        // still exposes their real virtual surface; validate the generated override against
        // that same source base in C#, rather than treating absent template output as success.
        var snapshot = Assert.Single(Projects.Value.ResolveAll(fixture.Path));
        var options = snapshot.Compilation.SyntaxTrees.First().Options as CSharpParseOptions;
        var tree = CSharpSyntaxTree.ParseText("using System; namespace Gallery; public class OverrideImplementation"
            + " : OverrideBase { " + property.Text + " }", options);
        var compilation = snapshot.Compilation.AddSyntaxTrees(tree);
        var errors = compilation.GetDiagnostics().Where(diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
            && ReferenceEquals(diagnostic.Location.SourceTree, tree)).ToArray();
        Assert.True(errors.Length == 0,
            string.Join(Environment.NewLine, errors.Select(diagnostic => diagnostic.ToString())));
        Assert.NotNull(compilation.GetTypeByMetadataName("Gallery.OverrideImplementation")?
            .GetMembers("Required").OfType<IPropertySymbol>().Single().OverriddenProperty);
    }

    private static void AssertCompiles(Fixture fixture, UiCompletionItem item)
    {
        var edits = new List<UiTextEdit>(item.AdditionalEdits ?? Array.Empty<UiTextEdit>())
        {
            new(item.Replacement!.Value, item.Text),
        };
        var text = fixture.Text;
        foreach (var edit in edits.OrderByDescending(edit => edit.Span.Start))
            text = text.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
        fixture.Service.ApplyBuffer(fixture.Uri, text);
        fixture.Service.Documents.Change(fixture.Uri, text, 2);
        var snapshot = Assert.Single(Projects.Value.ResolveAll(fixture.Path));
        Assert.True(snapshot.GeneratedDocuments.ContainsKey(Path.GetFullPath(fixture.Path)),
            "Applying the completion must produce the real template document, not only an error recovery view.");
        var errors = snapshot.Compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                && diagnostic.Location.IsInSource
                && (string.Equals(diagnostic.Location.GetMappedLineSpan().Path, fixture.Path,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(diagnostic.Location.SourceTree?.FilePath,
                        Path.Combine(Path.GetDirectoryName(fixture.Path)!, "TaskItem.cs"),
                        StringComparison.OrdinalIgnoreCase))).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(diagnostic => diagnostic.ToString())));
        // Each candidate is checked against the same unfinished original declaration.
        fixture.Service.ApplyBuffer(fixture.Uri, fixture.Text);
        fixture.Service.Documents.Change(fixture.Uri, fixture.Text, 3);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "EclipseUI.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Cannot locate the repository root.");
    }
}
