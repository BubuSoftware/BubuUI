using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Workspace;
using Xunit;

namespace EclipseUI.Ide.Tests;

/// <summary>镜像发布后的 server：仅从当前 Runtime.dll 元数据建目录，无项目源码。</summary>
public class MirrorDecompileTests
{
    [Fact]
    public void Decompile_WithCurrentServerRuntimeMetadata()
    {
        // Test the currently built runtime, rather than an ignored, possibly stale local publish.
        var provider = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = typeof(UiObject).Assembly.Location,
            IncludeSiblingAssemblies = false,
        });
        var catalog = provider.Catalog;
        Assert.True(catalog.Elements.Count > 0, "runtime catalog is empty");
        var compilation = Assert.IsType<Microsoft.CodeAnalysis.CSharp.CSharpCompilation>(provider.Compilation);
        Assert.Null(compilation.GetTypeByMetadataName("EclipseUI.Component"));

        var analysis = DocumentAnalysis.Create(
            "<TextBlock />", "ProbeDoc.ui", catalog, provider.Compilation, null);
        var displayName = analysis.ResolveAny(catalog, "TextBlock")!.ClrTypeDisplayName;

        var definition = DecompileProvider.Definition(analysis, displayName);
        Assert.NotNull(definition);
        Assert.StartsWith("eui-decompiled:", definition!.Uri, StringComparison.Ordinal);
        Assert.Contains("TextBlock", DecompileProvider.Content(definition.Uri!)!, StringComparison.Ordinal);
    }
}
