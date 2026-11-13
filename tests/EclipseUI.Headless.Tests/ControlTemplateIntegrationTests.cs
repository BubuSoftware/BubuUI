using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class ControlTemplateIntegrationTests
{
    [Fact]
    public void EntireCatalogCompilesAndTwoWayBindingsUpdateInBothDirections()
    {
        using var catalog = new Gallery.ControlCatalog(); using var host = new HeadlessRoot(catalog) { AvailableSize = new(900, 4000) }; host.UpdateLayout();
        var editor = catalog.Descendants().OfType<TextBox>().First(); host.Focus.Focus(editor); editor.SelectAll(); host.Input.TextInput("new text"); host.UpdateLayout(); Assert.Equal("new text", catalog.message);
        catalog.message = "from source"; catalog.Invalidate(); host.UpdateLayout(); Assert.Equal("from source", editor.Text);
        var slider = catalog.Descendants().OfType<Slider>().Single(); host.Focus.Focus(slider); host.KeyPress(Key.End); host.UpdateLayout(); Assert.Equal(100, catalog.level); Assert.Equal(100, catalog.Descendants().OfType<ProgressBar>().Single().Value);
        var buttonWithContent = catalog.Descendants().OfType<Button>().First(b => b.Content is StackPanel); Assert.NotNull(buttonWithContent.Content); Assert.Same(buttonWithContent, buttonWithContent.Content!.Parent);
        Assert.Equal(2, catalog.Descendants().OfType<ContextMenu>().Count());
    }
}
