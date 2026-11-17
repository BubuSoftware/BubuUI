using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class ProjectedPointerStateTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void TemplateContentPressesItsItemAndReleaseOrCancellationClearsIt(bool treeItem, bool cancel)
    {
        using var root = new Canvas { Width = 300, Height = 200 };
        Element item = treeItem ? new TreeViewItem { Header = "Item" } : new ListBoxItem();
        item.Width = 160; item.Height = 40;
        root.Children.Add(item); root.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(root); host.UpdateLayout();
        var releaseCount = 0;
        item.AddHandler(Element.PointerReleasedEvent, _ =>
        {
            Assert.True(item.IsPressed);
            releaseCount++;
        });

        host.PointerPress(new(12, 12));
        Assert.NotSame(item, host.Input.Captured);
        Assert.True(item.IsPressed);
        if (cancel) host.Input.CancelPointerCapture();
        else host.PointerRelease(new(250, 150));

        Assert.False(item.IsPressed);
        Assert.Null(host.Input.Captured);
        Assert.Equal(cancel ? 0 : 1, releaseCount);
    }
}
