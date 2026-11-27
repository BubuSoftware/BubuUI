using System;
using BubuUI;
using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

/// <summary>
/// G4 布局切片：Grid 行列布局（Auto/固定/星号轨道、Gap/Padding、落格钳制、
/// 空透明块不占格、非法定义 fail-fast）。
/// </summary>
public class GridTests
{
    [Fact]
    public void FixedTracks_PlaceChildrenInCellRects()
    {
        var grid = new Grid
        {
            RowDefinitions = "50,70",
            ColumnDefinitions = "100,120",
        };
        var b1 = new FixedBox(30, 20);
        var b2 = new FixedBox(40, 30);
        Grid.SetRow(b2, 1);
        Grid.SetColumn(b2, 1);
        grid.Children.Add(b1);
        grid.Children.Add(b2);

        grid.Measure(new Size(1000, 1000));
        grid.Arrange(new Rect(Point.Zero, new Size(220, 120)));

        Assert.Equal(new Size(220, 120), grid.DesiredSize);
        Assert.Equal(new Rect(0, 0, 100, 50), b1.FinalRect);
        Assert.Equal(new Rect(100, 50, 120, 70), b2.FinalRect);
    }

    [Fact]
    public void StarTracks_DistributeRemaining_ByWeight()
    {
        var grid = new Grid { RowDefinitions = "*,2*", ColumnDefinitions = "*" };
        var top = new FixedBox(50, 10);
        var bottom = new FixedBox(50, 10);
        Grid.SetRow(bottom, 1);
        grid.Children.Add(top);
        grid.Children.Add(bottom);

        grid.Measure(new Size(300, 300));
        grid.Arrange(new Rect(Point.Zero, new Size(300, 300)));

        // 剩余 300 按 1:2 → 100/200
        Assert.Equal(new Rect(0, 0, 300, 100), top.FinalRect);
        Assert.Equal(new Rect(0, 100, 300, 200), bottom.FinalRect);
    }

    [Fact]
    public void AutoTrack_TakesMaxDesiredOfCell()
    {
        var grid = new Grid { RowDefinitions = "Auto,50", ColumnDefinitions = "Auto" };
        var small = new FixedBox(40, 20);
        var large = new FixedBox(80, 35);   // 同格叠放：Auto 行/列都取格内最大期望
        grid.Children.Add(small);
        grid.Children.Add(large);

        grid.Measure(new Size(500, 500));

        // Auto 行 = max(20,35) = 35；Auto 列 = max(40,80) = 80
        Assert.Equal(35 + 50, grid.DesiredSize.Height, 5);
        Assert.Equal(80, grid.DesiredSize.Width, 5);
    }

    [Fact]
    public void MixedTracks_ReserveFixedAndAuto_StarsShareRest()
    {
        var grid = new Grid { RowDefinitions = "Auto,*,120", ColumnDefinitions = "100,*" };
        var autoChild = new FixedBox(30, 25);
        var starChild = new FixedBox(40, 10);
        Grid.SetRow(starChild, 1);
        Grid.SetColumn(starChild, 1);
        grid.Children.Add(autoChild);
        grid.Children.Add(starChild);

        grid.Measure(new Size(300, 400));
        grid.Arrange(new Rect(Point.Zero, new Size(300, 400)));

        // 高：Auto=25、固定=120 → 星号 = 400-25-120 = 255；宽：固定 100 → 星号 = 200
        Assert.Equal(new Rect(0, 0, 100, 25), autoChild.FinalRect);
        Assert.Equal(new Rect(100, 25, 200, 255), starChild.FinalRect);
        Assert.Equal(400, grid.DesiredSize.Height, 5);
    }

    [Fact]
    public void GapAndPadding_ShrinkStarBudget_AndOffsetTracks()
    {
        var grid = new Grid
        {
            RowDefinitions = "*,*",
            ColumnDefinitions = "*,*",
            Gap = 10,
            Padding = 5,
        };
        var b1 = new FixedBox(10, 10);
        var b2 = new FixedBox(10, 10);
        Grid.SetRow(b2, 1);
        Grid.SetColumn(b2, 1);
        grid.Children.Add(b1);
        grid.Children.Add(b2);

        grid.Measure(new Size(220, 220));
        grid.Arrange(new Rect(Point.Zero, new Size(220, 220)));

        // 内区 210：两轨 + 间隙 10 → 每轨 (210-10)/2 = 100
        Assert.Equal(new Rect(5, 5, 100, 100), b1.FinalRect);
        Assert.Equal(new Rect(115, 115, 100, 100), b2.FinalRect); // 5 + 100 + 10
    }

