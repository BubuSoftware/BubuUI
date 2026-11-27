using BubuUI.Compiler;
using BubuUI.LanguageServer.Analysis;
using BubuUI.LanguageServer.Workspace;
using Xunit;

namespace BubuUI.Ide.Tests;

/// <summary>镜像发布后的 server：仅从当前 Runtime.dll 元数据建目录，无项目源码。</summary>
public class MirrorDecompileTests
{
    [Fact]
    public async Task ConcurrentDefinitionsFromOneAssemblyKeepTheirOwnText()
    {
        var provider = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = typeof(UiObject).Assembly.Location,
            IncludeSiblingAssemblies = false,
        });
        using var start = new Barrier(4);
        var tasks = new[] { "TextBlock", "Border", "Button", "StackPanel" }.Select(name =>
            Task.Factory.StartNew(() =>
            {
                var analysis = DocumentAnalysis.Create("<TextBlock />", "ProbeDoc.bui",
                    provider.Catalog, provider.Compilation, null);
                var probe = analysis.Probe;
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(30)));
                Assert.NotNull(probe);
                var definition = DecompileProvider.Definition(analysis, "BubuUI.Elements." + name);
                Assert.NotNull(definition);
                Assert.Contains("class " + name, definition.Text, StringComparison.Ordinal);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(tasks);
    }

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
        Assert.Null(compilation.GetTypeByMetadataName("BubuUI.Component"));

        var analysis = DocumentAnalysis.Create(
            "<TextBlock />", "ProbeDoc.bui", catalog, provider.Compilation, null);
        var displayName = analysis.ResolveAny(catalog, "TextBlock")!.ClrTypeDisplayName;

        var definition = DecompileProvider.Definition(analysis, displayName);
        Assert.NotNull(definition);
        Assert.StartsWith("eui-decompiled:", definition!.Uri, StringComparison.Ordinal);
        Assert.Contains("TextBlock", DecompileProvider.Content(definition.Uri!)!, StringComparison.Ordinal);
    }
}
