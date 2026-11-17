using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;
using FluentStyles = EclipseUI.Themes.Fluent.StyleBootstrap;

namespace EclipseUI.Headless.Tests;

public class FluentValueControlTemplateTests
{
    [Fact]
    public void UnthemedValueControlsDoNotDrawAPresetSkin()
    {
        using var check = new CheckBox { Checked = true, Label = "Check" };
        using var radio = new RadioButton { Checked = true, Label = "Radio" };
        using var toggle = new ToggleButton { Checked = true, Label = "Toggle" };
        using var change = new ToggleSwitch { Checked = true, Label = "Switch" };
        using var slider = new Slider { Value = 50 };
        using var progress = new ProgressBar { Value = 50, IsIndeterminate = true };
        var drawing = new DrawingProbe();
        foreach (var element in new Element[] { check, radio, toggle, change, slider, progress }) element.Render(drawing);
        Assert.Equal(0, drawing.Commands);
    }

    [Theory]
    [InlineData("CheckBox")]
    [InlineData("RadioButton")]
    [InlineData("ToggleSwitch")]
    [InlineData("ToggleButton")]
    [InlineData("Slider")]
    [InlineData("ProgressBar")]
    public void EveryValueControlCanReplaceItsFluentVisualTree(string kind)
    {
        using var control = Create(kind);
        control.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(control);
        host.UpdateLayout();
        var original = Assert.IsAssignableFrom<Element>(control.TemplateRoot);
        var replacement = new Border { Width = 40, Height = 18, Background = UiColor.Parse("#123456") };
        control.Template = new FuncControlTemplate<Element>(_ => new TemplateInstance(replacement));
        host.UpdateLayout();
        Assert.Same(replacement, control.TemplateRoot);
        Assert.True(original.IsDisposed);
        Assert.False(control.IsDisposed);
    }

    [Fact]
    public void CheckStateUpdatesTheGlyphWithoutReplacingItsTemplate()
    {
        using var check = new CheckBox { Theme = FluentStyles.FluentCheckBox };
        check.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(check);
        host.UpdateLayout();
        var tree = check.TemplateRoot;
        var glyph = check.Descendants().OfType<FluentCheckBoxGlyph>().Single();
        var drawing = new DrawingProbe();
        glyph.Render(drawing);
        var uncheckedColor = Assert.Single(drawing.Fills);
        check.Toggle();
        host.UpdateLayout();
        drawing.Fills.Clear();
        glyph.Render(drawing);
        Assert.True(check.Checked);
        Assert.Same(tree, check.TemplateRoot);
        Assert.NotEqual(uncheckedColor, Assert.Single(drawing.Fills));
        var accent = FluentTheme.Light.Accent;
        Assert.Equal(new UiColor(accent.R, accent.G, accent.B, accent.A), drawing.Fills[0]);
    }

    [Theory]
    [InlineData("CheckBox")]
    [InlineData("RadioButton")]
    [InlineData("ToggleSwitch")]
    [InlineData("ToggleButton")]
    public void ClickingATemplateGlyphUpdatesItsOwningControl(string kind)
    {
        using var control = Create(kind);
        control.Width = 160;
        control.Height = 32;
        control.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(control);
        host.UpdateLayout();
        host.PointerMove(new(10, 16));
        Assert.True(control.IsHovered);
        host.PointerPress(new(10, 16));
        host.PointerRelease(new(10, 16));
        Assert.True(control is ToggleButton toggle ? toggle.Checked : ((CheckBox)control).Checked);
    }

    [Fact]
    public void SliderPointerUsesTheSameThemeThumbInsetAsItsVisual()
    {
        using var slider = new Slider { Theme = FluentStyles.FluentSlider, Width = 160, Height = 32 };
        using var host = new HeadlessRoot(slider);
        host.UpdateLayout();
        Assert.Equal(22, slider.ThumbSize);
        host.PointerPress(new(160 - slider.TrackInset, 16));
        host.PointerRelease(new(160 - slider.TrackInset, 16));
        Assert.Equal(slider.Maximum, slider.Value);
        host.Focus.Focus(slider);
        host.KeyPress(Key.Home);
        Assert.Equal(slider.Minimum, slider.Value);
    }

    [Fact]
    public void ProgressAnimationBelongsToTheTemplateAndStopsWhenItIsRemoved()
    {
        using var progress = new ProgressBar { Theme = FluentStyles.FluentProgressBar, IsIndeterminate = true };
        using var host = new HeadlessRoot(progress);
        host.UpdateLayout();
        var visual = progress.Descendants().OfType<FluentProgressBarVisual>().Single();
        var before = visual.RenderVersion;
        FrameAnimations.Advance(progress, TimeSpan.FromMilliseconds(200));
        Assert.True(visual.RenderVersion > before);
        progress.IsIndeterminate = false;
        host.UpdateLayout();
        before = visual.RenderVersion;
        FrameAnimations.Advance(progress, TimeSpan.FromMilliseconds(200));
        Assert.Equal(before, visual.RenderVersion);
        progress.Template = new FuncControlTemplate<Element>(_ => new TemplateInstance(new Border()));
        host.UpdateLayout();
        Assert.True(visual.IsDisposed);
        Assert.Empty(progress.Descendants().OfType<FluentProgressBarVisual>());
    }

    private static Element Create(string kind) => kind switch
    {
        "CheckBox" => new CheckBox { Theme = FluentStyles.FluentCheckBox },
        "RadioButton" => new RadioButton { Theme = FluentStyles.FluentRadioButton },
        "ToggleSwitch" => new ToggleSwitch { Theme = FluentStyles.FluentToggleSwitch },
        "ToggleButton" => new ToggleButton { Theme = FluentStyles.FluentToggleButton },
        "Slider" => new Slider { Theme = FluentStyles.FluentSlider },
        "ProgressBar" => new ProgressBar { Theme = FluentStyles.FluentProgressBar },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class DrawingProbe : IElementDrawingContext
    {
        public int Commands { get; private set; }
        public List<UiColor> Fills { get; } = [];
        public UiColor Foreground => default;
        public UiColor Background => default;
        public UiColor Border => default;
        public UiColor Accent => default;
        public UiColor OnAccent => default;
        public void FillRectangle(Rect rect, UiColor color, double radius = 0) { Commands++; Fills.Add(color); }
        public void StrokeRectangle(Rect rect, UiColor color, double width = 1, double radius = 0) => Commands++;
        public void Ellipse(Rect rect, UiColor fill, UiColor stroke, double strokeWidth = 0) => Commands++;
        public void Line(Point start, Point end, UiColor color, double width = 1) => Commands++;
        public void Path(string data, UiColor fill, UiColor stroke, double strokeWidth = 1) => Commands++;
        public void Text(string text, Point origin, double fontSize, UiColor color) => Commands++;
        public void Image(IRasterImage image, Rect destination, double opacity = 1) => Commands++;
        public void PushClip(Rect rect) => Commands++;
        public void PopClip() => Commands++;
    }
}
