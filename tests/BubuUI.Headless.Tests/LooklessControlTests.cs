using BubuUI.Elements;
using BubuUI.Rendering;
using BubuUI.Themes.Fluent;
using Xunit;

namespace BubuUI.Headless.Tests;

public class LooklessControlTests
{
    [Fact]
    public void SkinAssignmentsDoNotChangeThePriorityOfUserEventHandlers()
    {
        using var border = new Border();
        border.PropertyChanged += (_, property) => { if (property == Border.BackgroundProperty) border.BorderThickness = 7; };
        using (border.BeginSkinValues()) border.Background = new(1, 2, 3);
        border.SetRuleValue(Border.BorderThicknessProperty, 2d);
        Assert.Equal(7, border.BorderThickness);
    }

    [Fact]
    public void InvalidFactoryCannotDisposeItsOwner()
    {
        using var button = new Button { Theme = StyleBootstrap.TestButton };
        using var host = new HeadlessRoot(button); host.UpdateLayout();
        var old = button.TemplateRoot;
        Assert.Throws<InvalidOperationException>(() => button.Template = new FuncControlTemplate<Button>(owner => new TemplateInstance(owner)));
        Assert.False(button.IsDisposed);
        Assert.Same(old, button.TemplateRoot);
        button.ClearLocalValue(TemplatedControl.TemplateProperty);
    }

    [Fact]
    public void DuplicatePartsRejectAndCleanUpTheNewTree()
    {
        using var button = new Button { Theme = StyleBootstrap.TestButton };
        using var host = new HeadlessRoot(button); host.UpdateLayout();
        var old = button.TemplateRoot;
        var invalidRoot = new StackPanel();
        invalidRoot.Children.Add(new Border { Part = "Duplicate" });
        invalidRoot.Children.Add(new Border { Part = "Duplicate" });
        Assert.Throws<InvalidOperationException>(() => button.Template = new FuncControlTemplate<Button>(_ => new TemplateInstance(invalidRoot)));
        Assert.Same(old, button.TemplateRoot);
        Assert.True(invalidRoot.IsDisposed);
        button.ClearLocalValue(TemplatedControl.TemplateProperty);
    }

    [Fact]
    public void FailedRetemplateKeepsPreviousTreeAndContentAlive()
    {
        using var button = new Button { Theme = StyleBootstrap.TestButton, Content = new TextBox { Text = "state" } };
        using var host = new HeadlessRoot(button); host.UpdateLayout();
        var old = button.TemplateRoot!;
        var invalid = new FuncControlTemplate<Button>(_ => throw new InvalidOperationException("construction failed"));
        Assert.Throws<InvalidOperationException>(() => button.Template = invalid);
        Assert.Same(old, button.TemplateRoot);
        Assert.False(old.IsDisposed);
        Assert.Same(button.Content, button.Descendants().OfType<ContentPresenter>().Single().Content);
        button.ClearLocalValue(TemplatedControl.TemplateProperty);
        host.UpdateLayout();
        Assert.False(button.Content!.IsDisposed);
    }

    [Fact]
    public void CallerContentDoesNotBecomeOwnedTemplateParts()
    {
        var content = new Border { Part = "PART_ContentPresenter" };
        using var button = new Button { Theme = StyleBootstrap.TestButton, Content = content };
        using var host = new HeadlessRoot(button); host.UpdateLayout();
        Assert.Null(content.TemplatedParent);
        button.Theme = StyleBootstrap.Alternative;
        Assert.False(content.IsDisposed);
    }

    [Fact]
    public void ExplicitTemplateCanBeRemovedAndLegacyContentIsRestored()
    {
        var content = new TextBox { Text = "value" };
        using var button = new Button { Content = content, Template = new Fixtures.LooklessButton() };
        using var host = new HeadlessRoot(button); host.UpdateLayout();
        var old = button.TemplateRoot!;
        button.Template = null; host.UpdateLayout();
        Assert.True(old.IsDisposed);
        Assert.Null(button.TemplateRoot);
        Assert.Same(button, content.Parent);
        Assert.False(content.IsDisposed);
    }

    [Fact]
    public void CompiledTemplateCreatesIndependentTreesAndUpdatesOwnerBindings()
    {
        using var root = new StackPanel();
        var first = new Button { Label = "First", Theme = StyleBootstrap.TestButton };
        var second = new Button { Label = "Second", Theme = StyleBootstrap.TestButton };
        root.Children.Add(first); root.Children.Add(second);
        using var host = new HeadlessRoot(root); host.UpdateLayout();
        Assert.NotNull(first.TemplateRoot);
        Assert.NotSame(first.TemplateRoot, second.TemplateRoot);
        Assert.Same(first.TemplateRoot, Assert.Single(first.VisualChildren));
        var presenter = first.Descendants().OfType<ContentPresenter>().Single();
        Assert.Equal("First", presenter.Label);
        first.Label = "Changed";
        Assert.Equal("Changed", presenter.Label);
        Assert.Equal("Second", second.Descendants().OfType<ContentPresenter>().Single().Label);
    }