    [Fact]
    public void OutOfRangeIndices_ClampToLastTrack_NegativeClampsToZero()
    {
        var grid = new Grid { RowDefinitions = "40,40", ColumnDefinitions = "60,60" };
        var overflow = new FixedBox(10, 10);
        var negative = new FixedBox(10, 10);
        Grid.SetRow(overflow, 9);
        Grid.SetColumn(overflow, 9);
        Grid.SetRow(negative, -3);
        Grid.SetColumn(negative, -1);
        grid.Children.Add(overflow);
        grid.Children.Add(negative);

        grid.Measure(new Size(500, 500));
        grid.Arrange(new Rect(Point.Zero, new Size(120, 80)));

        Assert.Equal(new Rect(60, 40, 60, 40), overflow.FinalRect);   // 钳到 (1,1)
        Assert.Equal(new Rect(0, 0, 60, 40), negative.FinalRect);     // 钳到 (0,0)
    }

    [Fact]
    public void DefaultDefinitions_SingleStarCell_FillsGrid()
    {
        var grid = new Grid();
        var child = new FixedBox(30, 30);
        grid.Children.Add(child);

        grid.Measure(new Size(200, 150));
        grid.Arrange(new Rect(Point.Zero, new Size(200, 150)));

        Assert.Equal(new Size(200, 150), grid.DesiredSize); // 未定义轨道 → 单星铺满
        Assert.Equal(new Rect(0, 0, 200, 150), child.FinalRect);
    }

    [Fact]
    public void InfiniteAvailable_StarTracksBehaveLikeAuto()
    {
        var grid = new Grid { RowDefinitions = "*,40", ColumnDefinitions = "*,*" };
        var starRowChild = new FixedBox(70, 22);
        grid.Children.Add(starRowChild);

        grid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // 高度无限 → 星号行退化按内容（22）+ 固定 40；宽度无限 → 星号列按内容（70）
        Assert.Equal(22 + 40, grid.DesiredSize.Height, 5);
        Assert.Equal(70, grid.DesiredSize.Width, 5);
    }

    [Fact]
    public void Overconstrained_StarsShrinkToZero_FixedAndAutoKeepSizes()
    {
        var grid = new Grid { RowDefinitions = "100,*,Auto" };
        var starChild = new FixedBox(20, 10);
        var autoChild = new FixedBox(20, 30);
        Grid.SetRow(starChild, 1);
        Grid.SetRow(autoChild, 2);
        grid.Children.Add(starChild);
        grid.Children.Add(autoChild);

        grid.Measure(new Size(200, 90));
        grid.Arrange(new Rect(Point.Zero, new Size(200, 90)));

        // 剩余 = 90-100-30 < 0 → 星号钳 0；星号子元素拿 0 高矩形
        Assert.Equal(new Rect(0, 100, 200, 0), starChild.FinalRect);
        Assert.Equal(new Rect(0, 100, 200, 30), autoChild.FinalRect);
    }

    [Fact]
    public void SameCell_ChildrenStackInDeclarationOrder()
    {
        var grid = new Grid { RowDefinitions = "50", ColumnDefinitions = "50" };
        var first = new FixedBox(20, 20);
        var second = new FixedBox(30, 30);
        grid.Children.Add(first);
        grid.Children.Add(second);

        grid.Measure(new Size(200, 200));
        grid.Arrange(new Rect(Point.Zero, new Size(50, 50)));

        Assert.Equal(new Rect(0, 0, 50, 50), first.FinalRect);
        Assert.Equal(new Rect(0, 0, 50, 50), second.FinalRect); // 同格叠放
    }

    [Fact]
    public void EmptyTransparentBlock_DoesNotConsumeCell()
    {
        var grid = new Grid { RowDefinitions = "40,40", ColumnDefinitions = "80" };
        var block = new EmptyBlock();          // 布局透明的空块（D77）
        var real = new FixedBox(30, 30);
        Grid.SetRow(real, 1);
        grid.Children.Add(block);
        grid.Children.Add(real);

        grid.Measure(new Size(500, 500));
        grid.Arrange(new Rect(Point.Zero, new Size(80, 80)));

        // 空块不占 (0,0)，real 落 (1,0)；空块 arrange 到零矩形避免 FinalRect 残留
        Assert.Equal(new Rect(0, 0, 0, 0), block.FinalRect);
        Assert.Equal(new Rect(0, 40, 80, 40), real.FinalRect);
    }

    [Fact]
    public void InvalidDefinitions_ThrowFormatException()
    {
        Assert.Throws<FormatException>(() => Measure(new Grid { RowDefinitions = "abc" }));
        Assert.Throws<FormatException>(() => Measure(new Grid { ColumnDefinitions = "**" }));
        Assert.Throws<FormatException>(() => Measure(new Grid { RowDefinitions = "-5" }));
        Assert.Throws<FormatException>(() => Measure(new Grid { ColumnDefinitions = "50," }));
    }

