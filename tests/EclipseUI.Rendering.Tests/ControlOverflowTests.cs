using EclipseUI.Elements;
using EclipseUI.Themes.Fluent;
using Xunit;

namespace EclipseUI.Rendering.Tests;

public class ControlOverflowTests
{
    private static readonly Size Surface = new(320, 180);
    private static readonly Color Ink = new(220, 30, 50);
    private const string LongText = "A very long label which must stay inside the control";
    private sealed class StripText : ITextPainter
    {
        public TextLineMetrics Measure(string text, double size) => new(size, size * .4, text.Length * size * .6);
        public void Paint(DisplayList list, string text, double size, Point origin, Color color)
        {
            var m = Measure(text, size);
            list.Add(new FillRect(new(origin, new Size(m.Width, m.Height)), Ink));
        }
    }
    private sealed class OverdrawingElement : Element
    {
        protected override Size MeasureCore(Size available) => new(40, 24);
        protected override void ArrangeCore(Rect final) { }
        public override void Render(IElementDrawingContext context) => context.FillRectangle(new(-30, -20, 200, 120), new(Ink.R, Ink.G, Ink.B));
    }
    public static IEnumerable<object[]> Controls() => new[]
    {
        "Button", "CheckBox", "RadioButton", "ToggleButton", "ToggleSwitch", "TextBox", "PasswordBox",
        "NumericUpDown", "ComboBox", "DatePicker", "MenuItem", "TabControl", "TreeViewItem", "Calendar",
        "ContentControl", "Border", "ListBoxItem", "ListBox", "TreeView", "DataGrid", "Menu", "Custom",
    }.Select(name => new object[] { name });

    private static Border WideBody() => new() { Width = 200, Height = 80, Background = new(Ink.R, Ink.G, Ink.B), BorderThickness = 0 };
    private static Element Make(string name)
    {
        switch (name)
        {
            case "Button": return new Button { Label = LongText };
            case "CheckBox": return new CheckBox { Label = LongText };
            case "RadioButton": return new RadioButton { Label = LongText };
            case "ToggleButton": return new ToggleButton { Label = LongText };
            case "ToggleSwitch": return new ToggleSwitch { Label = LongText };
            case "TextBox": return new TextBox { Text = LongText };
            case "PasswordBox": return new PasswordBox { Text = LongText };
            case "NumericUpDown": return new NumericUpDown { Value = 123456789 };
            case "ComboBox": return new ComboBox { Placeholder = LongText };
            case "DatePicker": return new DatePicker { SelectedDate = new(2026, 11, 14) };
            case "MenuItem": return new MenuItem { Label = LongText };
            case "Calendar": return new Calendar();
            case "Custom": return new OverdrawingElement();
            case "ContentControl": return new ContentControl { Padding = 4, Content = WideBody() };
            case "Border": return new Border { Padding = 4, Content = WideBody() };
            case "TabControl":
                var tabs = new TabControl(); tabs.Children.Add(new TabItem { Header = LongText, Content = WideBody() }); return tabs;
            case "TreeViewItem":
                var branch = new TreeViewItem { Header = LongText, IsExpanded = true }; branch.Children.Add(WideBody()); return branch;
            case "TreeView":
                var tree = new TreeView(); tree.Children.Add(new TreeViewItem { Header = LongText, Width = 200 }); return tree;
            case "ListBoxItem":
                var item = new ListBoxItem(); item.Children.Add(WideBody()); return item;
            case "ListBox": return new ListBox { ItemsSource = new[] { LongText }, ItemTemplate = _ => WideBody() };
            case "Menu":
                var menu = new Menu(); menu.Children.Add(new MenuItem { Label = LongText, Width = 200 }); return menu;
            case "DataGrid":
                var grid = new DataGrid { ItemsSource = new[] { LongText } };
                grid.Columns.Add(new() { Header = LongText, Width = "200", ValueGetter = row => row }); return grid;
            default: throw new ArgumentException(name);
        }
    }

    [Theory]
    [MemberData(nameof(Controls))]
    public void SmallControlsContainTheirDrawingAndContent(string name)
    {
        var root = new Canvas { Width = Surface.Width, Height = Surface.Height };
        var control = Make(name); control.MinWidth = 0; control.MinHeight = 0; control.Width = 32; control.Height = 12;
        Canvas.SetLeft(control, 50); Canvas.SetTop(control, 40); root.Children.Add(control);
        using var host = new HeadlessRoot(root) { AvailableSize = Surface }; host.UpdateLayout();
        var list = new ElementPainter(textPainter: new StripText()).Build(root, Surface);
        Assert.Empty(list.Validate());
        PixelProbe.On(Render.Frame(list), name)
            .NotContains(new(0, 0, 50, 180), Ink)
            .NotContains(new(82, 0, 238, 180), Ink)
            .NotContains(new(50, 0, 32, 40), Ink)
            .NotContains(new(50, 52, 32, 128), Ink)
            .Verify();
    }

