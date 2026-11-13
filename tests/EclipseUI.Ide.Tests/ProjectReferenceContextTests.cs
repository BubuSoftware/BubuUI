using EclipseUI.LanguageServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace EclipseUI.Ide.Tests;

public class ProjectReferenceContextTests
{
    private static readonly Lazy<ProjectSnapshot> Gallery = new(() =>
    {
        var workspace = new ProjectWorkspace(new CatalogOptions { ProbeRoot = RepositoryRoot() });
        return Assert.Single(workspace.ResolveAll(Path.Combine(RepositoryRoot(), "samples", "Gallery", "TaskList.ui")));
    });

    [Fact]
    public void ProjectCompilationReferencesEachAssemblySimpleNameOnce()
    {
        var compilation = Gallery.Value.Compilation;
        var assemblies = compilation.References.Select(reference =>
            (Reference: reference, Assembly: compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol))
            .Where(entry => entry.Assembly is not null).ToArray();
        Assert.NotEmpty(assemblies);
        var duplicates = assemblies.GroupBy(entry => entry.Assembly!.Identity.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).ToArray();

        Assert.True(duplicates.Length == 0, string.Join(Environment.NewLine, duplicates.Select(group =>
            group.Key + ": " + string.Join("; ", group.Select(entry =>
                entry.Assembly!.Identity + " [" + entry.Reference.Display + "]")))));
    }

    [Fact]
    public void ProjectCompileAssetsTakePrecedenceOverServerRuntimeAssemblies()
    {
        var snapshot = Gallery.Value;
        const string name = "Microsoft.Extensions.DependencyInjection.Abstractions";
        var expected = Assert.Single(snapshot.Evaluation.References, reference =>
            string.Equals(Path.GetFileNameWithoutExtension(reference), name, StringComparison.OrdinalIgnoreCase));
        var actual = Assert.Single(snapshot.Compilation.References, reference =>
            snapshot.Compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly
            && assembly.Identity.Name == name);

        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(actual.Display!), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void GalleryDependencyInjectionCallResolvesWithoutAmbiguousRuntimeAndCompileCopies()
    {
        var compilation = Gallery.Value.Compilation;
        var tree = Assert.Single(compilation.SyntaxTrees, candidate =>
            string.Equals(Path.GetFileName(candidate.FilePath), "GalleryApp.cs", StringComparison.OrdinalIgnoreCase));
        var invocation = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            candidate => candidate.Expression is MemberAccessExpressionSyntax member
                && member.Name.Identifier.ValueText == "GetRequiredService");
        var info = compilation.GetSemanticModel(tree).GetSymbolInfo(invocation);
        var method = Assert.IsAssignableFrom<IMethodSymbol>(info.Symbol);

        Assert.Empty(info.CandidateSymbols);
        Assert.Equal("GetRequiredService", method.Name);
        Assert.Equal("GalleryLaunchOptions", method.ReturnType.Name);
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Id == "CS0121");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "EclipseUI.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Cannot locate the repository root.");
    }
}
