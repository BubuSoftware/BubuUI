using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;

namespace EclipseUI.Rendering.Tests;

public class FluentAppearanceOverrideTests
{
    private static readonly Size Surface = new(260, 180);
    private static readonly UiColor Red = new(220, 30, 50);
    private static readonly Color Ink = new(220, 30, 50);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SelectedListItemRespectsStyleAndLocalAppearanceAcrossThemeChanges(bool dark)
    {
        using var root = new Canvas { Width = Surface.Width, Height = Surface.Height };
        var item = new ListBoxItem { Width = 160, Height = 40, IsSelected = true };
        item.SetStyleValue(Element.BackgroundProperty, Red);
        item.SetStyleValue(Element.CornerRadiusProperty, 20d);
        root.Children.Add(item);
        var theme = dark ? FluentTheme.Dark : FluentTheme.Light;
        var painter = new ElementPainter(theme); painter.Prepare(root);
        using var host = new HeadlessRoot(root); host.UpdateLayout();

        AssertAppearance(painter.Build(root, Surface), new(0, 0, 160, 40), 20);
        PixelProbe.On(Render.Frame(painter.Build(root, Surface)))
            .At(20, 20, Ink).At(0, 0, theme.Surface).Verify();

        item.Background = new(10, 160, 60); item.CornerRadius = 12;
        painter.ReplaceTheme(dark ? FluentTheme.Light : FluentTheme.Dark);
        painter.Prepare(root); host.UpdateLayout();
        var list = painter.Build(root, Surface);
        Assert.Contains(list.Commands.OfType<FillRoundedRect>(), fill => fill.Rect == new Rect(0, 0, 160, 40) &&
            fill.Radius == 12 && fill.Brush is SolidColorBrush { Color: var color } && color == new Color(10, 160, 60));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PopupRespectsBackgroundBorderAndCornerOverrides(bool dark)
    {
        using var root = new Canvas { Width = Surface.Width, Height = Surface.Height };
        var target = new Button { Width = 100, Height = 32 };
        var popup = new Popup
        {
            IsOpen = true, PlacementTarget = target, Background = Red, BorderBrush = new(20, 170, 80), CornerRadius = 16,
            Content = new Border { Width = 160, Height = 60, BorderThickness = 0 }
        };
        root.Children.Add(target); root.Children.Add(popup);
        var painter = new ElementPainter(dark ? FluentTheme.Dark : FluentTheme.Light); painter.Prepare(root);
        using var host = new HeadlessRoot(root); host.UpdateLayout();
        var list = painter.Build(root, Surface);
        AssertAppearance(list, new(0, 32, 160, 60), 16);
        Assert.Contains(list.Commands.OfType<StrokePath>(), stroke => stroke.Brush is SolidColorBrush { Color: var color } && color == new Color(20, 170, 80));
        PixelProbe.On(Render.Frame(list)).At(80, 60, Ink).Verify();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ListItemDefaultBackgroundTracksSelectionPointerAndTheme(bool dark)
    {
        using var root = new Canvas { Width = Surface.Width, Height = Surface.Height };
        var item = new ListBoxItem { Width = 160, Height = 40 };
        root.Children.Add(item);
        var theme = dark ? FluentTheme.Dark : FluentTheme.Light;
        var painter = new ElementPainter(theme); painter.Prepare(root);
        using var host = new HeadlessRoot(root); host.UpdateLayout();
        Assert.Equal(ToUi(theme.ItemBackground), item.Background);
        host.PointerMove(new(20, 20));
        Assert.Equal(ToUi(theme.ItemHoverBackground), item.Background);
        host.PointerPress(new(20, 20));
        Assert.True(item.IsPressed);
        Assert.Equal(ToUi(theme.ItemPressedBackground), item.Background);
        item.IsSelected = true;
        Assert.Equal(ToUi(theme.SelectionBackground), item.Background);
        host.PointerRelease(new(20, 20));
        Assert.False(item.IsPressed);
        var replacement = dark ? FluentTheme.Light : FluentTheme.Dark;
        painter.ReplaceTheme(replacement); painter.Prepare(root); host.UpdateLayout();
        Assert.Equal(ToUi(replacement.SelectionBackground), item.Background);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SelectedTreeItemRespectsLocalAndAttachedBackgroundAcrossThemeChanges(bool dark)
    {
        using var root = new Canvas { Width = Surface.Width, Height = Surface.Height };
        var item = new TreeViewItem { Width = 160, Height = 40, IsSelected = true, Background = Red, CornerRadius = 12 };
        root.Children.Add(item);
        var painter = new ElementPainter(dark ? FluentTheme.Dark : FluentTheme.Light); painter.Prepare(root);
        using var host = new HeadlessRoot(root); host.UpdateLayout();
        AssertAppearance(painter.Build(root, Surface), new(0, 0, 160, item.HeaderHeight), 12);
        Visual.SetBackground(item, new Color(10, 160, 60));
        painter.ReplaceTheme(dark ? FluentTheme.Light : FluentTheme.Dark);
        painter.Prepare(root); host.UpdateLayout();
        Assert.Contains(painter.Build(root, Surface).Commands.OfType<FillRoundedRect>(), fill =>
            fill.Rect == new Rect(0, 0, 160, item.HeaderHeight) && fill.Radius == 12 &&
            fill.Brush is SolidColorBrush { Color: var color } && color == new Color(10, 160, 60));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TreeItemDefaultBackgroundTracksSelectionPointerAndTheme(bool dark)
    {
        using var root = new Canvas { Width = Surface.Width, Height = Surface.Height };
        var item = new TreeViewItem { Width = 160, Height = 40 };
        root.Children.Add(item);
        var theme = dark ? FluentTheme.Dark : FluentTheme.Light;
        var painter = new ElementPainter(theme); painter.Prepare(root);
        using var host = new HeadlessRoot(root); host.UpdateLayout();
        Assert.Equal(ToUi(theme.ItemBackground), item.Background);
        host.PointerMove(new(20, 20));
        Assert.Equal(ToUi(theme.ItemHoverBackground), item.Background);
        host.PointerPress(new(20, 20));
        Assert.True(item.IsPressed);
        Assert.Equal(ToUi(theme.ItemPressedBackground), item.Background);
        item.IsSelected = true;
        Assert.Equal(ToUi(theme.SelectionBackground), item.Background);
        host.PointerRelease(new(20, 20));
        Assert.False(item.IsPressed);
        var replacement = dark ? FluentTheme.Light : FluentTheme.Dark;
        painter.ReplaceTheme(replacement); painter.Prepare(root); host.UpdateLayout();
        Assert.Equal(ToUi(replacement.SelectionBackground), item.Background);
        Assert.Contains(painter.Build(root, Surface).Commands.OfType<FillRoundedRect>(), fill =>
            fill.Rect == new Rect(0, 0, 160, item.HeaderHeight) &&
            fill.Brush is SolidColorBrush { Color: var color } && color == replacement.SelectionBackground);
    }

    private static UiColor ToUi(Color color) => new(color.R, color.G, color.B, color.A);
    [Fact]
    public void SelectedTabHeaderUsesTheOwnersCornerRadius()
    {
        using var root = new Canvas { Width = Surface.Width, Height = Surface.Height };
        var tabs = new TabControl { Width = 160, Height = 100, CornerRadius = 12 };
        tabs.Children.Add(new TabItem { Header = "First" });
        root.Children.Add(tabs);
        var painter = new ElementPainter(FluentTheme.Light); painter.Prepare(root);
        using var host = new HeadlessRoot(root); host.UpdateLayout();
        Assert.Contains(painter.Build(root, Surface).Commands.OfType<FillRoundedRect>(), fill =>
            fill.Rect.Height == tabs.HeaderHeight && fill.Radius == 12 &&
            fill.Brush is SolidColorBrush { Color: var color } && color == FluentTheme.Light.SelectionBackground);
    }

    private static void AssertAppearance(DisplayList list, Rect rectangle, double radius)
    {
        Assert.Empty(list.Validate());
        Assert.Contains(list.Commands.OfType<FillRoundedRect>(), fill => fill.Rect == rectangle &&
            fill.Radius == radius && fill.Brush is SolidColorBrush { Color: var color } && color == Ink);
    }
}
