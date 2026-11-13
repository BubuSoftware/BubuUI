using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Text.Shaping;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class UnicodeTextBoxTests
{
    private static string Font(string name) => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), name);
    private static HarfBuzzTextShaper Primary() => new(Font("segoeui.ttf"));

    [Theory]
    [InlineData("中文输入")]
    [InlineData("Hello 中文 😀 🎉")]
    [InlineData("👩‍💻 👍🏽 ❤️ 1️⃣")]
    public void InputPaintsSupportedGlyphsAndSizesCaretForFallbackFonts(string value)
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = TextMeasurer.Current;
        using var shaper = Primary();
        using var painter = new HarfBuzzTextPainter(shaper);
        try
        {
            TextMeasurer.Current = painter;
            using var editor = new TextBox { Width = 650 };
            editor.SetValue(TextBlock.FontSizeProperty, 28);
            using var host = new HeadlessRoot(editor); host.UpdateLayout(); host.Focus.Focus(editor);
            host.Input.TextInput(value); host.UpdateLayout();
            Assert.Equal(value, editor.Text);
            var list = new ElementPainter(textPainter: painter).Build(editor, new(700, 120), focused: editor);
            Assert.Empty(list.Validate());
            var runs = list.Commands.OfType<DrawGlyphs>().ToArray();
            Assert.Equal(value, string.Concat(runs.Select(r => r.Glyphs.Text)));
            Assert.All(runs.SelectMany(r => r.Glyphs.Glyphs), g => Assert.NotEqual(0, g.GlyphId));
            var line = painter.Measure(value, 28);
            Assert.True(editor.FinalRect.Height >= line.Height + 12);
            Assert.True(editor.CaretRect.Height >= line.Height);
            Assert.Equal(6 + line.Width, editor.CaretRect.X, 5);
            Assert.True(runs.All(r => r.Origin.Y > 4 && r.Origin.Y + line.Descent <= editor.FinalRect.Height - 4));
            host.Input.KeyPress(Key.Home); host.Input.KeyPress(Key.Right);
            Assert.Equal(value.Length > 1 && char.IsHighSurrogate(value[0]) ? System.Globalization.StringInfo.GetNextTextElementLength(value) : 1, editor.CaretIndex);
        }
        finally { TextMeasurer.Current = previous; }
    }

    [Fact]
    public void ChinesePlaceholderAndCompositionIncreaseLineHeightBeforeCommit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = TextMeasurer.Current;
        using var shaper = Primary(); using var painter = new HarfBuzzTextPainter(shaper);
        try
        {
            TextMeasurer.Current = painter;
            using var editor = new TextBox { Width = 300, Placeholder = "placeholder" };
            using var host = new HeadlessRoot(editor); host.UpdateLayout(); host.Focus.Focus(editor);
            editor.Placeholder = "请输入中文 😀"; host.UpdateLayout();
            Assert.True(editor.FinalRect.Height >= painter.Measure(editor.Placeholder, 14).Height + 12);
            host.Input.TextComposition("你好😀", CompositionStage.Updated); host.UpdateLayout();
            var list = new ElementPainter(textPainter: painter).Build(editor, new(400, 100));
            Assert.Empty(editor.Text);
            Assert.Contains(list.Commands.OfType<DrawGlyphs>(), r => r.Font.Name == "msyh.ttc");
            Assert.Contains(list.Commands.OfType<DrawGlyphs>(), r => r.Font.Name == "seguiemj.ttf");
            Assert.True(editor.CaretRect.Height >= painter.Measure(editor.CompositionText, 14).Height);
            host.Input.TextComposition("你好😀", CompositionStage.Committed); host.UpdateLayout();
            Assert.Equal("你好😀", editor.Text);
        }
        finally { TextMeasurer.Current = previous; }
    }

    private sealed class TextApp : Application
    {
        public override void OnFrameworkInitializationCompleted() { }
    }

    [Fact]
    public void AppBuilderDefaultTextPipelineLoadsFallbacksAndUsesSameMeasurer()
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = TextMeasurer.Current;
        var app = AppBuilder.Configure<TextApp>().UseHarfBuzzText().Build();
        try
        {
            var painter = app.Services.GetRequiredService<ITextPainter>();
            Assert.Same(painter, TextMeasurer.Current);
            var list = new DisplayList(new(400, 100)); painter.Paint(list, "中文😀", 24, new(0, 0), Color.Black);
            Assert.All(list.Commands.OfType<DrawGlyphs>().SelectMany(r => r.Glyphs.Glyphs), g => Assert.NotEqual(0, g.GlyphId));
            Assert.Equal(new[] { "msyh.ttc", "seguiemj.ttf" }, list.Commands.OfType<DrawGlyphs>().Select(r => r.Font.Name));
        }
        finally { (app.Services as IDisposable)?.Dispose(); TextMeasurer.Current = previous; }
    }
}
