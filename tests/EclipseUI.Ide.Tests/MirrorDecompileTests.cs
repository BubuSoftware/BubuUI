using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Workspace;
using Xunit;

namespace EclipseUI.Ide.Tests;

/// <summary>镜像真实 server 条件的临时诊断（打包目录 Runtime.dll + 空工作区）。</summary>
public class MirrorDecompileTests
{
    [Fact]
    public void Decompile_WithBundledServerRuntime()
    {
        // repo root = tests bin 的上溯（EclipseUI.Ide.Tests/bin/Debug/net10.0 → repo）
        var dir = AppContext.BaseDirectory;
        string? repo = null;
        for (var cur = new DirectoryInfo(dir); cur is not null; cur = cur.Parent)
        {
            if (Directory.Exists(Path.Combine(cur.FullName, "ide", "vscode", "server"))) { repo = cur.FullName; break; }
        }
        Assert.False(repo is null, "repo layout not found");

        var serverDir = Path.Combine(repo!, "ide", "vscode", "server");
        var provider = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(serverDir, "EclipseUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        });
        var catalog = provider.Catalog;
        Assert.True(catalog.Elements.Count > 0, "bundled catalog is empty");

        var analysis = DocumentAnalysis.Create(
            "<TextBlock />", "ProbeDoc.ui", catalog, provider.Compilation, null);
        var displayName = analysis.ResolveAny(catalog, "TextBlock")!.ClrTypeDisplayName;

        var definition = DecompileProvider.Definition(analysis, displayName);
        Assert.NotNull(definition);
        Assert.StartsWith("eui-decompiled:", definition!.Uri, StringComparison.Ordinal);
        Assert.Contains("TextBlock", DecompileProvider.Content(definition.Uri!)!, StringComparison.Ordinal);
    }
}
