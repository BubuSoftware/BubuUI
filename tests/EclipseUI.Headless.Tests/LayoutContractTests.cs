using System;
using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// 布局契约（design/05 §布局契约，Layoutable 级统一面）：Margin 计入 DesiredSize、
/// 显式 Width/Height 覆盖测量期望、Min*/Max* 钳制、对齐档在排列槽内定位。
/// 默认 Stretch 填满槽并统一应用尺寸约束——本套与 LayoutTests / WrapPanelTests / GridTests
/// 互为护栏（naive 测量器确定性：普通字符 0.6×字号、行高 1.4×字号）。
/// </summary>
public class LayoutContractTests
{
    // "abcd" @14：4 × 0.6 × 14 = 33.6 宽，14 × 1.4 = 19.6 高。
    private const double TextWidth = 33.6;
    private const double TextHeight = 19.6;

    private static TextBlock Text(string text)
    {
        var block = new TextBlock();
        block.AddRun(new TextRun(text));
        return block;
    }

    /// <summary>Rect 精度容差断言（naive 测量器的 1.4×字号在二进制里不精确）。</summary>
    private static void AssertRect(Rect actual, double x, double y, double w, double h)
    {
        Assert.Equal(x, actual.X, 3);
        Assert.Equal(y, actual.Y, 3);
        Assert.Equal(w, actual.Width, 3);
        Assert.Equal(h, actual.Height, 3);
    }

    [Fact]
    public void Margin_CountsIntoDesiredSize_AndShiftsTheCoreRect()
    {
        var panel = new StackPanel { Gap = 0 };
        var first = Text("abcd");
        first.Margin = 8;
        var second = Text("efgh");
        panel.Children.Add(first);
        panel.Children.Add(second);

        panel.Measure(Size.Infinity);
        panel.Arrange(new Rect(0, 0, 100, 400));

        // DesiredSize 含外边距（19.6 + 16），面板堆叠自动留位；核心矩形从内槽原点落位。
        Assert.Equal(TextHeight * 2 + 16, panel.DesiredSize.Height, 3);
        // Stretch 填满扣掉 Margin 的内槽（100 - 16 = 84 宽）。
        AssertRect(first.FinalRect, 8, 8, 84, TextHeight);
        AssertRect(second.FinalRect, 0, TextHeight + 16, 100, TextHeight);
    }

    [Fact]
    public void Explicit_Width_AndHeight_OverrideTheMeasuredDesired()
    {
        var panel = new StackPanel { Gap = 0 };
        var child = Text("abcd");   // 自然 33.6 × 19.6
        child.Width = 50;
        child.Height = 20;
        panel.Children.Add(child);

        panel.Measure(Size.Infinity);
        panel.Arrange(new Rect(0, 0, 100, 400));

        Assert.Equal(new Size(50, 20), panel.DesiredSize);
        AssertRect(child.FinalRect, 25, 0, 50, 20);
    }

    [Fact]
    public void Min_AndMax_ClampTheMeasuredDesired()
    {
        var panel = new StackPanel { Gap = 0 };
        var grown = Text("abcd");
        grown.MinWidth = 200;
        var shrunk = Text("abcd");
        shrunk.MaxWidth = 30;
        panel.Children.Add(grown);
        panel.Children.Add(shrunk);

        panel.Measure(Size.Infinity);
        Assert.Equal(200, grown.DesiredSize.Width, 3);
        Assert.Equal(200, panel.DesiredSize.Width, 3);
        Assert.Equal(30, shrunk.DesiredSize.Width, 3);

        panel.Arrange(new Rect(0, 0, 100, 400));
        Assert.Equal(100, panel.DesiredSize.Width, 3);
        Assert.Equal(100, grown.DesiredSize.Width, 3);
        AssertRect(grown.FinalRect, -50, 0, 200, TextHeight);
        AssertRect(shrunk.FinalRect, 35, TextHeight, 30, TextHeight);
    }

    [Fact]
    public void Alignment_PositionsTheChildWithinTheArrangeSlot()
    {
        var grid = new Grid { ColumnDefinitions = "200", RowDefinitions = "100" };
        var centered = Text("abcd");
        centered.HorizontalAlignment = HorizontalAlignment.Center;
        centered.VerticalAlignment = VerticalAlignment.Center;
        var left = Text("abcd");
        left.HorizontalAlignment = HorizontalAlignment.Left;
        left.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(centered);
        grid.Children.Add(left);

        grid.Measure(Size.Infinity);
        grid.Arrange(new Rect(0, 0, 200, 100));

        AssertRect(centered.FinalRect, (200 - TextWidth) / 2, (100 - TextHeight) / 2, TextWidth, TextHeight);
        AssertRect(left.FinalRect, 0, 0, TextWidth, TextHeight);
    }

    [Fact]
    public void Explicit_Width_CombinesWithAlignment()
    {
        var grid = new Grid { ColumnDefinitions = "200", RowDefinitions = "50" };
        var child = Text("abcd");
        child.Width = 100;
        child.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(child);

        grid.Measure(Size.Infinity);
        grid.Arrange(new Rect(0, 0, 200, 50));

        // 显式宽 + Right：贴右缘；垂直未设尺寸约束，Stretch 填满行高。
        AssertRect(child.FinalRect, 200 - 100, 0, 100, 50);
    }

    [Fact]
    public void Stretch_RemainsTheDefault_AndPreservesLegacyBehavior()
    {
        var panel = new StackPanel { Gap = 0 };
        var child = Text("abcd");
        panel.Children.Add(child);

        panel.Measure(Size.Infinity);
        panel.Arrange(new Rect(0, 0, 100, 400));

        AssertRect(child.FinalRect, 0, 0, 100, TextHeight);
    }
}
