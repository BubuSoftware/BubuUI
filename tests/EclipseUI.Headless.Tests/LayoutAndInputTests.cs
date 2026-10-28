using System;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>G2-11：Panel.Padding 参与测量/排列（绘制侧再由 ElementPainter 读 FinalRect）。</summary>
public class PanelPaddingTests
{
    private static (StackPanel Panel, TextBlock First, TextBlock Second) Build(double padding, double gap)
    {
        var panel = new StackPanel { Padding = padding, Gap = gap };
        var first = new TextBlock(); first.AddRun(new TextRun("first"));
        var second = new TextBlock(); second.AddRun(new TextRun("second"));
        panel.Children.Add(first);
        panel.Children.Add(second);
        return (panel, first, second);
    }

    [Fact]
    public void Padding_OffsetsChildrenAndGrowsDesiredSize()
    {
        var (panel, first, second) = Build(padding: 10, gap: 4);
        new HeadlessRoot(panel).UpdateLayout();

        Assert.Equal(10, first.FinalRect.X, 3);
        Assert.Equal(10, first.FinalRect.Y, 3);
        Assert.Equal(10, second.FinalRect.X, 3);
        Assert.Equal(10 + first.DesiredSize.Height + 4, second.FinalRect.Y, 3);

        Assert.Equal(Math.Max(first.DesiredSize.Width, second.DesiredSize.Width) + 20, panel.DesiredSize.Width, 3);
        Assert.Equal(first.DesiredSize.Height + second.DesiredSize.Height + 4 + 20, panel.DesiredSize.Height, 3);
        Assert.Equal(panel.DesiredSize.Width - 20, first.FinalRect.Width, 3);
    }

    [Fact]
    public void Padding_IsClampedToNonNegative()
    {
        var (panel, first, _) = Build(padding: 0, gap: 0);
        panel.Padding = -12;

        Assert.Equal(0, panel.Padding);
        new HeadlessRoot(panel).UpdateLayout();
        Assert.Equal(0, first.FinalRect.X, 3);
    }

    [Fact]
    public void Padding_ZeroMatchesPrePaddingLayout()
    {
        var (panel, first, second) = Build(padding: 0, gap: 8);
        new HeadlessRoot(panel).UpdateLayout();

        Assert.Equal(0, first.FinalRect.X, 3);
        Assert.Equal(0, second.FinalRect.X, 3);
        Assert.Equal(first.DesiredSize.Height + 8, second.FinalRect.Y, 3);
    }

    [Fact]
    public void ListBoxItem_DefaultsToEightPixelPadding()
        => Assert.Equal(8, new ListBoxItem().Padding);

    [Fact]
    public void ClipToBounds_DefaultsOffAndIsSettable()
    {
        var panel = new StackPanel();
        Assert.False(panel.ClipToBounds);
        panel.ClipToBounds = true;
        Assert.True(panel.ClipToBounds);
    }
}

/// <summary>G2-11：无头宿主与窗口宿主共用的 InputRouter 语义。</summary>
public class InputRouterTests
{
    private sealed class FocusablePanel : StackPanel
    {
        public override bool IsFocusable => true;
    }

    private static Point CenterInRoot(Element element)
    {
        double x = element.FinalRect.X + element.FinalRect.Width / 2;
        double y = element.FinalRect.Y + element.FinalRect.Height / 2;
        for (var node = element.Parent; node is not null; node = node.Parent)
        {
            x += node.FinalRect.X;
            y += node.FinalRect.Y;
        }
        return new Point(x, y);
    }

    [Fact]
    public void PointerPress_HitsDeepestButFocusesNearestFocusableAncestor()
    {
        var outer = new FocusablePanel { Padding = 10 };
        var text = new TextBlock(); text.AddRun(new TextRun("nested content"));
        outer.Children.Add(text);

        var root = new HeadlessRoot(outer);
        root.UpdateLayout();

        var point = CenterInRoot(text);
        root.PointerPress(point);

        Assert.Same(text.Runs.Single(), root.HitTest(point)); // 命中保持最深层
        Assert.Same(outer, root.Focus.Focused);              // 焦点交给最近可聚焦祖先
        Assert.Same(text.Runs.Single(), root.Input.Captured);
    }

    [Fact]
    public void PointerRelease_ClearsCapture_AndRoutesToCapturedElement()
    {
        var panel = new StackPanel();
        var button = new Button { Label = "OK" };
        var checkBox = new CheckBox { Label = "other" };
        panel.Children.Add(button);
        panel.Children.Add(checkBox);

        var root = new HeadlessRoot(panel);
        root.UpdateLayout();

        int clicks = 0;
        button.AddHandler(Button.ClickEvent, () => clicks++);

        root.PointerPress(CenterInRoot(button));
        Assert.Same(button, root.Input.Captured);

        // 拖到复选框上释放：仍归按钮，不会误触复选框。
        root.PointerRelease(CenterInRoot(checkBox));

        Assert.Null(root.Input.Captured);
        Assert.Equal(1, clicks);
        Assert.False(checkBox.Checked);
    }

    [Fact]
    public void KeyPress_RoutesFromFocusedElement()
    {
        var panel = new StackPanel();
        var button = new Button { Label = "OK" };
        panel.Children.Add(button);

        var root = new HeadlessRoot(panel);
        root.UpdateLayout();

        Key? seen = null;
        button.AddHandler(InputEvents.KeyDownEvent, args => seen = ((KeyEventArgs)args).Key);

        root.PointerPress(CenterInRoot(button));
        root.KeyPress(Key.Enter);

        Assert.Equal(Key.Enter, seen);
    }

    [Fact]
    public void KeyPress_Tab_MovesFocusWithoutRoutingKeyDown()
    {
        var panel = new StackPanel();
        var first = new Button { Label = "one" };
        var second = new Button { Label = "two" };
        panel.Children.Add(first);
        panel.Children.Add(second);

        var root = new HeadlessRoot(panel);
        root.UpdateLayout();

        bool routed = false;
        first.AddHandler(InputEvents.KeyDownEvent, _ => routed = true);
        second.AddHandler(InputEvents.KeyDownEvent, _ => routed = true);

        root.PointerPress(CenterInRoot(first));
        root.KeyPress(Key.Tab);

        Assert.Same(second, root.Focus.Focused);
        Assert.False(routed);
    }

    [Fact]
    public void PointerPress_OutsideTree_ClearsNothingAndKeepsFocus()
    {
        var panel = new StackPanel();
        var button = new Button { Label = "OK" };
        panel.Children.Add(button);

        var root = new HeadlessRoot(panel);
        root.UpdateLayout();

        root.PointerPress(CenterInRoot(button));
        var focused = root.Focus.Focused;

        root.PointerPress(new Point(-100, -100));
        Assert.Same(focused, root.Focus.Focused);
        Assert.Null(root.Input.Captured);
    }
}
