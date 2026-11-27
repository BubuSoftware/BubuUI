using System;
using System.Linq;
using BubuUI;
using BubuUI.Elements;
using BubuUI.Rendering;
using Xunit;

namespace BubuUI.Rendering.Tests;

/// <summary>
/// G2-13 damage v0：基于 RenderVersion + 上一帧快照的脏区检测。
/// 断言“变了就报脏、没变不报脏”，以及首帧/resize 的整帧语义。
/// </summary>
public class DamageTests
{
    private static readonly Size DefaultSize = new(600, 400);

    private static void Layout(Element root)
    {
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = DefaultSize };
        host.UpdateLayout();
    }

    private static Rect Absolute(Element element)
    {
        double x = element.FinalRect.X, y = element.FinalRect.Y;
        for (var node = element.Parent; node is not null; node = node.Parent)
        {
            x += node.FinalRect.X;
            y += node.FinalRect.Y;
        }
        return new Rect(x, y, element.FinalRect.Width, element.FinalRect.Height);
    }

    private static bool Covers(FrameDamage damage, Rect rect)
    {
        if (damage.IsFull) return true;
        foreach (var candidate in damage.Rects)
        {
            if (candidate.Left <= rect.Left + 0.01 && candidate.Top <= rect.Top + 0.01 &&
                candidate.Right >= rect.Right - 0.01 && candidate.Bottom >= rect.Bottom - 0.01)
                return true;
        }
        return false;
    }

    private static bool Touches(FrameDamage damage, Point point)
    {
        if (damage.IsFull) return true;
        return damage.Rects.Any(r =>
            r.Left <= point.X + 0.01 && point.X <= r.Right + 0.01 &&
            r.Top <= point.Y + 0.01 && point.Y <= r.Bottom + 0.01);
    }

    private static TextBlock Text(string text, double fontSize = 14)
    {
        var block = new TextBlock { FontSize = fontSize };
        block.AddRun(new TextRun(text));
        return block;
    }

    [Fact]
    public void FirstFrame_IsFull_UnchangedSecondFrame_IsEmpty()
    {
        var root = new StackPanel();
        root.Children.Add(Text("static"));
        Layout(root);

        var painter = new ElementPainter(FluentTheme.Dark);

        var first = painter.BuildFrame(root, DefaultSize);
        Assert.True(first.Damage.IsFull);
        Assert.Equal(1.0, first.Damage.Coverage(DefaultSize));

        var second = painter.BuildFrame(root, DefaultSize);
        Assert.False(second.Damage.IsFull);
        Assert.True(second.Damage.IsEmpty);
        Assert.Equal(0.0, second.Damage.Coverage(DefaultSize));
    }

    [Fact]
    public void CheckBoxToggle_DamagesControlRect()
    {
        var root = new StackPanel();
        var checkBox = new CheckBox { Label = "enable" };
        root.Children.Add(checkBox);
        Layout(root);

        var painter = new ElementPainter(FluentTheme.Dark);
        painter.BuildFrame(root, DefaultSize);

        checkBox.Checked = true;

        var frame = painter.BuildFrame(root, DefaultSize);
        Assert.False(frame.Damage.IsEmpty);
        Assert.True(Covers(frame.Damage, Absolute(checkBox)));
    }

    [Fact]
    public void TextChange_DamagesOldAndNewBounds()
    {
        var root = new StackPanel();
        var block = Text("ab");
        root.Children.Add(block);
        Layout(root);

        var painter = new ElementPainter(FluentTheme.Dark);
        painter.BuildFrame(root, DefaultSize);
        var oldBounds = Absolute(block.Runs[0]);

        block.Runs[0].Text = "abcdefghijkl";
        Layout(root);

        var frame = painter.BuildFrame(root, DefaultSize);
        var newBounds = Absolute(block.Runs[0]);
        Assert.True(Touches(frame.Damage, new Point(oldBounds.Right - 1, oldBounds.Y + oldBounds.Height / 2)));
        Assert.True(Touches(frame.Damage, new Point(newBounds.Right - 1, newBounds.Y + newBounds.Height / 2)));
    }

    [Fact]
    public void LayoutShift_DamagesVacatedAndNewPositions()
    {
        var root = new StackPanel();
        var mover = new Button { Label = "mover" };
        root.Children.Add(mover);
        Layout(root);

        var painter = new ElementPainter(FluentTheme.Dark);
        painter.BuildFrame(root, DefaultSize);
        var before = Absolute(mover);

        root.Padding = 24;
        Layout(root);

        var frame = painter.BuildFrame(root, DefaultSize);
        var after = Absolute(mover);
        Assert.NotEqual(before.Y, after.Y);
        Assert.True(Covers(frame.Damage, before));
        Assert.True(Covers(frame.Damage, after));
    }

    [Fact]
    public void ElementRemoval_DamagesVacatedRect()
    {
        var root = new StackPanel();
        var gone = new Button { Label = "gone" };
        root.Children.Add(gone);
        Layout(root);

        var painter = new ElementPainter(FluentTheme.Dark);
        painter.BuildFrame(root, DefaultSize);
        var bounds = Absolute(gone);

        root.Children.Remove(gone);
        Layout(root);

        var frame = painter.BuildFrame(root, DefaultSize);
        Assert.True(Covers(frame.Damage, bounds));
    }

    [Fact]
    public void FocusChange_DamagesOldAndNewRingArea()
    {
        var root = new StackPanel();
        var first = new Button { Label = "first" };
        var second = new Button { Label = "second" };
        root.Children.Add(first);
        root.Children.Add(second);
        Layout(root);

        var theme = new ElementTheme();
        var painter = new ElementPainter(theme);
        (FocusManager.GetFor(root) ?? new FocusManager(root)).Focus(first, FocusOrigin.Keyboard);
        painter.BuildFrame(root, DefaultSize);

        (FocusManager.GetFor(root) ?? new FocusManager(root)).Focus(second, FocusOrigin.Keyboard);
        var frame = painter.BuildFrame(root, DefaultSize);

        var firstRect = Absolute(first);
        var secondRect = Absolute(second);
        Assert.True(Covers(frame.Damage, Clamp(Inflate(firstRect, theme.FocusRingWidth), DefaultSize)));
        Assert.True(Covers(frame.Damage, Clamp(Inflate(secondRect, theme.FocusRingWidth), DefaultSize)));
    }

    [Fact]
    public void PointerPress_DamagesPressedButton()
    {
        var root = new StackPanel();
        var button = new Button { Label = "press" };
        root.Children.Add(button);
        Layout(root);

        var input = new InputRouter(root);
        var painter = new ElementPainter(FluentTheme.Dark);
        painter.BuildFrame(root, DefaultSize);

        var center = new Point(
            button.FinalRect.X + button.FinalRect.Width / 2,
            button.FinalRect.Y + button.FinalRect.Height / 2);
        input.PointerPress(center);

        (FocusManager.GetFor(root) ?? new FocusManager(root)).Focus(input.Focus.Focused, FocusOrigin.Keyboard);
        var frame = painter.BuildFrame(root, DefaultSize);
        Assert.True(button.IsPressed);
        Assert.True(Covers(frame.Damage, Absolute(button)));
    }

    [Fact]
    public void SizeOrScaleChange_ForcesFullFrame()
    {
        var root = new StackPanel();
        root.Children.Add(Text("resize"));
        Layout(root);

        var painter = new ElementPainter(FluentTheme.Dark);
        painter.BuildFrame(root, DefaultSize);
        Assert.True(painter.BuildFrame(root, DefaultSize).Damage.IsEmpty);

        Assert.True(painter.BuildFrame(root, new Size(800, 600)).Damage.IsFull);

        var painter2 = new ElementPainter(FluentTheme.Dark);
        painter2.BuildFrame(root, DefaultSize, scale: 1.0);
        var scaled = painter2.BuildFrame(root, DefaultSize, scale: 1.5);
        Assert.True(scaled.Damage.IsFull);
    }

    [Fact]
    public void ResetDamage_ForcesFullNextFrame()
    {
        var root = new StackPanel();
        Layout(root);
        Layout(root);

        var painter = new ElementPainter(FluentTheme.Dark);
        painter.BuildFrame(root, DefaultSize);
        painter.ResetDamage();

        Assert.True(painter.BuildFrame(root, DefaultSize).Damage.IsFull);
    }

    [Fact]
    public void Build_WithoutTracking_ClearsDamageHistory()
    {
        var root = new StackPanel();
        Layout(root);

        var painter = new ElementPainter(FluentTheme.Dark);
        painter.BuildFrame(root, DefaultSize);
        painter.Build(root, DefaultSize);

        Assert.True(painter.BuildFrame(root, DefaultSize).Damage.IsFull);
    }

    [Fact]
    public void FrameDamage_MergesOverlappingRects_AndClipsNothingItself()
    {
        var damage = new FrameDamage();
        damage.Add(new Rect(10, 10, 20, 20));
        damage.Add(new Rect(25, 25, 20, 20)); // 与第一个重叠 → 合并

        Assert.Single(damage.Rects);
        Assert.Equal(new Rect(10, 10, 35, 35), damage.Rects[0]);
        Assert.Equal(35 * 35, damage.Area);

        damage.Add(new Rect(100, 100, 10, 10)); // 不相交 → 保留
        Assert.Equal(2, damage.Rects.Count);
        Assert.Equal(35 * 35 + 100, damage.Area);

        var coverage = damage.Coverage(new Size(100, 100));
        Assert.Equal(0.1325, coverage, 6);

        damage.AddFull();
        Assert.True(damage.IsFull);
        Assert.Equal(1.0, damage.Coverage(new Size(100, 100)));
    }

    private static Rect Inflate(Rect rect, double amount) => new(
        rect.X - amount, rect.Y - amount,
        rect.Width + amount * 2, rect.Height + amount * 2);

    private static Rect Clamp(Rect rect, Size surface)
    {
        var left = Math.Max(rect.Left, 0);
        var top = Math.Max(rect.Top, 0);
        var right = Math.Min(rect.Right, surface.Width);
        var bottom = Math.Min(rect.Bottom, surface.Height);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