    [Fact]
    public void ThemeSwapPreservesCallerContentAndDisposesOnlyOldVisuals()
    {
        var content = new TextBox { Text = "Keep me" };
        using var button = new Button { Content = content, Theme = StyleBootstrap.TestButton };
        using var host = new HeadlessRoot(button); host.UpdateLayout();
        var old = button.TemplateRoot!;
        host.Focus.Focus(content);
        button.Theme = StyleBootstrap.Alternative;
        host.UpdateLayout();
        Assert.True(old.IsDisposed);
        Assert.False(content.IsDisposed);
        Assert.Same(content, button.Content);
        Assert.Same(content, host.Focus.Focused);
        Assert.Equal("Keep me", content.Text);
        Assert.IsAssignableFrom<StackPanel>(button.TemplateRoot);
        Assert.Same(content, button.Descendants().OfType<ContentPresenter>().Single().Content);
        button.Dispose();
        Assert.True(content.IsDisposed);
    }

    [Fact]
    public void StateRulesRestoreBindingsAndLocalValuesWin()
    {
        using var button = new Button { Label = "Click", Theme = StyleBootstrap.TestButton };
        using var host = new HeadlessRoot(button); host.UpdateLayout();
        var border = Assert.IsAssignableFrom<Border>(button.TemplateRoot);
        Assert.Equal(UiColor.Parse("#112233"), border.Background);
        button.IsEnabled = false;
        Assert.Equal(UiColor.Parse("#999999"), border.Background);
        button.IsEnabled = true;
        Assert.Equal(UiColor.Parse("#112233"), border.Background);
        border.Background = UiColor.Parse("#00ff00");
        button.IsEnabled = false;
        Assert.Equal(UiColor.Parse("#00ff00"), border.Background);
        border.ClearLocalValue(Border.BackgroundProperty);
        Assert.Equal(UiColor.Parse("#999999"), border.Background);
    }

    [Fact]
    public void TypedStylesCrossOnlyTheExplicitTemplateBoundary()
    {
        var previous = Styling.Provider;
        var registry = new StyleRegistry(); StyleBootstrap.Apply(registry); Styling.Provider = registry;
        try
        {
            using var button = new Button { Label = "Styled", Width = 100, Height = 40 };
            button.ApplyClasses(["template-test"]);
            using var host = new HeadlessRoot(button); host.UpdateLayout();
            var border = Assert.IsAssignableFrom<Border>(button.TemplateRoot);
            Assert.Equal(UiColor.Parse("#112233"), border.Background);
            host.Input.PointerMove(new(10, 10)); host.UpdateLayout();
            Assert.Equal(UiColor.Parse("#ff0000"), border.Background);
            host.Input.PointerMove(new(200, 200)); host.UpdateLayout();
            Assert.Equal(UiColor.Parse("#112233"), border.Background);
        }
        finally { Styling.Provider = previous; }
    }

    [Fact]
    public void FluentButtonUsesCompiledThemeAndSwitchesPaletteWithoutReplacingTree()
    {
        using var button = new Button { Label = "Fluent" };
        button.SetThemeDefaults(FluentTheme.Light);
        using var host = new HeadlessRoot(button); host.UpdateLayout();
        var border = Assert.IsAssignableFrom<Border>(button.TemplateRoot);
        Assert.Equal(UiColor.Parse("#ffffffb3"), border.Background);
        button.SetThemeDefaults(FluentTheme.Dark); host.UpdateLayout();
        Assert.Same(border, button.TemplateRoot);
        Assert.Equal(UiColor.Parse("#ffffff0f"), border.Background);
    }

    [Fact]
    public void TemplateButtonKeepsKeyboardAndPointerBehavior()
    {
        using var button = new Button { Label = "Click", Theme = StyleBootstrap.TestButton, Width = 100, Height = 40 };
        using var host = new HeadlessRoot(button); host.UpdateLayout();
        var count = 0; button.AddHandler(Button.ClickEvent, _ => count++);
        host.Focus.Focus(button); host.KeyPress(Key.Space);
        Assert.Equal(1, count);
        host.PointerPress(new(10, 10)); host.PointerRelease(new(10, 10));
        Assert.Equal(2, count);
    }
}