    [Theory]
    [InlineData("Button")]
    [InlineData("ContentControl")]
    [InlineData("Border")]
    [InlineData("ListBoxItem")]
    public void OversizedContentCannotPaintOrReceiveInputOutsideItsContentArea(string name)
    {
        var body = WideBody(); Element owner;
        if (name == "Button") owner = new Button { Content = body };
        else if (name == "ListBoxItem") { var item = new ListBoxItem(); item.Children.Add(body); owner = item; }
        else owner = name == "Border" ? new Border { Padding = 4, Content = body } : new ContentControl { Padding = 4, Content = body };
        owner.Width = 70; owner.Height = 40;
        var root = new Canvas { Width = Surface.Width, Height = Surface.Height }; root.Children.Add(owner);
        using var host = new HeadlessRoot(root) { AvailableSize = Surface }; host.UpdateLayout();
        Assert.Same(root, host.Input.HitTest(new(100, 15)));
        Assert.Same(owner, host.Input.HitTest(new(2, 2)));
        Assert.Same(body, host.Input.HitTest(new(20, 15)));
        var list = new ElementPainter().Build(root, Surface);
        PixelProbe.On(Render.Frame(list), name).NotContains(new(70, 0, 250, 180), Ink).Verify();
    }

    [Fact]
    public void OrdinaryLayoutPanelStillAllowsExplicitChildOverflow()
    {
        var parent = new StackPanel { Width = 70, Height = 40 }; var body = WideBody(); parent.Children.Add(body);
        var root = new Canvas { Width = Surface.Width, Height = Surface.Height }; root.Children.Add(parent);
        using var host = new HeadlessRoot(root) { AvailableSize = Surface }; host.UpdateLayout();
        Assert.Same(body, host.Input.HitTest(new(100, 15)));
        PixelProbe.On(Render.Frame(new ElementPainter().Build(root, Surface))).At(100, 15, Ink).Verify();
    }

    [Theory]
    [InlineData("Button", 88)]
    [InlineData("ComboBox", 72)]
    [InlineData("MenuItem", 76)]
    [InlineData("NumericUpDown", 76)]
    public void LabelsStayOutOfPaddingAndReservedIconOrSpinnerAreas(string name, int textRight)
    {
        var control = Make(name); control.Width = 100; control.Height = 40;
        if (control is MenuItem menu) menu.Submenu = new ContextMenu();
        var root = new Canvas { Width = Surface.Width, Height = Surface.Height };
        Canvas.SetLeft(control, 50); Canvas.SetTop(control, 40); root.Children.Add(control);
        using var host = new HeadlessRoot(root) { AvailableSize = Surface }; host.UpdateLayout();
        var list = new ElementPainter(textPainter: new StripText()).Build(root, Surface);
        Assert.Empty(list.Validate());
        PixelProbe.On(Render.Frame(list), name).NotContains(new(50 + textRight, 40, 100 - textRight, 40), Ink).Verify();
    }

    [Fact]
    public void LargeTabHeaderTextDoesNotDrawOverTheSelectedPage()
    {
        var control = new TabControl { Width = 140, Height = 100 };
        control.SetValue(TextBlock.FontSizeProperty, 60); control.Children.Add(new TabItem { Header = LongText });
        var root = new Canvas { Width = Surface.Width, Height = Surface.Height }; root.Children.Add(control);
        using var host = new HeadlessRoot(root) { AvailableSize = Surface }; host.UpdateLayout();
        PixelProbe.On(Render.Frame(new ElementPainter(textPainter: new StripText()).Build(root, Surface)))
            .Contains(new(0, 0, 140, 32), Ink).NotContains(new(0, 32, 140, 68), Ink).Verify();
    }

    [Fact]
    public void LargeTreeHeaderTextDoesNotDrawOverExpandedChildren()
    {
        var control = new TreeViewItem { Header = LongText, IsExpanded = true, Width = 140, Height = 100 };
        control.SetValue(TextBlock.FontSizeProperty, 60); control.Children.Add(new TreeViewItem());
        var root = new Canvas { Width = Surface.Width, Height = Surface.Height }; root.Children.Add(control);
        using var host = new HeadlessRoot(root) { AvailableSize = Surface }; host.UpdateLayout();
        PixelProbe.On(Render.Frame(new ElementPainter(textPainter: new StripText()).Build(root, Surface)))
            .Contains(new(0, 0, 140, 28), Ink).NotContains(new(0, 28, 140, 72), Ink).Verify();
    }

