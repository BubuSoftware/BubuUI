using BubuUI.LanguageServer.Analysis;
using BubuUI.LanguageServer.Workspace;
using Xunit;

namespace BubuUI.Ide.Tests;

public class LooklessTemplateTests
{
    [Fact]
    public void TemplateOwnerHasTheControlTypeInTheEditingProbe()
    {
        var provider = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = typeof(UiObject).Assembly.Location,
            IncludeSiblingAssemblies = false,
        });
        var analysis = DocumentAnalysis.Create("""
            @namespace Demo
            @using BubuUI.Elements
            @template Button
            <Border Background={Owner.Background} />
            """, "ButtonSkin.bui", provider.Catalog, provider.Compilation, null);
        var probe = Assert.IsType<BubuUI.Compiler.Probe.ExpressionProbe>(analysis.Probe);
        Assert.Equal("BubuUI.Elements.Button", probe.InferType("Owner", [])!.ToDisplayString());
        Assert.Equal("BubuUI.UiColor", probe.InferType("Owner.Background", [])!.ToDisplayString());
    }
}
