using System;
using System.Linq;
using BubuUI;
using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

public class HitTestTests
{
    private static (StackPanel Panel, TextBlock A, TextBlock B, HeadlessRoot Root) Build()
    {
        var panel = new StackPanel();
        panel.Gap = 8;
        var a = new TextBlock(); a.AddRun(new TextRun("AAAA"));
        var b = new TextBlock(); b.AddRun(new TextRun("BBBB"));
        panel.Children.Add(a);
        panel.Children.Add(b);
        var root = new HeadlessRoot(panel);
        root.UpdateLayout();
        return (panel, a, b, root);
    }

    [Fact]
    public void HitTest_ReturnsDeepestElement()
    {
        var (panel, a, b, root) = Build();

        // 文本段是一等元素，命中应到最深处
        var centerA = new Point(a.FinalRect.X + a.FinalRect.Width / 2, a.FinalRect.Y + a.FinalRect.Height / 2);
        Assert.Same(a.Runs.Single(), root.HitTest(centerA));

        var insideB = new Point(b.FinalRect.X + 1, b.FinalRect.Y + 1);
        Assert.Same(b.Runs.Single(), root.HitTest(insideB));
    }

    [Fact]
    public void HitTest_InGapFallsBackToPanel()
    {
        var (panel, a, _, root) = Build();
        var gapPoint = new Point(1, a.FinalRect.Bottom + 4); // 子元素之间的 Gap 区
        Assert.Same(panel, root.HitTest(gapPoint));
    }

    [Fact]
    public void HitTest_MissReturnsNull()
    {
        var (_, _, _, root) = Build();
        Assert.Null(root.HitTest(new Point(root.Content.DesiredSize.Width + 50, 1)));
    }

    [Fact]
    public void HitTest_SkipsNonHitTestVisibleElements()
    {
        var (panel, a, _, root) = Build();
        a.IsHitTestVisible = false;
        var insideA = new Point(a.FinalRect.X + 1, a.FinalRect.Y + 1);
        Assert.Same(panel, root.HitTest(insideA));
    }
}

public class PointerInputTests
{
    private static Point Center(Element element) => new(
        element.FinalRect.X + element.FinalRect.Width / 2,
        element.FinalRect.Y + element.FinalRect.Height / 2);

    [Fact]
    public void PointerPressRelease_OnButton_RaisesClick()
    {
        var panel = new StackPanel();
        var button = new Button { Label = "OK" };
        panel.Children.Add(button);
        var root = new HeadlessRoot(panel);
        root.UpdateLayout();

        int clicks = 0;
        panel.AddHandler(Button.ClickEvent, () => clicks++);

        var center = Center(button);
        root.PointerPress(center);
        root.PointerRelease(center);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void PointerRelease_OnCheckBox_Toggles()
    {
        var panel = new StackPanel();
        var checkbox = new CheckBox { Label = "opt" };
        panel.Children.Add(checkbox);
        var root = new HeadlessRoot(panel);
        root.UpdateLayout();

        var center = Center(checkbox);
        root.PointerPress(center);
        root.PointerRelease(center);
        Assert.True(checkbox.Checked);
    }

    [Fact]
    public void CheckBoxChanged_FiresOnUserToggle_ButNotOnPropertyWrite()
    {
        var panel = new StackPanel();
        var checkbox = new CheckBox { Label = "opt" };
        panel.Children.Add(checkbox);
        var root = new HeadlessRoot(panel);
        root.UpdateLayout();

        var raised = 0;
        Element? source = null;
        checkbox.AddHandler(CheckBox.CheckedChangedEvent, args => { raised++; source = args.Source; });

        var center = Center(checkbox);
        root.PointerPress(center);
        root.PointerRelease(center);
        Assert.Equal(1, raised);
        Assert.Same(checkbox, source);

        // 绑定推回源值的写入不广播，否则模板回调会被自己触发
        checkbox.Checked = false;
        Assert.Equal(1, raised);
    }

    [Fact]
    public void PointerEvent_BubblesToParent_WithRootPosition()
    {
        var panel = new StackPanel();
        var text = new TextBlock(); text.AddRun(new TextRun("hit me"));
        panel.Children.Add(text);
        var root = new HeadlessRoot(panel);
        root.UpdateLayout();

        Point? received = null;
        panel.AddHandler(Element.PointerPressedEvent,
            args => received = ((PointerEventArgs)args).Position);

        var point = new Point(text.FinalRect.X + 2, text.FinalRect.Y + 2);
        root.PointerPress(point);
        Assert.NotNull(received);
        Assert.Equal(point, received.Value);
    }

    [Fact]
    public void ReleaseOutsidePressedElement_GoesToCapturedElement()
    {
        // 捕获语义：按下即捕获，释放路由给捕获元素——不会误触别的控件
        var panel = new StackPanel();
        var button = new Button { Label = "OK" };
        var checkbox = new CheckBox { Label = "opt" };
        panel.Children.Add(button);
        panel.Children.Add(checkbox);
        var root = new HeadlessRoot(panel);
        root.UpdateLayout();

        int clicks = 0;
        panel.AddHandler(Button.ClickEvent, () => clicks++);

        root.PointerPress(Center(button));
        root.PointerRelease(Center(checkbox)); // 拖到别处释放

        Assert.Equal(1, clicks);
        Assert.False(checkbox.Checked);
    }

    [Fact]
    public void Release_AfterCapturedElementDetached_DoesNotCrashOrStrayClick()
    {
        bool flag = false;
        var panel = new StackPanel();
        var block = new ConditionalBlock();
        panel.Children.Add(block);
        block.SetCondition(() => flag);
        block.SetContent(scope => scope.AddRoot(new Button { Label = "gone" }));

        var root = new HeadlessRoot(panel);
        root.UpdateLayout();
        flag = true;
        block.Refresh();
        root.UpdateLayout();

        var button = (Button)block.Children.Single();
        root.PointerPress(Center(button)); // 捕获按钮

        int clicks = 0;
        panel.AddHandler(Button.ClickEvent, () => clicks++);

        flag = false; // 内容卸载，捕获元素脱离树
        block.Refresh();
        root.PointerRelease(Center(panel)); // 释放路由给已脱离元素：不抛异常、不冒泡到面板
        Assert.Equal(0, clicks);
    }
}

public class FocusTests
{
    private sealed record Setup(HeadlessRoot Root, Button B1, CheckBox Cb, Button B2);

