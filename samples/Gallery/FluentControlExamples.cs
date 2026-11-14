using EclipseUI;
using EclipseUI.Elements;

namespace Gallery;

/// <summary>One set of real control factories for the interactive gallery and state snapshots.</summary>
public static class FluentControlExamples
{
    public sealed record Example(string Name, Func<Element> Create);
    public static IReadOnlyList<Example> All { get; } = new Example[]
    {
        new("Button", () => new Button { Label = "Standard button", Width = 160 }),
        new("Toggle button / on", () => new ToggleButton { Label = "Selected", Checked = true, Width = 160 }),
        new("TextBox", () => new TextBox { Text = "Editable text", Width = 160 }),
        new("Placeholder", () => new TextBox { Placeholder = "Enter a value", Width = 160 }),
        new("ComboBox", () => new ComboBox { ItemsSource = new[] { "Alpha", "Beta", "Gamma" }, SelectedIndex = 0, Width = 160 }),
        new("CheckBox / off", () => new CheckBox { Label = "Option" }),
        new("CheckBox / on", () => new CheckBox { Label = "Option", Checked = true }),
        new("Indeterminate", () => new CheckBox { Label = "Option", IsIndeterminate = true, IsThreeState = true }),
        new("RadioButton / off", () => new RadioButton { Label = "Option", GroupName = "gallery-options" }),
        new("RadioButton / on", () => new RadioButton { Label = "Option", GroupName = "gallery-options", Checked = true }),
        new("ToggleSwitch / off", () => new ToggleSwitch { Label = "Off" }),
        new("ToggleSwitch / on", () => new ToggleSwitch { Label = "On", Checked = true }),
        new("Slider", () => new Slider { Width = 160, Value = 40 }),
        new("ProgressBar", () => new ProgressBar { Width = 160, Value = 40 }),
    };
}
