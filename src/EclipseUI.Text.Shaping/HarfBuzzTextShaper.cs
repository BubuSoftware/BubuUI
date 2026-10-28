using System;
using System.Collections.Generic;
using EclipseUI.Elements;
using HarfBuzzSharp;

namespace EclipseUI.Text.Shaping;

/// <summary>
/// HarfBuzz shaping 实现（M1 spike：文本先 shaping 再测量）。
/// 布局测量与未来 display list 共用同一 shaping 结果；
/// glyph run 已含进距/偏移，光栅化侧只需字形栅格化。
/// </summary>
public sealed class HarfBuzzTextShaper : ITextShaper, ITextMeasurer, IDisposable
{
    // HarfBuzz 以 26.6 定点数（1/64 像素）返回进距
    private const int FixedScale = 64;

    private readonly Blob _blob;
    private readonly Face _face;
    private readonly Font _font;

    public HarfBuzzTextShaper(string fontPath, int faceIndex = 0)
    {
        _blob = Blob.FromFile(fontPath);
        _face = new Face(_blob, faceIndex);
        _font = new Font(_face);
    }

    public ShapedBuffer Shape(string text, double fontSize, TextDirection direction = TextDirection.LeftToRight)
    {
        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf8(text);
        buffer.GuessSegmentProperties();
        buffer.Direction = direction == TextDirection.LeftToRight
            ? Direction.LeftToRight
            : Direction.RightToLeft;
        _font.SetScale((int)Math.Round(fontSize * FixedScale), (int)Math.Round(fontSize * FixedScale));
        _font.Shape(buffer);

        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        var glyphs = new List<ShapedGlyph>(infos.Length);
        for (int i = 0; i < infos.Length; i++)
        {
            glyphs.Add(new ShapedGlyph(
                (ushort)infos[i].Codepoint,
                (int)infos[i].Cluster,
                positions[i].XAdvance / (double)FixedScale,
                positions[i].XOffset / (double)FixedScale,
                positions[i].YOffset / (double)FixedScale));
        }

        return new ShapedBuffer
        {
            Text = text,
            FontSize = fontSize,
            Direction = direction,
            Glyphs = glyphs,
        };
    }

    /// <summary>字体行盒高度（ascender + |descender|），随字号缩放。</summary>
    public double LineHeight(double fontSize)
    {
        _font.SetScale((int)Math.Round(fontSize * FixedScale), (int)Math.Round(fontSize * FixedScale));
        var extents = _font.GetFontExtentsForDirection(Direction.LeftToRight); // 26.6 定点数
        if (extents.Ascender == 0 && extents.Descender == 0)
            return fontSize * 1.4;
        return (extents.Ascender - extents.Descender) / (double)FixedScale;
    }

    public TextMetrics Measure(string text, double fontSize)
        => new TextMetrics(Shape(text, fontSize).Width, LineHeight(fontSize));

    public void Dispose()
    {
        _font.Dispose();
        _face.Dispose();
        _blob.Dispose();
    }
}
