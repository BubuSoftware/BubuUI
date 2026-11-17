using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using EclipseUI.Rendering.Vello;
using Xunit;

namespace EclipseUI.Rendering.Tests;

public class FluentThemeTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InputUsesRoundedBaselineForPointerFocusAndDisablesItWhenDisabled(bool dark)
    {
        var theme = dark ? FluentTheme.Dark : FluentTheme.Light;
        var root = new Canvas(); var editor = new TextBox { Width = 160, Height = 32 };
        root.Children.Add(editor);
        var painter = new ElementPainter(theme); painter.Prepare(root);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Size }; host.UpdateLayout();
        var normal = painter.BuildFrame(root, Size);
        Assert.Contains(normal.List.Commands.OfType<FillRoundedRect>(), f => f.Rect == new Rect(0, 31, 160, 1) && ColorOf(f) == theme.InputBottomBorder);
        host.Input.PointerPress(new(20, 16)); host.Input.PointerRelease(new(20, 16));
        Assert.True(editor.IsFocused);
        var focused = painter.BuildFrame(root, Size);
        Assert.False(focused.Damage.IsEmpty);
        Assert.Contains(focused.List.Commands.OfType<FillRoundedRect>(), f => f.Rect == new Rect(0, 30, 160, 2) && ColorOf(f) == theme.Accent);
        Assert.Contains(focused.List.Commands, c => c is PushClipPath);
        Assert.Empty(focused.List.Validate());
        Assert.True(painter.BuildFrame(root, Size).Damage.IsEmpty);
        editor.IsEnabled = false;
        Assert.DoesNotContain(painter.Build(root, Size).Commands.OfType<FillRoundedRect>(), f => f.Rect.Height is 1 or 2);
    }

    [Fact]
    public void ThemeMetricsReachMeasurementAndCanBeOverriddenOrRemoved()
    {
        var root = new Canvas(); var check = new CheckBox(); var slider = new Slider(); var progress = new ProgressBar();
        var editor = new TextBox(); var combo = new ComboBox();
        foreach (var element in new Element[] { check, slider, progress, editor, combo }) root.Children.Add(element);
        var painter = new ElementPainter(FluentTheme.Light); painter.Prepare(root);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Size }; host.UpdateLayout();
        Assert.Equal(20, check.EffectiveGlyphSize); Assert.Equal(32, check.DesiredSize.Height);
        Assert.Equal(22, slider.ThumbSize); Assert.Equal(32, slider.DesiredSize.Height);
        Assert.Equal(3, progress.DesiredSize.Height); Assert.Equal(12, editor.HorizontalPadding);
        Assert.Equal(HorizontalAlignment.Left, combo.Descendants().OfType<Button>().First().LabelAlignment);
        check.GlyphSize = 24; editor.HorizontalPadding = 18; progress.Height = 6;
        painter.ReplaceTheme(ElementTheme.Dark); painter.Prepare(root); host.UpdateLayout();
        Assert.Equal(24, check.EffectiveGlyphSize); Assert.Equal(18, editor.HorizontalPadding); Assert.Equal(6, progress.DesiredSize.Height);
        Assert.Equal(0, slider.ThumbSize);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ButtonElevationUsesGradientOnlyInEnabledRestAndHoverStates(bool dark)
    {
        var theme = dark ? FluentTheme.Dark : FluentTheme.Light;
        var root = new Canvas(); var button = new Button { Width = 120, Height = 32 }; root.Children.Add(button);
        var painter = new ElementPainter(theme); painter.Prepare(root);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Size }; host.UpdateLayout();
        var gradient = Assert.IsType<LinearGradient>(Assert.Single(painter.Build(root, Size).Commands.OfType<StrokePath>()).Brush);
        Assert.Equal(theme.ControlSecondaryBorder, gradient.Stops[0].Color);
        Assert.Equal(theme.ControlBorder, gradient.Stops[^1].Color);
        Assert.Equal(dark ? 0 : 32, gradient.Start.Y);
        Assert.Equal(dark ? 3 : 29, gradient.End.Y);
        host.Input.PointerPress(new(10, 10));
        Assert.DoesNotContain(painter.Build(root, Size).Commands.OfType<StrokePath>(), s => s.Brush is LinearGradient);
        host.Input.PointerRelease(new(10, 10)); button.IsEnabled = false;
        Assert.DoesNotContain(painter.Build(root, Size).Commands.OfType<StrokePath>(), s => s.Brush is LinearGradient);
    }

    [Theory]
    [InlineData(1)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2)]
    public void LayeredChromeRetainedFramesMatchFreshRenderingAcrossDpi(double scale)
    {
        var root = new Canvas(); var editor = new TextBox { Width = 150, Height = 32 }; root.Children.Add(editor);
        var button = new Button { Width = 120, Height = 32 }; Canvas.SetTop(button, 50); root.Children.Add(button);
        var painter = new ElementPainter(FluentTheme.Light); painter.Prepare(root);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Size }; host.UpdateLayout();
        painter.BuildFrame(root, Size, scale);
        host.Input.PointerPress(new(20, 16)); host.Input.PointerRelease(new(20, 16));
        var retained = painter.BuildFrame(root, Size, scale).List;
        var fresh = new ElementPainter(FluentTheme.Light).Build(root, Size, scale);
        Assert.Equal(Render.Frame(fresh).ContentHash(), Render.Frame(retained).ContentHash());
        Assert.True(painter.BuildFrame(root, Size, scale).Damage.IsEmpty);
        painter.ReplaceTheme(FluentTheme.Dark); painter.Prepare(root); host.UpdateLayout();
        var changed = painter.BuildFrame(root, Size, scale);
        Assert.False(changed.Damage.IsEmpty);
        Assert.Equal(Render.Frame(new ElementPainter(FluentTheme.Dark).Build(root, Size, scale)).ContentHash(), Render.Frame(changed.List).ContentHash());
    }

    private static readonly Size Size = new(320, 240);
    private sealed class Preference : ISystemThemeProvider { public ThemeVariant Variant { get; set; } }
    private sealed class App : Application { }
    private static Color ColorOf(FillRoundedRect fill) => ((SolidColorBrush)fill.Brush).Color;

    [Fact]
    public void BuilderInstallsSystemSkinAndAcceptsExplicitMode()
    {
        Assert.Same(FluentTheme.System, AppBuilder.Configure<App>().UseFluentTheme().Build().DefaultTheme);
        Assert.Same(FluentTheme.Dark, AppBuilder.Configure<App>().UseFluentTheme(ThemeVariant.Dark).Build().DefaultTheme);
        Assert.Throws<ArgumentOutOfRangeException>(() => FluentTheme.For((ThemeVariant)99));
    }

    [Fact]
    public void DefaultsRespectAllValueLayersAndReachNewChildren()
    {
        var root = new Canvas();
        var button = new Button { Label = "Save" };
        root.Children.Add(button);
        var painter = new ElementPainter(FluentTheme.Light);
        painter.Prepare(root);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Size };
        host.UpdateLayout();
        Assert.Equal(32, button.MinHeight);
        Assert.Equal(32, button.DesiredSize.Height);
        button.SetStyleValue(Element.MinHeightProperty, 36);
        button.SetTemplateValue(Element.MinHeightProperty, 40);
        button.MinHeight = 48;
        Assert.Equal(48, button.MinHeight);
        button.ClearLocalValue(Element.MinHeightProperty);
        Assert.Equal(40, button.MinHeight);
        root.SetValue(TextBlock.FontSizeProperty, 18);
        Assert.Equal(18, button.GetValue(TextBlock.FontSizeProperty));
        var editor = new TextBox(); root.Children.Add(editor);
        host.UpdateLayout();
        Assert.Equal(32, editor.MinHeight);
        Assert.Equal(18, editor.GetValue(TextBlock.FontSizeProperty));
        painter.ReplaceTheme(ElementTheme.Dark); painter.Prepare(root);
        host.UpdateLayout();
        Assert.Equal(0, editor.MinHeight);
        Assert.Equal(40, button.MinHeight);
    }

    [Fact]
    public void DefaultsRemainScopedToTheirActualTree()
    {
        var first = new Canvas(); var second = new Canvas();
        var button = new Button(); first.Children.Add(button);
        new ElementPainter(FluentTheme.Light).Prepare(first);
        using var firstHost = new HeadlessRoot(first) { AvailableSize = Size }; firstHost.UpdateLayout();
        Assert.Equal(32, button.MinHeight);
        first.Children.Remove(button); second.Children.Add(button);
        using var secondHost = new HeadlessRoot(second) { AvailableSize = Size }; secondHost.UpdateLayout();
        Assert.Equal(0, button.MinHeight);
    }

    [Fact]
    public void SystemChangesInvalidateRetainedCommandsAndExplicitThemeStopsFollowing()
    {
        var preference = new Preference { Variant = ThemeVariant.Light };
        var binding = new ThemeBinding(FluentTheme.System, preference);
        var root = new Canvas(); root.Children.Add(new Button { Label = "Save", Width = 120 });
        var painter = new ElementPainter(binding.Current);
        binding.Update(painter, root);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Size };
        host.UpdateLayout();
        painter.BuildFrame(root, Size);
        Assert.True(painter.BuildFrame(root, Size).Damage.IsEmpty);
        preference.Variant = ThemeVariant.Dark;
        binding.Update(painter, root); host.UpdateLayout();
        Assert.Same(FluentTheme.Dark, painter.Theme);
        Assert.False(painter.BuildFrame(root, Size).Damage.IsEmpty);
        Assert.Equal(0, painter.LastBuildStats.Reused);
        Assert.True(painter.BuildFrame(root, Size).Damage.IsEmpty);
        binding.SetSource(FluentTheme.Light); binding.Update(painter, root);
        preference.Variant = ThemeVariant.Dark; binding.Update(painter, root);
        Assert.Same(FluentTheme.Light, painter.Theme);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ButtonsRespondToPointerStatesAndKeepExplicitOverrides(bool dark)
    {
        var theme = dark ? FluentTheme.Dark : FluentTheme.Light;
        var root = new Canvas(); var button = new Button { Width = 120, Height = 32 };
        root.Children.Add(button);
        var painter = new ElementPainter(theme); painter.Prepare(root);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Size }; host.UpdateLayout();
        Color Background() => ColorOf(painter.Build(root, Size).Commands.OfType<FillRoundedRect>().First());
        Assert.Equal(theme.ButtonBackground, Background());
        host.Input.PointerMove(new(10, 10)); Assert.Equal(theme.ButtonHoverBackground, Background());
        host.Input.PointerPress(new(10, 10)); Assert.Equal(theme.ButtonPressedBackground, Background());
        host.Input.PointerRelease(new(10, 10));
        button.IsEnabled = false; Assert.Equal(theme.DisabledBackground, Background());
        var local = new Color(180, 80, 40); Visual.SetBackground(button, local);
        Assert.Equal(local, Background());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SelectionAndFocusUseTheirSemanticTokens(bool dark)
    {
        var theme = dark ? FluentTheme.Dark : FluentTheme.Light;
        var root = new Canvas(); var item = new ListBoxItem { IsSelected = true, Width = 120, Height = 32 };
        root.Children.Add(item);
        var painter = new ElementPainter(theme); painter.Prepare(root);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Size }; host.UpdateLayout();
        var list = painter.Build(root, Size);
        Assert.Contains(list.Commands.OfType<FillRoundedRect>(), fill => ColorOf(fill) == theme.SelectionBackground);
        Assert.Contains(list.Commands.OfType<FillRoundedRect>(), fill => ColorOf(fill) == theme.Accent);
        var button = new Button { Label = "Next", Width = 120, Height = 32 }; root.Children.Add(button); Canvas.SetTop(button, 50);
        host.UpdateLayout(); host.Input.KeyPress(Key.Tab);
        list = painter.Build(root, Size);
        Assert.Contains(list.Commands.OfType<StrokePath>(), stroke => stroke.Brush is SolidColorBrush brush && brush.Color == theme.FocusRing);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ComboBoxDropdownSelectionMarkerIsVisibleOutsideTheTextPadding(bool dark)
    {
        var theme = dark ? FluentTheme.Dark : FluentTheme.Light;
        using var root = new Canvas { Width = 320, Height = 240 };
        var combo = new ComboBox { Width = 180, Height = 32, ItemsSource = new[] { "Alpha", "Beta", "Gamma" }, SelectedIndex = 1 };
        root.Children.Add(combo);
        var painter = new ElementPainter(theme); painter.Prepare(root);
        using var host = new HeadlessRoot(root) { AvailableSize = Size }; host.UpdateLayout();
        combo.IsDropDownOpen = true; host.UpdateLayout();

        var item = Assert.Single(combo.Descendants().OfType<ListBoxItem>(), item => item.IsSelected);
        var origin = item.PointFromRoot(Point.Zero);
        var marker = new Rect(3 - origin.X, 6 - origin.Y, 3, item.FinalRect.Height - 12);
        var list = painter.Build(root, Size);
        Assert.Empty(list.Validate());
        PixelProbe.On(Render.Frame(list)).Contains(marker, theme.Accent, minCount: 4).Verify();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PopupSurfaceIsOpaqueAndClosingDamagesItsShadow(bool dark)
    {
        var theme = dark ? FluentTheme.Dark : FluentTheme.Light;
        var root = new Canvas(); var target = new Button { Width = 100, Height = 32 };
        root.Children.Add(target);
        var border = new Border { Content = new TextBlock(), Width = 120, Height = 60 };
        var popup = new Popup { PlacementTarget = target, Content = border, IsOpen = true };
        root.Children.Add(popup);
        var painter = new ElementPainter(theme); painter.Prepare(root);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Size }; host.UpdateLayout();
        var frame = painter.BuildFrame(root, Size);
        Assert.Empty(frame.List.Validate());
        Assert.Contains(frame.List.Commands.OfType<FillRoundedRect>(), fill => ColorOf(fill) == theme.PopupBackground && ColorOf(fill).A == 255);
        Assert.Contains(frame.List.Commands.OfType<FillRoundedRect>(), fill => ColorOf(fill).A is > 0 and < 255);
        Assert.True(painter.BuildFrame(root, Size).Damage.IsEmpty);
        popup.Close(); host.UpdateLayout();
        Assert.False(painter.BuildFrame(root, Size).Damage.IsEmpty);
    }

    [Fact]
    public void ThemeResourcesFollowFluentPalettesWithoutLosingLocalValues()
    {
        var root = new Canvas(); var button = new Button { Width = 120, Height = 32 };
        root.Children.Add(button);
        var dark = new Color(180, 90, 20); var light = new Color(30, 150, 50);
        Visual.SetBackgroundResource(button, new(dark, light));
        var painter = new ElementPainter(FluentTheme.Light); painter.Prepare(root);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Size }; host.UpdateLayout();
        Assert.Contains(painter.Build(root, Size).Commands.OfType<FillRoundedRect>(), fill => ColorOf(fill) == light);
        painter.ReplaceTheme(FluentTheme.Dark); painter.Prepare(root); host.UpdateLayout();
        Assert.Contains(painter.Build(root, Size).Commands.OfType<FillRoundedRect>(), fill => ColorOf(fill) == dark);
    }

    [Fact]
    public void HoverOverContentReachesTheControlAndClearsWhenLeaving()
    {
        var root = new Canvas();
        var button = new Button { Content = new Border { Width = 60, Height = 16 }, Width = 120, Height = 32 };
        var item = new ListBoxItem { Width = 120, Height = 32 };
        var label = new TextBlock(); label.AddRun("Alpha"); item.Children.Add(label);
        root.Children.Add(button); root.Children.Add(item); Canvas.SetTop(item, 60);
        var painter = new ElementPainter(FluentTheme.Light); painter.Prepare(root);
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Size }; host.UpdateLayout();
        host.Input.PointerMove(new(16, 10));
        Assert.True(button.IsHovered);
        host.Input.PointerMove(new(16, 72));
        Assert.False(button.IsHovered); Assert.True(item.IsHovered);
        host.Input.PointerMove(new(400, 400));
        Assert.False(item.IsHovered);
    }
}
