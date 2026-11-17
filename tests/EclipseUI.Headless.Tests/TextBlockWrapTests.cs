using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// TextBlock 换行（design/05 §布局契约）：Wrap 贪心断行——空格是换行机会、行首空白折叠、
/// 行尾空白不进段、超行宽长词（CJK 无空格串）字符级硬切；NoWrap 单行现状零回归。
/// naive 测量器确定性：普通字符 0.6×字号、空格 0.3×字号、行高 1.4×字号。
/// </summary>
public class TextBlockWrapTests
{
    private const double LineHeight = 19.6;   // 14 × 1.4

    [Theory]
    [InlineData("😀", 15)]
    [InlineData("👩‍💻", 15)]
    [InlineData("👍🏽", 15)]
    [InlineData("❤️", 15)]
    [InlineData("1️⃣", 15)]
    [InlineData("👨‍👩‍👧‍👦", 1)]
    public void WrapKeepsWholeTextElementsEvenWhenOneExceedsTheLine(string emoji, double width)
    {
        var previous = TextMeasurer.Current;
        try
        {
            TextMeasurer.Current = new GraphemeMeasurer();
            using var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
            block.AddRun(emoji + emoji + emoji);
            block.Measure(new(width, double.PositiveInfinity));
            Assert.Equal(new[] { emoji, emoji, emoji }, block.TextLines.Select(line => Assert.Single(line.Segments).Text));
        }
        finally { TextMeasurer.Current = previous; }
    }

    [Fact]
    public void WrapMeasuresTheShapedPrefixInsteadOfSummingIndividualCharacters()
    {
        var previous = TextMeasurer.Current;
        try
        {
            TextMeasurer.Current = new GraphemeMeasurer();
            using var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
            block.AddRun("AVAV");
            block.Measure(new(15, double.PositiveInfinity));
            Assert.Equal(new[] { "AV", "AV" }, block.TextLines.Select(line => Assert.Single(line.Segments).Text));
        }
        finally { TextMeasurer.Current = previous; }
    }

    private sealed class GraphemeMeasurer : ITextMeasurer
    {
        public TextMetrics Measure(string text, double fontSize) => new(
            text == "AV" ? 15 : System.Globalization.StringInfo.ParseCombiningCharacters(text).Length * 10, 20);
    }

    [Fact]
    public void Wrap_BreaksAtWordBoundaries_AndStacksLines()
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        block.AddRun(new TextRun("aaaa bb cc"));   // 33.6 / 4.2 / 16.8 / 4.2 / 16.8

        block.Measure(new Size(40, double.PositiveInfinity));

        // 行1 "aaaa"（bb 放不下换行，行尾空格折叠）；行2 "bb cc"。
        Assert.Equal(2, block.TextLines.Count);
        Assert.Equal(37.8, block.DesiredSize.Width, 3);
        Assert.Equal(LineHeight * 2, block.DesiredSize.Height, 3);
        Assert.Equal(0, block.TextLines[0].Y, 3);
        Assert.Equal("aaaa", Assert.Single(block.TextLines[0].Segments).Text);
        Assert.Equal(LineHeight, block.TextLines[1].Y, 3);
        var line2 = block.TextLines[1].Segments;
        Assert.Equal(new[] { "bb", " ", "cc" }, line2.Select(s => s.Text).ToArray());
        Assert.Equal(new[] { 0, 16.8, 21 }, line2.Select(s => s.X).ToArray());
    }

    [Fact]
    public void Wrap_SplitsLongWordsByCharacter()
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        block.AddRun(new TextRun("aaaaaaaaaa"));   // 10 × 8.4 = 84 > 40

        block.Measure(new Size(40, double.PositiveInfinity));

        // 字符级硬切：4 + 4 + 2（一行最多 4 个字符，33.6 ≤ 40 < 42）。
        Assert.Equal(3, block.TextLines.Count);
        Assert.Equal(new[] { "aaaa", "aaaa", "aa" },
            block.TextLines.Select(l => Assert.Single(l.Segments).Text).ToArray());
        Assert.Equal(0, block.TextLines[0].Y, 3);
        Assert.Equal(LineHeight, block.TextLines[1].Y, 3);
        Assert.Equal(LineHeight * 2, block.TextLines[2].Y, 3);
        Assert.Equal(33.6, block.DesiredSize.Width, 3);
        Assert.Equal(LineHeight * 3, block.DesiredSize.Height, 3);
    }

    [Fact]
    public void Wrap_TrimsLineEnd_AndCollapsesLineStart()
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        block.AddRun(new TextRun("aa   bb"));      // 16.8 / 12.6 / 16.8 → 46.2 > 40

        block.Measure(new Size(40, double.PositiveInfinity));

        // "bb" 随其前导空格一起换行：行尾空格折叠、行首空格折叠——两行各一段纯词。
        Assert.Equal(2, block.TextLines.Count);
        Assert.Equal("aa", Assert.Single(block.TextLines[0].Segments).Text);
        Assert.Equal("bb", Assert.Single(block.TextLines[1].Segments).Text);
        Assert.Equal(16.8, block.DesiredSize.Width, 3);
        Assert.Equal(LineHeight * 2, block.DesiredSize.Height, 3);
    }

    [Fact]
    public void NoWrap_KeepsSingleLine_AndLeavesLinesEmpty()
    {
        var block = new TextBlock();
        block.AddRun(new TextRun("aa   bb"));

        block.Measure(new Size(40, double.PositiveInfinity));

        Assert.Equal(TextWrapping.NoWrap, block.TextWrapping);   // 缺省
        Assert.Equal(40, block.DesiredSize.Width, 3);
        Assert.Equal(46.2, block.Runs[0].DesiredSize.Width, 3); // 内部单行内容需求独立保存。
        Assert.Empty(block.TextLines);
    }

    [Fact]
    public void Painter_PaintsWrappedSegmentsAtLinePositions()
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        block.AddRun(new TextRun("aaaa bb"));
        var root = new HeadlessRoot(block) { AvailableSize = new Size(40, 200) };
        root.UpdateLayout();

        var painter = new RecordingTextPainter();
        new ElementPainter(new ElementTheme(), painter).Build(block, new Size(100, 100));

        // 段按行布局定位（跨行 run 的 FinalRect 只落首段，绘制以 TextLines 为准）。
        Assert.Equal(2, painter.Painted.Count);
        Assert.Equal("aaaa", painter.Painted[0].Text);
        Assert.Equal(0, painter.Painted[0].Origin.X, 3);
        Assert.Equal(0, painter.Painted[0].Origin.Y, 3);
        Assert.Equal("bb", painter.Painted[1].Text);
        Assert.Equal(0, painter.Painted[1].Origin.X, 3);
        Assert.Equal(LineHeight, painter.Painted[1].Origin.Y, 3);
    }

    private sealed class RecordingTextPainter : ITextPainter
    {
        public List<(string Text, double FontSize, Point Origin)> Painted { get; } = new();

        public TextLineMetrics Measure(string text, double fontSize) =>
            new(fontSize, fontSize * 0.4, 0);

        public void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color) =>
            Painted.Add((text, fontSize, topLeft));
    }
}
