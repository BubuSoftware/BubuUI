using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class LayoutTests
{
    [Fact]
    public void StackPanel_Measure_SumsChildren_WithGap()
    {
        var panel = new StackPanel();
        panel.Gap = 8;
        var a = new TextBlock();
        a.AddRun(new TextRun("AB"));
        var b = new TextBlock();
        b.AddRun(new TextRun("CDEF"));
        panel.Children.Add(a);
        panel.Children.Add(b);

        var root = new HeadlessRoot(panel) { AvailableSize = new Size(1000, 1000) };
        root.UpdateLayout();

        Assert.Equal(16.8, a.DesiredSize.Width, 5);   // 2 × 14 × 0.6
        Assert.Equal(19.6, a.DesiredSize.Height, 5);  // 14 × 1.4
        Assert.Equal(33.6, b.DesiredSize.Width, 5);
        Assert.Equal(33.6, panel.DesiredSize.Width, 5);
        Assert.Equal(19.6 * 2 + 8, panel.DesiredSize.Height, 5);
    }

    [Fact]
    public void StackPanel_Arrange_StacksChildrenWithGap()
    {
        var panel = new StackPanel();
        panel.Gap = 8;
        var a = new TextBlock(); a.AddRun(new TextRun("A"));
        var b = new TextBlock(); b.AddRun(new TextRun("B"));
        panel.Children.Add(a);
        panel.Children.Add(b);

        var root = new HeadlessRoot(panel);
        root.UpdateLayout();

        Assert.Equal(0, a.FinalRect.Y, 5);
        Assert.Equal(a.DesiredSize.Height + 8, b.FinalRect.Y, 5);
    }

    [Fact]
    public void TextChange_MarksDirty_ThenRelayoutsOnUpdate()
    {
        var block = new TextBlock();
        var run = new TextRun("ABC");
        block.AddRun(run);
        var root = new HeadlessRoot(block);
        root.UpdateLayout();
        var before = block.DesiredSize;

        run.Text = "ABCDE";
        Assert.Equal(before, block.DesiredSize); // 脏标记挂起，desired 未变
        root.UpdateLayout();
        Assert.True(block.DesiredSize.Width > before.Width);
    }

    [Fact]
    public void Button_MeasuresLabelWithPadding()
    {
        var button = new Button { Label = "OK" };
        var root = new HeadlessRoot(button);
        root.UpdateLayout();
        Assert.Equal(12 * 2 + 2 * 14 * 0.6, button.DesiredSize.Width, 5);
    }
}
