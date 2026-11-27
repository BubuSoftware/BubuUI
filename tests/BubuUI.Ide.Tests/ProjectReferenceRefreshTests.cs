using BubuUI.LanguageServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BubuUI.Ide.Tests;

public sealed class ProjectReferenceRefreshTests
{
    [Fact]
    public void BuildingRebuildingAndCleaningDependencyRefreshesOpenProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "bubuui-reference-refresh", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "App", "obj"));
        Directory.CreateDirectory(Path.Combine(root, "Dependency"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Dependency", "Dependency.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "App", "App.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Dependency/Dependency.csproj" />
                    <AdditionalFiles Include="View.bui" />
                  </ItemGroup>
                </Project>
                """);
            // Supply the restore contract without needing a network restore or a build:
            // this reproduces opening the editor before the first dependency output exists.
            File.WriteAllText(Path.Combine(root, "App", "obj", "project.assets.json"), """
                {
                  "targets": { "net11.0": { "Dependency/1.0.0": { "type": "project" } } },
                  "libraries": { "Dependency/1.0.0": { "msbuildProject": "../Dependency/Dependency.csproj" } },
                  "packageFolders": {}
                }
                """);
            var document = Path.Combine(root, "App", "View.bui");
            File.WriteAllText(document, "@namespace App\n");
            var output = Path.Combine(root, "Dependency", "bin", "Debug", "net11.0", "Dependency.dll");
            var workspace = new ProjectWorkspace(new CatalogOptions { ProbeRoot = root });
            ProjectSnapshot Snapshot() => Assert.Single(workspace.ResolveAll(document));

            var initial = Snapshot();
            Assert.Null(initial.Compilation.GetTypeByMetadataName("Dependency.FirstControl"));
            Assert.Contains(initial.Notes, note => note.Contains("is not built", StringComparison.Ordinal));

            Emit(output, "FirstControl");
            workspace.Invalidate(new[] { output });
            var built = Snapshot();
            Assert.Contains(output, built.Evaluation.References);
            Assert.NotNull(built.Compilation.GetTypeByMetadataName("Dependency.FirstControl"));
            Assert.DoesNotContain(built.Notes, note => note.Contains("is not built", StringComparison.Ordinal));

            Emit(output, "UpdatedControl");
            workspace.Invalidate(new[] { output });
            var rebuilt = Snapshot();
            Assert.Null(rebuilt.Compilation.GetTypeByMetadataName("Dependency.FirstControl"));
            Assert.NotNull(rebuilt.Compilation.GetTypeByMetadataName("Dependency.UpdatedControl"));

            File.Delete(output);
            workspace.Invalidate(new[] { output });
            var cleaned = Snapshot();
            Assert.Null(cleaned.Compilation.GetTypeByMetadataName("Dependency.UpdatedControl"));
            Assert.Contains(cleaned.Notes, note => note.Contains("is not built", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Emit(string path, string type)
    {
        var compilation = CSharpCompilation.Create("Dependency",
            new[] { CSharpSyntaxTree.ParseText($"namespace Dependency; public class {type} {{ }}") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, image.ToArray());
    }
}
