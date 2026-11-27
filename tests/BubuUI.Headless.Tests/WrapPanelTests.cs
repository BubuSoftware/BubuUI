using System;
using System.Linq;
using BubuUI;
using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

/// <summary>G4 布局补全第一片：WrapPanel 流式换行（水平/竖直、Gap、越界独占一行）。</summary>
public class WrapPanelTests
{
    [Fact]
    public void Horizontal_WrapsIntoRows_WithGapAndPadding()
    {
        var wrap = new WrapPanel { Gap = 8, Padding = 10 };
        var b1 = new FixedBox(100, 50);
        var b2 = new FixedBox(100, 60);
        var b3 = new FixedBox(100, 40);
        wrap.Children.Add(b1);
        wrap.Children.Add(b2);
        wrap.Children.Add(b3);

        wrap.Measure(new Size(250, double.PositiveInfinity));
        wrap.Arrange(new Rect(Point.Zero, new Size(250, wrap.DesiredSize.Height)));

        // 100+8+100=208 ≤ 230 内宽，装不下第三个 → 两行
        Assert.Equal(100 + 8 + 100 + 20, wrap.DesiredSize.Width, 5);
        Assert.Equal(60 + 8 + 40 + 20, wrap.DesiredSize.Height, 5); // 行高 = 行内最大

        Assert.Equal(new Rect(10, 10, 100, 50), b1.FinalRect);
        Assert.Equal(new Rect(118, 10, 100, 60), b2.FinalRect);
        Assert.Equal(new Rect(10, 78, 100, 40), b3.FinalRect); // 第二行：10 + 60 + 8
    }

    [Fact]
    public void Horizontal_NoWrap_WhenEverythingFits()
    {
        var wrap = new WrapPanel { Gap = 8 };
        wrap.Children.Add(new FixedBox(100, 50));
        wrap.Children.Add(new FixedBox(100, 50));

        wrap.Measure(new Size(1000, double.PositiveInfinity));

        Assert.Equal(208, wrap.DesiredSize.Width, 5);
        Assert.Equal(50, wrap.DesiredSize.Height, 5);
    }

    [Fact]
    public void Horizontal_OversizedChild_OccupiesOwnLine()
    {
        var wrap = new WrapPanel();
        var wide = new FixedBox(300, 30);
        var normal = new FixedBox(50, 20);
        wrap.Children.Add(wide);
        wrap.Children.Add(normal);

        wrap.Measure(new Size(250, double.PositiveInfinity));
        wrap.Arrange(new Rect(Point.Zero, new Size(250, wrap.DesiredSize.Height)));

        // 超宽独占一行且不裁剪（测量给无穷主轴约束）；下一元素换行
        Assert.Equal(new Rect(0, 0, 300, 30), wide.FinalRect);
        Assert.Equal(new Rect(0, 30, 50, 20), normal.FinalRect);
        Assert.Equal(250, wrap.DesiredSize.Width, 5); // 局部子项溢出不扩大面板对外期望。
        Assert.Equal(50, wrap.DesiredSize.Height, 5);
    }

    [Fact]
    public void Vertical_WrapsIntoColumns()
    {
        var wrap = new WrapPanel { Orientation = Orientation.Vertical, Gap = 6 };
        var b1 = new FixedBox(50, 100);
        var b2 = new FixedBox(60, 100);
        var b3 = new FixedBox(40, 100);
        wrap.Children.Add(b1);
        wrap.Children.Add(b2);
        wrap.Children.Add(b3);

        wrap.Measure(new Size(double.PositiveInfinity, 250));
        wrap.Arrange(new Rect(Point.Zero, new Size(wrap.DesiredSize.Width, 250)));

        // 100+6+100=206 ≤ 250，装不下第三个 → 两列；列宽 = 列内最大
        Assert.Equal(60 + 6 + 40, wrap.DesiredSize.Width, 5);
        Assert.Equal(100 + 6 + 100, wrap.DesiredSize.Height, 5); // 主轴（高）取最高列

        Assert.Equal(new Rect(0, 0, 50, 100), b1.FinalRect);
        Assert.Equal(new Rect(0, 106, 60, 100), b2.FinalRect);
        Assert.Equal(new Rect(66, 0, 40, 100), b3.FinalRect);
    }

    [Fact]
    public void Empty_WrapPanel_TakesOnlyPadding()
    {
        var wrap = new WrapPanel { Padding = 12 };
        wrap.Measure(new Size(500, 500));
        Assert.Equal(new Size(24, 24), wrap.DesiredSize);
    }

    /// <summary>固定期望尺寸的探针元素（绕过 Measure 递归）。</summary>
    private sealed class FixedBox : Panel
    {
        private readonly Size _size;
        public FixedBox(double width, double height) => _size = new Size(width, height);

        // 跨程序集重写 protected internal 成员只能收窄为 protected（C# 规则）。
        protected override Size MeasureCore(Size available) => _size;

        protected override void ArrangeCore(Rect final)
        {
        }
    }
}

