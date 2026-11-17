using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Rendering.Tests;

public class TextBlockOverflowTests
{
    private static readonly Size Surface = new(320, 180);
    private static readonly Color Ink = new(220, 30, 50);

    // Paint the natural text extent as a solid strip so the pixel assertions test clipping,
    // independently of installed fonts, shaping and glyph antialiasing.
    private sealed class StripTextPainter : ITextPainter
    {
        public TextLineMetrics Measure(string text, double size) => new(size, size * .4, text.Length * size * .6);
        public void Paint(DisplayList list, string text, double size, Point origin, Color color)
        {
            var metrics = Measure(text, size);
            list.Add(new FillRect(new(origin, new Size(metrics.Width, metrics.Height)), Ink));
        }
    }

    private static (Canvas Root, StackPanel Panel, TextBlock Text) Scene(bool wrap = false, bool horizontal = false)
    {
        var root = new Canvas { Width = Surface.Width, Height = Surface.Height };
        var panel = new StackPanel
        {
            Width = 100, Height = 70, Padding = 8,
            Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical,
        };
        Canvas.SetLeft(panel, 30); Canvas.SetTop(panel, 20); root.Children.Add(panel);
        var text = new TextBlock { TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (horizontal) text.MaxWidth = 84;
        text.AddRun(new string('a', 100));
        // A separate run starts beyond the visible width in NoWrap mode.
        text.AddRun("last"); panel.Children.Add(text);
        return (root, panel, text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NaturalTextCannotPaintOutsideItsAssignedWidth(bool horizontal)
    {
        var (root, panel, text) = Scene(horizontal: horizontal);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Surface }; host.UpdateLayout();
        Assert.False(panel.ClipToBounds);
        Assert.False(text.ClipToBounds);
        Assert.Equal(84, text.FinalRect.Width);
        Assert.True(text.Runs[1].FinalRect.X > text.FinalRect.Width);
        Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
        var painter = new ElementPainter(FluentTheme.Dark, textPainter: new StripTextPainter());
        var list = painter.Build(root, Surface);
        Assert.Empty(list.Validate());
        var frame = Render.Frame(list);
        PixelProbe.On(frame)
            .Contains(new(40, 30, 40, 10), Ink)
            .NotContains(new(123, 20, 190, 70), Ink)
            .Verify();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextHitTestingMatchesVisibleBounds(bool wrap)
    {
        var (root, _, text) = Scene(wrap);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Surface }; host.UpdateLayout();
        Assert.Same(text.Runs[0], host.Input.HitTest(new(42, 32)));
        Assert.Same(root, host.Input.HitTest(new(160, 32)));
    }

    [Fact]
    public void WrappedLinesAlsoRespectAnExplicitHeight()
    {
        var (root, _, text) = Scene(wrap: true); text.Height = 24;
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Surface }; host.UpdateLayout();
        Assert.True(text.TextLines.Count > 1);
        var list = new ElementPainter(FluentTheme.Dark, textPainter: new StripTextPainter()).Build(root, Surface);
        Assert.Empty(list.Validate());
        PixelProbe.On(Render.Frame(list))
            .Contains(new(40, 30, 40, 10), Ink)
            .NotContains(new(30, 53, 100, 120), Ink)
            .Verify();
    }

    [Fact]
    public void ZeroWidthTextDoesNotLeakItsNaturalRuns()
    {
        var (root, _, text) = Scene(); text.Width = 0;
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Surface }; host.UpdateLayout();
        Assert.Equal(0, text.FinalRect.Width);
        var list = new ElementPainter(FluentTheme.Dark, textPainter: new StripTextPainter()).Build(root, Surface);
        Assert.Empty(list.Validate());
        PixelProbe.On(Render.Frame(list)).Count(Ink, 0, 0).Verify();
    }

    [Fact]
    public void ClipDoesNotAffectFollowingSiblingsAndSurvivesReuseAndResize()
    {
        var (root, panel, text) = Scene();
        var sibling = new Button { Width = 40, Height = 24 };
        var siblingColor = new Color(60, 170, 240);
        Visual.SetBackground(sibling, siblingColor);
        Canvas.SetLeft(sibling, 230); Canvas.SetTop(sibling, 20); root.Children.Add(sibling);
        var painter = new ElementPainter(FluentTheme.Dark, textPainter: new StripTextPainter());
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Surface }; host.UpdateLayout();
        var first = painter.BuildFrame(root, Surface);
        var cached = painter.BuildFrame(root, Surface);
        Assert.True(cached.Damage.IsEmpty);
        Assert.Equal(0, painter.LastBuildStats.Repainted);
        Assert.Equal(Render.Frame(first.List).ContentHash(), Render.Frame(cached.List).ContentHash());
        panel.Width = 70; host.UpdateLayout();
        Assert.Equal(54, text.FinalRect.Width);
        var resized = painter.BuildFrame(root, Surface);
        Assert.False(resized.Damage.IsEmpty);
        var full = new ElementPainter(FluentTheme.Dark, textPainter: new StripTextPainter()).Build(root, Surface);
        Assert.Equal(Render.Frame(full).ContentHash(), Render.Frame(resized.List).ContentHash());
        PixelProbe.On(Render.Frame(resized.List))
            .NotContains(new(93, 20, 130, 70), Ink)
            .At(240, 30, siblingColor)
            .Verify();
    }
}
