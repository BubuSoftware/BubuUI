using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Workspace;
using Xunit;

namespace EclipseUI.Ide.Tests;

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
            @using EclipseUI.Elements
            @template Button
            <Border Background={Owner.Background} />
            """, "ButtonSkin.ui", provider.Catalog, provider.Compilation, null);
        var probe = Assert.IsType<EclipseUI.Compiler.Probe.ExpressionProbe>(analysis.Probe);
        Assert.Equal("EclipseUI.Elements.Button", probe.InferType("Owner", [])!.ToDisplayString());
        Assert.Equal("EclipseUI.UiColor", probe.InferType("Owner.Background", [])!.ToDisplayString());
    }
}
