using BubuUI.Elements;
using Xunit;

namespace BubuUI.Rendering.Tests;

public class FocusVisualTests
{
    private static readonly Size Surface = new(400, 260);
    private static StrokePath[] Rings(DisplayList list) => list.Commands.OfType<StrokePath>()
        .Where(s => s.Brush is SolidColorBrush { Color: var color } && color == FluentTheme.Dark.FocusRing && s.Width == 2).ToArray();

    [Fact]
    public void PointerKeyboardPointer_RemovesRingAndDamagesOldPixels()
    {
        var root = new StackPanel { Padding = 20 }; var button = new Button { Width = 100, Height = 32 };
        root.Children.Add(button); using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Surface }; host.UpdateLayout();
        var painter = new ElementPainter(FluentTheme.Dark); host.Focus.Focus(button, FocusOrigin.Pointer);
        Assert.Empty(Rings(painter.BuildFrame(root, Surface).List));
        host.Input.KeyPress(Key.Space); var keyboard = painter.BuildFrame(root, Surface);
        var ring = Assert.Single(Rings(keyboard.List)); Assert.False(keyboard.Damage.IsEmpty);
        host.Focus.NotifyInput(FocusOrigin.Pointer); var pointer = painter.BuildFrame(root, Surface);
        Assert.Empty(Rings(pointer.List)); Assert.False(pointer.Damage.IsEmpty);
        Assert.True(pointer.Damage.Bounds.Left <= ring.Path.Bounds.Left - ring.Width / 2);
        Assert.True(painter.BuildFrame(root, Surface).Damage.IsEmpty); Assert.Equal(0, painter.LastBuildStats.Repainted);
    }

    [Fact]
    public void EditorsAndCompositeShellsUseSingleOwnBorder()
    {
        foreach (Element control in new Element[] { new TextBox(), new PasswordBox(), new NumericUpDown(), new ComboBox(), new DatePicker() })
        {
            using var host = new HeadlessRoot(FluentTest.Skin(control)) { AvailableSize = Surface }; host.UpdateLayout();
            var target = control is DatePicker ? control.Descendants().OfType<TextBox>().First()
                : control is ComboBox ? control.Descendants().OfType<Button>().First() : control;
            host.Focus.Focus(target, FocusOrigin.Pointer);
            var list = new ElementPainter(FluentTheme.Dark).Build(control, Surface);
            Assert.Empty(Rings(list));
            Assert.Contains(list.Commands, c => c is StrokePath { Brush: SolidColorBrush { Color: var stroke } } && stroke == FluentTheme.Dark.Accent ||
                c is FillRoundedRect { Brush: SolidColorBrush { Color: var fill } } && fill == FluentTheme.Dark.Accent);
            Assert.Empty(list.Validate());
        }
    }

    [Fact]
    public void FocusDecorationFollowsChildrenAndRespectsAncestorClip()
    {
        var root = new Border { Width = 120, Height = 70, ClipToBounds = true };
        var button = new Button { Content = new Rectangle { Width = 120, Height = 70, Fill = new(255, 0, 0) } }; root.Content = button;
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Surface }; host.UpdateLayout(); host.Focus.Focus(button);
        var list = new ElementPainter(FluentTheme.Dark).Build(root, Surface); var ring = Assert.Single(Rings(list));
        var index = list.Commands.IndexOf(ring);
        Assert.True(list.Commands.Take(index).OfType<FillRoundedRect>().Any());
        Assert.IsType<PopClip>(list.Commands.Skip(index + 1).First(c => c is not StrokePath)); Assert.Empty(list.Validate());
    }

    [Fact]
    public void RingStyleGeometryAndDamageUseSameDescriptor()
    {
        var root = new StackPanel { Padding = 30 }; var button = new Button { Width = 100, Height = 40 };
        root.Children.Add(button); using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Surface }; host.UpdateLayout(); host.Focus.Focus(button);
        var painter = new ElementPainter(FluentTheme.Dark); painter.BuildFrame(root, Surface);
        Visual.SetFocusRingWidth(button, 6); Visual.SetFocusRingOffset(button, 12); Visual.SetCornerRadius(button, 10);
        var color = new Color(255, 0, 255); Visual.SetFocusRingColor(button, color);
        var frame = painter.BuildFrame(root, Surface); var ring = Assert.Single(frame.List.Commands.OfType<StrokePath>(), s => s.Width == 6 && s.Brush is SolidColorBrush { Color: var c } && c == color);
        Assert.Equal(6, ring.Width); Assert.Equal(18, ring.Path.Bounds.X); Assert.Equal(124, ring.Path.Bounds.Width);
        Assert.True(frame.Damage.Bounds.Left <= 14); Assert.Equal(color, Assert.IsType<SolidColorBrush>(ring.Brush).Color);
        button.FocusVisualMode = FocusVisualMode.None;
        Assert.DoesNotContain(painter.BuildFrame(root, Surface).List.Commands.OfType<StrokePath>(), s => s.Brush is SolidColorBrush { Color: var c } && c == color);
        Assert.True(button.IsFocusVisible);
    }

    [Fact]
    public void NavigationTargetsRowsHeadersAndViewport()
    {
        var tree = new TreeView(); var node = new TreeViewItem { Header = "node", IsExpanded = true };
        node.Children.Add(new TreeViewItem { Header = "child" }); tree.Children.Add(node);
        var tabs = new TabControl(); tabs.Children.Add(new TabItem { Header = "One", Content = new TextBox() }); tabs.Children.Add(new TabItem { Header = "Two", Content = new TextBox() });
        var list = new ListBox { ItemsSource = new[] { "one", "two", "three" }, SelectedIndex = 1 };
        var grid = new DataGrid { Height = 100, ItemsSource = new[] { "one", "two", "three" }, SelectedIndex = 1 };
        grid.Columns.Add(new() { Header = "Value" });
        foreach (var target in new Element[] { node, tabs, list, grid })
        {
            var root = target == node ? tree : target;
            using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Surface }; host.UpdateLayout(); host.UpdateLayout(); host.Focus.Focus(target);
            var painter = new ElementPainter(FluentTheme.Dark); var frame = painter.BuildFrame(root, Surface); var ring = Assert.Single(Rings(frame.List));
            Assert.True(ring.Path.Bounds.Height <= 50, $"{target}: {ring.Path.Bounds}"); Assert.Empty(frame.List.Validate());
            if (target == grid) Assert.Contains(frame.List.Commands, c => c is PushClip p && p.Rect.Height <= 100);
            if (target == tabs)
            {
                tabs.SelectedIndex = 1; host.UpdateLayout(); var changed = painter.BuildFrame(root, Surface);
                Assert.False(changed.Damage.IsEmpty); Assert.NotEqual(ring.Path.Bounds, Assert.Single(Rings(changed.List)).Path.Bounds);
            }
        }
    }

    [Fact]
    public void RetainedCompositionMatchesFullPixelsThroughRingChanges()
    {
        var root = new StackPanel { Padding = 30 }; var button = new Button { Width = 100, Height = 40 };
        root.Children.Add(button); using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Surface }; host.UpdateLayout();
        var painter = new ElementPainter(FluentTheme.Dark);
        using var backend = new Vello.VelloCpuBackend(new Vello.VelloBackendOptions { UseCpu = true, ValidateDisplayList = true });
        var compositor = new RetainedFrameCompositor(backend, (int)Surface.Width, (int)Surface.Height);
        void Compare()
        {
            var frame = painter.BuildFrame(root, Surface); var actual = compositor.Composite(frame.List, frame.Damage);
            var reference = Render.Frame(new ElementPainter(FluentTheme.Dark).Build(root, Surface));
            Assert.Equal(reference.ContentHash(), actual.ContentHash());
        }
        host.Focus.Focus(button, FocusOrigin.Pointer); Compare();
        host.Input.KeyPress(Key.Space); Compare();
        Visual.SetFocusRingOffset(button, 14); Visual.SetFocusRingWidth(button, 6); Visual.SetCornerRadius(button, 12); Compare();
        Visual.SetFocusRingOffset(button, -3); Compare();
        host.Focus.NotifyInput(FocusOrigin.Pointer); Compare();
        host.Focus.AlwaysShowFocusVisuals = true; Compare();
        button.FocusVisualMode = FocusVisualMode.None; Compare();
    }

    [Fact]
    public void KeyboardNavigationMovesRingWhileLogicalFocusStaysOnClickedRow()
    {
        var list = new ListBox { ItemsSource = new[] { "one", "two", "three" } };
        using var host = new HeadlessRoot(FluentTest.Skin(list)) { AvailableSize = Surface }; host.UpdateLayout();
        var row = list.Children[0]; list.Select(0); host.Focus.Focus(row, FocusOrigin.Pointer);
        host.Input.KeyPress(Key.Space); var painter = new ElementPainter(FluentTheme.Dark); var first = painter.BuildFrame(list, Surface);
        host.Input.KeyPress(Key.Down); var next = painter.BuildFrame(list, Surface);
        Assert.Same(row, host.Focus.Focused); Assert.Equal(1, list.SelectedIndex);
        Assert.True(Assert.Single(Rings(next.List)).Path.Bounds.Y > Assert.Single(Rings(first.List)).Path.Bounds.Y);
        Assert.False(next.Damage.IsEmpty);
    }
}