    [Fact]
    public void CalendarDayTextStaysInsideEachNarrowCell()
    {
        var calendar = new Calendar { Width = 140, Height = 224 };
        var root = new Canvas { Width = Surface.Width, Height = 240 }; root.Children.Add(calendar);
        using var host = new HeadlessRoot(root) { AvailableSize = new(320, 240) }; host.UpdateLayout();
        var list = new ElementPainter(textPainter: new StripText()).Build(root, new(320, 240));
        Assert.Empty(list.Validate());
        var probe = PixelProbe.On(Render.Frame(list)).Contains(new(0, 56, 140, 168), Ink);
        for (var column = 0; column < 7; column++) probe.NotContains(new(column * 20 + 18, 56, 2, 168), Ink);
        probe.Verify();
    }

    [Theory]
    [InlineData("Button")]
    [InlineData("Calendar")]
    [InlineData("MenuItem")]
    [InlineData("Custom")]
    public void ZeroSizeControlsCannotLeakDrawing(string name)
    {
        var root = new Canvas { Width = Surface.Width, Height = Surface.Height };
        var control = Make(name); control.MinWidth = 0; control.MinHeight = 0; root.Children.Add(control);
        var painter = new ElementPainter(textPainter: new StripText());
        using var host = new HeadlessRoot(root) { AvailableSize = Surface };
        foreach (var size in new[] { new Size(0, 50), new Size(100, 0) })
        {
            control.Width = size.Width; control.Height = size.Height; host.UpdateLayout();
            var list = painter.Build(root, Surface); Assert.Empty(list.Validate());
            PixelProbe.On(Render.Frame(list)).Count(Ink, 0, 0).Verify();
        }
    }

    [Fact]
    public void KeyboardFocusOutlineRemainsVisibleOutsideTheControl()
    {
        var root = new Canvas { Width = Surface.Width, Height = Surface.Height };
        var button = new Button { Width = 60, Height = 32 }; Canvas.SetLeft(button, 50); Canvas.SetTop(button, 40); root.Children.Add(button);
        using var host = new HeadlessRoot(root) { AvailableSize = Surface }; host.UpdateLayout();
        host.Focus.AlwaysShowFocusVisuals = true; host.Focus.Focus(button);
        var painter = new ElementPainter();
        PixelProbe.On(Render.Frame(painter.Build(root, Surface))).At(48, 56, painter.Theme.FocusRing).Verify();
    }

    [Fact]
    public void PopupEscapesOwnerContentClipAndItsShadowIsDamagedWhenClosed()
    {
        var root = new Canvas { Width = Surface.Width, Height = Surface.Height };
        var owner = new Button { Width = 60, Height = 32 };
        var panel = new StackPanel(); var body = WideBody(); body.Width = 120; body.Height = 50;
        var popup = new Popup { Content = body, PlacementTarget = owner, IsOpen = true };
        panel.Children.Add(popup); owner.Content = panel; root.Children.Add(owner);
        var painter = new ElementPainter(FluentTheme.Light); painter.Prepare(root);
        using var host = new HeadlessRoot(root) { AvailableSize = Surface }; host.UpdateLayout();
        Assert.Same(body, host.Input.HitTest(new(90, 45)));
        var first = painter.BuildFrame(root, Surface); Assert.Empty(first.List.Validate());
        PixelProbe.On(Render.Frame(first.List)).At(90, 45, Ink).Verify();
        body.Width = 160; host.UpdateLayout();
        var moved = painter.BuildFrame(root, Surface);
        var reference = new ElementPainter(FluentTheme.Light).Build(root, Surface);
        var shown = Render.Frame(moved.List);
        Assert.Equal(Render.Frame(reference).ContentHash(), shown.ContentHash());
        var origin = body.PointFromRoot(Point.Zero);
        var bottom = -origin.Y + body.FinalRect.Height;
        var shadow = new Rect(-origin.X + 10, bottom + 1, 80, 3);
        Assert.False(painter.BuildFrame(root, Surface).Damage.IsFull);
        popup.Close(); host.UpdateLayout();
        var closed = painter.BuildFrame(root, Surface);
        Assert.True(closed.Damage.Bounds.Bottom >= shadow.Bottom);
        PixelProbe.On(shown).SameAsOutside(Render.Frame(closed.List), closed.Damage.Bounds).Verify();
        Assert.Same(root, host.Input.HitTest(new(90, 45)));
    }
}