    private static Setup Build()
    {
        var panel = new StackPanel();
        var b1 = new Button { Label = "one" };
        var cb = new CheckBox { Label = "check" };
        var b2 = new Button { Label = "two" };
        panel.Children.Add(b1);
        panel.Children.Add(cb);
        panel.Children.Add(b2);
        var root = new HeadlessRoot(panel);
        root.UpdateLayout();
        return new Setup(root, b1, cb, b2);
    }

    [Fact]
    public void Tab_TraversesFocusablesInTreeOrder()
    {
        var s = Build();
        s.Root.KeyPress(Key.Tab);
        Assert.Same(s.B1, s.Root.Focus.Focused);
        s.Root.KeyPress(Key.Tab);
        Assert.Same(s.Cb, s.Root.Focus.Focused);
        s.Root.KeyPress(Key.Tab);
        Assert.Same(s.B2, s.Root.Focus.Focused);
        s.Root.KeyPress(Key.Tab); // 环形回到第一个
        Assert.Same(s.B1, s.Root.Focus.Focused);
    }

    [Fact]
    public void GotLostFocus_EventsRaised()
    {
        var s = Build();
        Element? got = null, lost = null;
        s.Root.Content.AddHandler(Element.GotFocusEvent, args => got = args.Source);
        s.Root.Content.AddHandler(Element.LostFocusEvent, args => lost = args.Source);

        s.Root.Focus.Focus(s.B1);
        Assert.Same(s.B1, got);
        Assert.Null(lost);

        s.Root.Focus.Focus(s.Cb);
        Assert.Same(s.Cb, got);
        Assert.Same(s.B1, lost);
    }

    [Fact]
    public void Space_TogglesFocusedCheckBox_EnterClicksFocusedButton()
    {
        var s = Build();

        s.Root.Focus.Focus(s.Cb);
        s.Root.KeyPress(Key.Space);
        Assert.True(s.Cb.Checked);

        s.Root.Focus.Focus(s.B2);
        int clicks = 0;
        s.B2.AddHandler(Button.ClickEvent, () => clicks++);
        s.Root.KeyPress(Key.Enter);
        s.Root.KeyPress(Key.Space);
        Assert.Equal(2, clicks);
    }

    [Fact]
    public void NonFocusable_CannotBeFocused()
    {
        var s = Build();
        Assert.False(s.Root.Focus.Focus(s.Root.Content));
        Assert.Null(s.Root.Focus.Focused);
    }
}
