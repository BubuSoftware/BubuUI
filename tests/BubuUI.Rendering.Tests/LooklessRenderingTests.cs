using BubuUI.Elements;
using Xunit;

namespace BubuUI.Rendering.Tests;

public class LooklessRenderingTests
{
    [Fact]
    public void UnthemedInteractionControlsDoNotProduceAPresetSkin()
    {
        using var root = new StackPanel();
        foreach (var control in Controls())
        {
            control.Width = 160; control.Height = 32;
            root.Children.Add(control);
        }
        using var host = new HeadlessRoot(root) { AvailableSize = new(400, 1000) };
        host.UpdateLayout();
        var display = new ElementPainter(ElementTheme.Dark).Build(root, host.AvailableSize);
        Assert.Empty(display.Validate());
        Assert.DoesNotContain(display.Commands, command => command is FillRoundedRect or StrokePath or FillEllipse);
        Assert.All(root.Children, control => Assert.Null(control.TemplateRoot));
    }

    [Fact]
    public void FluentProvidesTemplatesForTheInteractionControlFamilies()
    {
        foreach (var control in Controls())
        {
            using (control)
            {
                control.SetThemeDefaults(FluentTheme.Light);
                using var host = new HeadlessRoot(control) { AvailableSize = new(400, 300) };
                host.UpdateLayout();
                Assert.True(control.Theme is not null, $"Missing Fluent theme for {control.GetType().Name}.");
                Assert.True(control.TemplateRoot is not null, $"Missing Fluent template for {control.GetType().Name}.");
                Assert.True(control.Theme!.TargetType.IsInstanceOfType(control));
                Assert.Empty(new ElementPainter(FluentTheme.Light).Build(control, host.AvailableSize).Validate());
            }
        }
    }

    private static IEnumerable<Element> Controls() =>
    [
        new Button { Label = "Button" }, new ToggleButton { Label = "Toggle", Checked = true },
        new CheckBox { Label = "Check", Checked = true }, new RadioButton { Label = "Radio", Checked = true },
        new ToggleSwitch { Label = "Switch", Checked = true }, new Slider { Value = 50 },
        new ProgressBar { Value = 50 }, new TextBox { Text = "Text" }, new PasswordBox { Text = "Secret" },
        new NumericUpDown { Value = 50 }, new ComboBox(), new DatePicker(), new Calendar(),
        new DropDownButton { Label = "Drop down" }, new MenuItem { Label = "Menu" },
        new TreeViewItem { Header = "Tree" }, new TreeView(), new TabControl(), new DataGrid(),
        new ListBoxItem { IsSelected = true }, new ItemsControl(), new ScrollViewer(), new Popup(),
    ];
}
