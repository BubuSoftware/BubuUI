using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Themes.Fluent;
using Gallery;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class FluentGalleryTests
{
    [Fact]
    public void LiveInputUpdatesStatusAndResetDisposesOpenPopupAndRestoresValues()
    {
        using var page = new FluentGalleryPage();
        new ElementPainter(FluentTheme.Light).Prepare(page);
        using var host = new HeadlessRoot(page) { AvailableSize = new(1180, 960) }; host.UpdateLayout();
        var button = (Button)page.LiveControls[0]; var p = button.PointFromRoot(Point.Zero);
        var point = new Point(-p.X + 10, -p.Y + 12);
        host.Input.PointerPress(point); host.Input.PointerRelease(point);
        Assert.Equal("Button clicked 1 time", page.Status);
        var slider = page.LiveControls.OfType<Slider>().Single(); slider.Value = 75;
        Assert.Equal(75, page.LiveControls.OfType<ProgressBar>().Single().Value);
        var combo = page.LiveControls.OfType<ComboBox>().Single(); combo.IsDropDownOpen = true;
        host.UpdateLayout(); Assert.Contains(page.Descendants().OfType<Popup>(), x => x.IsOpen);
        page.ResetButton.Click(); host.UpdateLayout();
        Assert.True(combo.IsDisposed); Assert.True(button.IsDisposed);
        Assert.Equal("Ready to explore", page.Status);
        Assert.Equal(40, page.LiveControls.OfType<Slider>().Single().Value);
        Assert.DoesNotContain(page.Descendants().OfType<Popup>(), x => x.IsOpen);
        Assert.DoesNotContain(host.Focus.Focusables, x => x.IsDisposed);
    }

    [Fact]
    public void GalleryUsesOnlyLiveDrawingAndKeepsFocusAcrossThemeChanges()
    {
        using var page = new FluentGalleryPage();
        var painter = new ElementPainter(FluentTheme.Light); painter.Prepare(page);
        using var host = new HeadlessRoot(page) { AvailableSize = new(1180, 960) }; host.UpdateLayout();
        var editor = page.LiveControls.OfType<TextBox>().First(); host.Focus.Focus(editor);
        var light = painter.BuildFrame(page, host.AvailableSize);
        Assert.Same(editor, host.Focus.Focused);
        Assert.All(host.Focus.Focusables, x => Assert.True(x == page.ThemeButton || x == page.ResetButton || page.LiveControls.Contains(x) || page.LiveControls.Any(c => c.Descendants().Contains(x))));
        Assert.Empty(light.List.Commands.OfType<DrawImage>());
        var idle = painter.BuildFrame(page, host.AvailableSize);
        Assert.True(idle.Damage.IsEmpty);
        painter.ReplaceTheme(FluentTheme.Dark); painter.Prepare(page); host.UpdateLayout();
        var dark = painter.BuildFrame(page, host.AvailableSize);
        Assert.False(dark.Damage.IsEmpty); Assert.Same(editor, host.Focus.Focused);
        Assert.Empty(dark.List.Commands.OfType<DrawImage>());
        Assert.Empty(dark.List.Validate());
    }

    [Fact]
    public void NarrowWindowFitsLiveControlsWithoutHorizontalScrolling()
    {
        using var page = new FluentGalleryPage();
        new ElementPainter(FluentTheme.Light).Prepare(page);
        using var host = new HeadlessRoot(page);
        foreach (var width in new[] { 1180d, 360d, 700d, 1180d })
        {
            host.AvailableSize = new(width, 760); host.UpdateLayout();
            Assert.Equal(width, page.Viewer.FinalRect.Width, 5);
            Assert.Equal(160, page.LiveControls[0].FinalRect.Width, 5);
            Assert.True(page.Viewer.ScrollableHeight > 0);
            Assert.Equal(0, page.Viewer.ScrollableWidth);
        }
    }

    [Fact]
    public void ThemeButtonCyclesHostAppearanceAndLegacyIsExplicit()
    {
        var app = AppBuilder.Configure<GalleryApp>()
            .ConfigureServices(s => s.AddSingleton(GalleryLaunchOptions.Parse(["--theme", "light"]))).Build();
        using var window = Assert.IsType<MainWindow>(app.MainWindow);
        var host = new AppearanceHost(); app.Host = host;
        window.ThemeButton.Click(); Assert.Same(FluentTheme.Dark, host.Theme);
        window.ThemeButton.Click(); Assert.Same(FluentTheme.System, host.Theme);
        window.ThemeButton.Click(); Assert.Same(FluentTheme.Light, host.Theme);
        Assert.False(GalleryLaunchOptions.Parse([]).Legacy);
        Assert.True(GalleryLaunchOptions.Parse(["--legacy"]).Legacy);
    }

    [Fact]
    public void KeyboardFocusRevealsLiveControlsInASmallViewport()
    {
        using var page = new FluentGalleryPage(); new ElementPainter(FluentTheme.Light).Prepare(page);
        using var host = new HeadlessRoot(page) { AvailableSize = new(360, 640) }; host.UpdateLayout();
        var slider = page.LiveControls.OfType<Slider>().Single();
        Assert.True(host.Focus.Focus(slider, FocusOrigin.Keyboard)); host.UpdateLayout();
        var origin = slider.PointFromRoot(Point.Zero); var viewport = page.Viewer.PointFromRoot(Point.Zero);
        Assert.InRange(viewport.X - origin.X, 0, page.Viewer.ViewportWidth - slider.FinalRect.Width);
        Assert.InRange(viewport.Y - origin.Y, 0, page.Viewer.ViewportHeight - slider.FinalRect.Height);
    }

    private sealed class AppearanceHost : IWindowHost
    {
        public IHostTheme? Theme { get; private set; }
        public void SetTheme(IHostTheme theme) => Theme = theme;
    }
}
