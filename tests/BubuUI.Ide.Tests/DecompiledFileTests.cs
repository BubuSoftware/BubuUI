using System.Text.Json.Nodes;
using BubuUI.LanguageServer;
using BubuUI.LanguageServer.Analysis;
using BubuUI.LanguageServer.Protocol;
using BubuUI.LanguageServer.Server;
using BubuUI.LanguageServer.Workspace;
using Xunit;

namespace BubuUI.Ide.Tests;

public sealed class DecompiledFileTests
{
    [Fact]
    public void CacheUsesContentVersionedPathsAndReadOnlyFiles()
    {
        const string source = "eui-decompiled:///../../escape.cs?assembly=A";
        var first = DecompiledFileCache.Materialize(source, "class A {}");
        Assert.Equal(first, DecompiledFileCache.Materialize(source, "class A {}"));
        Assert.NotEqual(first, DecompiledFileCache.Materialize(source, "class A { int B; }"));
        var path = DocumentStore.UriToPath(first);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "BubuUI", "decompiled"), path);
        Assert.Equal("class A {}", File.ReadAllText(path));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        Assert.True(ServerArguments.Parse(["--materialize-decompiled"]).MaterializeDecompiled);
        Assert.False(ServerArguments.Parse([]).MaterializeDecompiled);
    }

    [Fact]
    public async Task RiderDefinitionReturnsReadableFileWithTheOriginalSymbolRange()
    {
        var (service, uri, _) = TestHost.Open("@namespace Demo\n@using BubuUI.Elements\n<Button La|bel=\"ok\" />");
        var catalog = new CatalogProvider(new CatalogOptions { RuntimePath = Path.Combine(AppContext.BaseDirectory, "BubuUI.Runtime.dll") });
        var host = new LanguageServerHost(new JsonRpcConnection(new MemoryStream(), new MemoryStream()), service, catalog)
        {
            MaterializeDecompiled = true,
        };
        var parameters = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = 2, ["character"] = 10 } };
        var result = await host.HandleRequestAsync(new(JsonValue.Create(1)!, "textDocument/definition", JsonNode.Parse(parameters.ToJsonString())));
        Assert.NotNull(result);
        var target = result!["uri"]!.GetValue<string>();
        Assert.StartsWith("file:", target);
        var text = File.ReadAllText(DocumentStore.UriToPath(target));
        var start = result["range"]!["start"]!;
        var line = text.Split('\n')[start["line"]!.GetValue<int>()];
        Assert.StartsWith("Label", line[start["character"]!.GetValue<int>()..]);
    }
}