    [Fact]
    public void WeightZeroStar_GetsNothing_SiblingsShareSpace()
    {
        var grid = new Grid { ColumnDefinitions = "0*,*,*" };
        var zero = new FixedBox(10, 10);
        var left = new FixedBox(10, 10);
        Grid.SetColumn(left, 1);
        var right = new FixedBox(10, 10);
        Grid.SetColumn(right, 2);
        grid.Children.Add(zero);
        grid.Children.Add(left);
        grid.Children.Add(right);

        grid.Measure(new Size(200, 50));
        grid.Arrange(new Rect(Point.Zero, new Size(200, 50)));

        Assert.Equal(new Rect(0, 0, 0, 50), zero.FinalRect);
        Assert.Equal(new Rect(0, 0, 100, 50), left.FinalRect);
        Assert.Equal(new Rect(100, 0, 100, 50), right.FinalRect);
    }

    [Fact]
    public void WhitespaceTokens_AreTolerated()
    {
        var grid = new Grid { RowDefinitions = " Auto , 2* , 30 ", ColumnDefinitions = "Auto" };
        var a = new FixedBox(10, 10);
        var b = new FixedBox(10, 10);
        Grid.SetRow(b, 2);
        grid.Children.Add(a);
        grid.Children.Add(b);

        grid.Measure(new Size(200, 200));

        // Auto=10；固定=30；2* = (200-10-30)/2×2 = 160 → 期望高 200
        Assert.Equal(200, grid.DesiredSize.Height, 5);
        Assert.Equal(10, grid.DesiredSize.Width, 5);
    }

    private static void Measure(Grid grid) => grid.Measure(new Size(1000, 1000));

    [Fact]
    public void StarColumn_RemeasuresWrappedContentBeforeResolvingAutoRow()
    {
        var grid = new Grid { ColumnDefinitions = "50,*", RowDefinitions = "Auto,Auto", Gap = 10, Padding = 5 };
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.AddRun(new string('a', 20));
        Grid.SetColumn(text, 1);
        var next = new FixedBox(10, 10);
        Grid.SetRow(next, 1);
        grid.Children.Add(text);
        grid.Children.Add(next);
        foreach (var width in new[] { 120d, 300d, 120d })
        {
            grid.Measure(new Size(width, double.PositiveInfinity));
            grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height));
            var lines = width == 120 ? 4 : 1;
            Assert.Equal(lines, text.TextLines.Count);
            Assert.Equal(lines * 19.6, text.FinalRect.Height, 5);
            Assert.Equal(5 + lines * 19.6 + 10, next.FinalRect.Y, 5);
            Assert.Equal(lines * 19.6 + 30, grid.DesiredSize.Height, 5);
        }
    }

    [Fact]
    public void FinalColumnWidth_RemeasuresWrappedContentAndAutoRow()
    {
        var grid = new Grid { RowDefinitions = "Auto,Auto" };
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.AddRun(new string('a', 20));
        var next = new FixedBox(10, 10);
        Grid.SetRow(next, 1);
        grid.Children.Add(text);
        grid.Children.Add(next);
        grid.Measure(new Size(300, double.PositiveInfinity));
        grid.Arrange(new Rect(0, 0, 50, 300));
        Assert.Equal(4, text.TextLines.Count);
        Assert.Equal(4 * 19.6, next.FinalRect.Y, 5);
        grid.Arrange(new Rect(0, 0, 300, 300));
        Assert.Single(text.TextLines);
        Assert.Equal(19.6, next.FinalRect.Y, 5);
    }

    [Fact]
    public void OverconstrainedFixedAndAutoTracks_DoNotExpandDesiredSize()
    {
        var grid = new Grid { ColumnDefinitions = "400,Auto", RowDefinitions = "200" };
        var child = new FixedBox(800, 300);
        Grid.SetColumn(child, 1);
        grid.Children.Add(child);
        grid.Measure(new Size(300, 100));
        Assert.Equal(new Size(300, 100), grid.DesiredSize);
        grid.Arrange(new Rect(0, 0, 300, 100));
        Assert.Equal(new Rect(400, 0, 800, 200), child.FinalRect);
    }

    [Fact]
    public void DefinitionChanges_DoNotReuseStaleTracksOrZeroStarSizes()
    {
        var grid = new Grid { ColumnDefinitions = "100,100,100", RowDefinitions = "100,100,100" };
        grid.Children.Add(new FixedBox(10, 10));
        grid.Measure(new Size(400, 400));
        Assert.Equal(new Size(300, 300), grid.DesiredSize);
        grid.ColumnDefinitions = "100";
        grid.RowDefinitions = "100";
        grid.Measure(new Size(400, 400));
        Assert.Equal(new Size(100, 100), grid.DesiredSize);
        grid.ColumnDefinitions = "0*";
        grid.RowDefinitions = "0*";
        grid.Measure(new Size(400, 400));
        Assert.Equal(Size.Empty, grid.DesiredSize);
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

    /// <summary>布局透明的空块探针（模拟 @if 为假的块协调器形态）。</summary>
    private sealed class EmptyBlock : Panel
    {
        public override bool IsLayoutTransparent => true;

        protected override Size MeasureCore(Size available) => default;

        protected override void ArrangeCore(Rect final)
        {
        }
    }
}
