using System;
using System.Linq;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class S1RootDynamicTests
{
    private static (S1Probe Probe, HeadlessRoot Root, StackPanel Panel, TextBlock Text) Mount()
    {
        var probe = new S1Probe();
        var root = new HeadlessRoot(probe);
        root.UpdateLayout();
        var panel = Assert.IsAssignableFrom<StackPanel>(root.Content);
        var text = panel.Descendants<TextBlock>().Single();
        return (probe, root, panel, text);
    }

    [Fact]
    public void RootPropertyAndText_UpdateAfterInvalidate()
    {
        var (probe, root, panel, text) = Mount();
        Assert.Equal(4d, panel.Gap);
        Assert.Equal("start", text.FullText());

        probe.gap = 7;
        probe.message = "updated";
        probe.Invalidate();
        root.UpdateLayout();

        Assert.Equal(7d, panel.Gap);
        Assert.Equal("updated", text.FullText());
    }

    [Fact]
    public void EqualValue_Rerender_DoesNotRaisePropertyChanged()
    {
        var (probe, root, panel, _) = Mount();
        var changes = 0;
        panel.PropertyChanged += (_, _) => changes++;

        probe.gap = 7;
        probe.Invalidate();
        root.UpdateLayout();
        Assert.Equal(7d, panel.Gap);
        int afterChange = changes;

        probe.Invalidate();
        root.UpdateLayout();

        Assert.Equal(afterChange, changes);
    }

    [Fact]
    public void ControlEvent_InvalidatesComponent_AfterHandler()
    {
        var (_, root, panel, text) = Mount();
        var bump = panel.Descendants<Button>().First(b => b.Label == "bump");

        bump.Click();
        root.UpdateLayout();

        Assert.Equal("bumped", text.FullText());
    }

    [Fact]
    public void ThrowingHandler_SkipsInvalidation()
    {
        var (_, root, panel, text) = Mount();
        var fail = panel.Descendants<Button>().First(b => b.Label == "fail");

        Assert.Throws<InvalidOperationException>(() => fail.Click());
        root.UpdateLayout();

        Assert.Equal("start", text.FullText());
    }
}
