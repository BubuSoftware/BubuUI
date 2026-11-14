using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;

namespace Gallery;

/// <summary>Live Fluent controls with theme switching and reset.</summary>
public sealed class FluentGalleryPage : Grid
{
    public Button ThemeButton { get; } = new() { Label = "Theme: System", MinWidth = 144 };
    public Button ResetButton { get; } = new() { Label = "Reset controls" };
    public ScrollViewer Viewer { get; } = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    public IReadOnlyList<Element> LiveControls => _live;
    public string Status => _status.Runs[0].Text;
    private readonly List<Element> _live = [];
    private readonly List<Canvas> _cells = [];
    private readonly TextBlock _status = Text("Ready to explore", 12, muted: true);
    private int _clicks;

    public FluentGalleryPage()
    {
        RowDefinitions = "Auto,*";
        var header = new StackPanel { Padding = 24, Gap = 8 };
        header.Children.Add(Text("Fluent / Windows 11", 28));
        header.Children.Add(Text("Hover, click or press Tab to explore", 14, muted: true));
        var toolbar = new WrapPanel { Gap = 8 };
        toolbar.Children.Add(ThemeButton); toolbar.Children.Add(ResetButton);
        header.Children.Add(toolbar);
        header.Children.Add(_status);
        Children.Add(header);

        var table = new StackPanel { Padding = 24, Gap = 0, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        var headings = Row();
        string[] titles = ["Control", "Interactive"];
        for (var i = 0; i < titles.Length; i++)
        {
            var heading = Text(titles[i], 13, muted: true);
            heading.Margin = 8; Grid.SetColumn(heading, i); headings.Children.Add(heading);
        }
        table.Children.Add(headings);
        foreach (var example in FluentControlExamples.All)
        {
            var row = Row();
            var label = Text(example.Name, 14); label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            var liveCell = new Canvas { Width = 180, Height = 48 };
            Grid.SetColumn(liveCell, 1); row.Children.Add(liveCell); _cells.Add(liveCell);
            table.Children.Add(row);
        }
        Viewer.Children.Add(table); Grid.SetRow(Viewer, 1); Children.Add(Viewer);
        ResetButton.AddHandler(Button.ClickEvent, ResetControls);
        ResetControls();
    }

    private static Grid Row() => new() { ColumnDefinitions = "*,180", Height = 48 };
    private static TextBlock Text(string value, double size, bool muted = false)
    {
        var block = new TextBlock { FontSize = size };
        // A single text run also avoids introducing template whitespace into the specimen labels.
        block.AddRun(value);
        if (muted) Visual.SetForegroundResource(block, new(new(255, 255, 255, 197), new(0, 0, 0, 158)));
        return block;
    }

    public void ResetControls()
    {
        // Dispose removed controls so open popups, focus and event ownership are released.
        foreach (var cell in _cells)
        {
            var previous = cell.Children.ToArray(); cell.Children.Clear();
            foreach (var child in previous) child.Dispose();
        }
        _live.Clear(); _clicks = 0;
        for (var i = 0; i < FluentControlExamples.All.Count; i++)
        {
            var control = FluentControlExamples.All[i].Create();
            Place(_cells[i], control); _live.Add(control);
        }
        ((Button)_live[0]).AddHandler(Button.ClickEvent, () => SetStatus($"Button clicked {++_clicks} time{(_clicks == 1 ? "" : "s")}"));
        foreach (var control in _live)
        {
            control.AddHandler(Element.GotFocusEvent, () => Reveal(control));
            if (control is TextBox editor) editor.AddHandler(TextBox.TextChangedEvent, () => SetStatus($"Text updated: {editor.Text}"));
            if (control is CheckBox check) check.AddHandler(CheckBox.CheckedChangedEvent, () => SetStatus($"{check.GetType().Name}: {(check.IsIndeterminate ? "mixed" : check.Checked ? "on" : "off")}"));
            if (control is ToggleButton toggle) toggle.AddHandler(ToggleButton.CheckedChangedEvent, () => SetStatus($"Toggle button: {(toggle.Checked ? "on" : "off")}"));
            if (control is ComboBox combo) combo.AddHandler(ComboBox.SelectionChangedEvent, () => SetStatus($"Selected: {combo.SelectedItem}"));
        }
        var slider = _live.OfType<Slider>().Single(); var progress = _live.OfType<ProgressBar>().Single();
        slider.AddHandler(RangeControl.ValueChangedEvent, () => { progress.Value = slider.Value; SetStatus($"Value: {slider.Value:0}%"); });
        foreach (var toggle in _live.OfType<ToggleSwitch>())
            toggle.AddHandler(CheckBox.CheckedChangedEvent, () => toggle.Label = toggle.Checked ? "On" : "Off");
        SetStatus("Ready to explore");
    }

    private void SetStatus(string text) => _status.Runs[0].Text = text;
    private void Reveal(Element control)
    {
        var origin = control.PointFromRoot(Point.Zero); var viewport = Viewer.PointFromRoot(Point.Zero);
        var x = viewport.X - origin.X; var y = viewport.Y - origin.Y;
        if (x < 0) Viewer.ScrollToHorizontalOffset(Viewer.HorizontalOffset + x - 8);
        else if (x + control.FinalRect.Width > Viewer.ViewportWidth)
            Viewer.ScrollToHorizontalOffset(Viewer.HorizontalOffset + x + control.FinalRect.Width - Viewer.ViewportWidth + 8);
        if (y < 0) Viewer.ScrollToVerticalOffset(Viewer.VerticalOffset + y - 8);
        else if (y + control.FinalRect.Height > Viewer.ViewportHeight)
            Viewer.ScrollToVerticalOffset(Viewer.VerticalOffset + y + control.FinalRect.Height - Viewer.ViewportHeight + 8);
    }
    private static void Place(Canvas cell, Element control)
    {
        Canvas.SetLeft(control, 8); Canvas.SetTop(control, control is ProgressBar ? 22 : 8);
        cell.Children.Add(control);
    }

}
