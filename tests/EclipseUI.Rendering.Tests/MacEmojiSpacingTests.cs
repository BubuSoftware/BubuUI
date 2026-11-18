using System.Runtime.InteropServices;
using EclipseUI.Text.Shaping;
using Xunit;

namespace EclipseUI.Rendering.Tests;

public class MacEmojiSpacingTests
{
    [Theory]
    [InlineData(8)] [InlineData(13.4)] [InlineData(14)] [InlineData(16)] [InlineData(17.25)] [InlineData(18)] [InlineData(24)] [InlineData(40)]
    public void EmojiAdvancesAndOpticalSizeMatchTheSystemFont(double size)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var system = new NativeEmojiFont(size);
        using var shaper = new HarfBuzzTextShaper("/System/Library/Fonts/Apple Color Emoji.ttc");
        using var painter = new HarfBuzzTextPainter(shaper, []);
        foreach (var emoji in new[] { "😀", "🎉", "👩‍💻", "👍🏽", "❤️", "🇨🇳", "1️⃣" })
        {
            var single = shaper.Shape(emoji, size);
            var visible = Assert.Single(single.Glyphs, glyph => glyph.XAdvance > 0);
            var expected = system.Advance(visible.GlyphId);
            Assert.Equal(expected, single.Width, 5);
            Assert.Equal(expected * 3, painter.Measure(emoji + emoji + emoji, size).Width, 5);
            // This font's design ascender is one em. Its native small-size optical
            // adjustment must reach the rasterizer as well as the line metrics.
            Assert.Equal(system.Ascent, single.FontSize, 3);
            Assert.Equal(system.Ascent, shaper.LineMetrics(size).Ascent, 5);
            Assert.Equal(system.Descent, shaper.LineMetrics(size).Descent, 5);
            Assert.All(single.Glyphs.Where(glyph => glyph.GlyphId != visible.GlyphId), glyph => Assert.Equal(0, glyph.XAdvance));
        }
    }

    [Theory]
    [InlineData(14, 1)] [InlineData(14, 1.25)] [InlineData(14, 1.5)] [InlineData(14, 2)]
    [InlineData(24, 1)] [InlineData(24, 1.25)] [InlineData(24, 1.5)] [InlineData(24, 2)]
    public void FullWidthEmojiLeaveVisibleRoomBeforeTheNextGlyph(double size, double scale)
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var shaper = new HarfBuzzTextShaper("/System/Library/Fonts/Supplemental/Arial.ttf");
        using var painter = new HarfBuzzTextPainter(shaper);
        var metrics = painter.Measure("🎉", size);
        var list = new DisplayList(new(120, 100)).Add(new PushTransform(Transform.Scale(scale)));
        painter.Paint(list, "🎉", size, new(8, 8), Color.Black);
        list.Add(new PopTransform());
        var frame = Render.Frame(list, (int)(120 * scale), (int)(100 * scale));
        var ink = frame.OpaqueBounds();
        Assert.True(metrics.Width - ink.Width / scale > .2,
            $"The emoji fills its entire advance: ink={ink.Width / scale}, advance={metrics.Width} at {size}/{scale}.");
        Assert.True(ink.Width / scale >= size * .9);
    }
}

// Independent system-font reference: name matching here is deliberately separate
// from the production provider's exact file/collection-face descriptor selection.
internal sealed class NativeEmojiFont : IDisposable
{
    private const string CoreText = "/System/Library/Frameworks/CoreText.framework/CoreText";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly Lazy<nint> AppKit = new(() => NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"));
    private nint _font;
    internal NativeEmojiFont(double size)
    {
        _ = AppKit.Value;
        var name = CFStringCreateWithCString(0, "AppleColorEmoji", 0x08000100);
        try { _font = CTFontCreateWithName(name, size, 0); }
        finally { CFRelease(name); }
        if (_font == 0) throw new InvalidOperationException("Apple Color Emoji is unavailable.");
    }
    internal double Ascent => CTFontGetAscent(_font);
    internal double Descent => CTFontGetDescent(_font);
    internal double Advance(ushort glyph) => CTFontGetAdvancesForGlyphs(_font, 1, [glyph], 0, 1);
    public void Dispose() { if (_font != 0) CFRelease(_font); _font = 0; }
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
    [DllImport(CoreText)] private static extern nint CTFontCreateWithName(nint name, double size, nint matrix);
    [DllImport(CoreText)] private static extern double CTFontGetAscent(nint font);
    [DllImport(CoreText)] private static extern double CTFontGetDescent(nint font);
    [DllImport(CoreText)] private static extern double CTFontGetAdvancesForGlyphs(nint font, uint orientation, ushort[] glyphs, nint advances, nint count);
}
