using Path = System.IO.Path;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BubuUI.Elements;
using BubuUI.Rendering;
using HarfBuzzSharp;

namespace BubuUI.Text.Shaping;

/// <summary>
/// HarfBuzz shaping 实现（M1 spike：文本先 shaping 再测量）。
/// 布局测量与绘制共用同一 shaping 结果；
/// glyph run 已含进距/偏移，光栅化侧只需字形栅格化。
/// </summary>
public sealed class HarfBuzzTextShaper : ITextShaper, ITextMeasurer, IDisposable
{
    // HarfBuzz 以 26.6 定点数（1/64 像素）返回进距
    private const int FixedScale = 64;

    private readonly Blob _blob;
    private readonly Face _face;
    private readonly Font _font;
    private readonly MacBitmapFontMetrics? _bitmapMetrics;

    // HarfBuzz 的 Font 携带可变状态（scale 等），不是线程安全的；
    // 布局测量可能来自后台线程，因此 shaping 与度量统一串行化。
    private readonly object _gate = new();

    public HarfBuzzTextShaper(string fontPath, int faceIndex = 0)
    {
        FontPath = Path.GetFullPath(fontPath);
        _blob = Blob.FromFile(fontPath);
        _face = new Face(_blob, faceIndex);
        _font = new Font(_face);
        Face = FontFace.Load(fontPath, faceIndex);
        _bitmapMetrics = MacBitmapFontMetrics.TryCreate(FontPath, faceIndex);
    }

    /// <summary>供绘制侧使用的字体句柄（与 shaping 同一字体文件/face）。</summary>
    public FontFace Face { get; }

    public string FontPath { get; }

    /// <summary>Tests a complete Unicode text element; joiners and variation selectors are shaping controls.</summary>
    public bool SupportsTextElement(string text)
    {
        lock (_gate)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Value is 0x200C or 0x200D or >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF ||
                    Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.Control)
                    continue;
                if (!_font.TryGetGlyph((uint)rune.Value, out var glyph) || glyph == 0) return false;
            }
            return true;
        }
    }

    public ShapedBuffer Shape(string text, double fontSize, TextDirection direction = TextDirection.LeftToRight)
    {
        lock (_gate)
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
            var renderSize = fontSize;
            double[]? advances = null;
            var opticalScale = 1d;
            if (_bitmapMetrics is not null && double.IsFinite(fontSize) && fontSize > 0)
            {
                // Apple Color Emoji applies optical sizing and tracking outside the
                // OpenType advances returned by HarfBuzz. Use the native metrics for
                // both layout and the bitmap's raster size, including small text.
                var native = _bitmapMetrics.LineMetrics(fontSize);
                var ascent = _font.GetFontExtentsForDirection(Direction.LeftToRight).Ascender / (double)FixedScale;
                if (ascent > 0)
                {
                    opticalScale = native.Ascent / ascent;
                    _font.GetScale(out _, out var verticalScale);
                    renderSize = verticalScale / (double)FixedScale * opticalScale;
                }
                advances = _bitmapMetrics.GlyphAdvances(infos.Select(info => (ushort)info.Codepoint).ToArray(), fontSize);
            }
            var glyphs = new List<ShapedGlyph>(infos.Length);
            for (int i = 0; i < infos.Length; i++)
            {
                glyphs.Add(new ShapedGlyph(
                    (ushort)infos[i].Codepoint,
                    (int)infos[i].Cluster,
                    // Variation selectors/joiners may become invisible zero-advance
                    // glyphs. They must not acquire the native space glyph's width.
                    advances is not null && positions[i].XAdvance != 0
                        ? Math.CopySign(advances[i], positions[i].XAdvance)
                        : positions[i].XAdvance / (double)FixedScale,
                    positions[i].XOffset / (double)FixedScale * opticalScale,
                    positions[i].YOffset / (double)FixedScale * opticalScale));
            }

            return new ShapedBuffer
            {
                Text = text,
                FontSize = renderSize,
                Direction = direction,
                Glyphs = glyphs,
            };
        }
    }

    /// <summary>字体行盒高度（ascender + |descender|），随字号缩放。</summary>
    public double LineHeight(double fontSize) => LineMetrics(fontSize).Height;

    /// <summary>行盒度量：基线以上/以下高度，随字号缩放。</summary>
    public TextLineMetrics LineMetrics(double fontSize)
    {
        lock (_gate)
        {
            if (_bitmapMetrics is not null && double.IsFinite(fontSize) && fontSize > 0)
                return _bitmapMetrics.LineMetrics(fontSize);
            _font.SetScale((int)Math.Round(fontSize * FixedScale), (int)Math.Round(fontSize * FixedScale));
            var extents = _font.GetFontExtentsForDirection(Direction.LeftToRight); // 26.6 定点数
            if (extents.Ascender == 0 && extents.Descender == 0)
                return new TextLineMetrics(fontSize, fontSize * 0.4);

            return new TextLineMetrics(
                extents.Ascender / (double)FixedScale,
                -extents.Descender / (double)FixedScale);
        }
    }

    public TextMetrics Measure(string text, double fontSize)
        => new TextMetrics(Shape(text, fontSize).Width, LineHeight(fontSize));

    public void Dispose()
    {
        lock (_gate)
        {
            _bitmapMetrics?.Dispose();
            _font.Dispose();
            _face.Dispose();
            _blob.Dispose();
        }
    }
}
