using System.Collections.Generic;

namespace EclipseUI.Text;

public enum TextDirection
{
    LeftToRight,
    RightToLeft,
}

/// <summary>一个字形：glyph id（字体内索引）、源文本簇、进距与偏移（像素）。</summary>
public readonly record struct ShapedGlyph(ushort GlyphId, int Cluster, double XAdvance, double XOffset, double YOffset);

/// <summary>一次 shaping 的结果：glyph run 供布局测量与未来 display list 共用。</summary>
public sealed class ShapedBuffer
{
    public required string Text { get; init; }
    public required double FontSize { get; init; }
    public required TextDirection Direction { get; init; }
    public required IReadOnlyList<ShapedGlyph> Glyphs { get; init; }

    public double Width
    {
        get
        {
            double width = 0;
            foreach (var glyph in Glyphs)
                width += glyph.XAdvance;
            return width;
        }
    }
}

/// <summary>文本整形契约：文本先 shaping 再测量，布局所见即绘制所得（设计文档 05）。</summary>
public interface ITextShaper
{
    ShapedBuffer Shape(string text, double fontSize, TextDirection direction = TextDirection.LeftToRight);
}
