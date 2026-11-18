using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Text.Shaping;
using EclipseUI.Themes.Fluent;
using Gallery;
using Path = System.IO.Path;

var output = args.FirstOrDefault() ?? "out/fluent";
Directory.CreateDirectory(output);
using var shaper = TextShaperFactory.TryCreate();
using var textPainter = shaper is null ? null : new HarfBuzzTextPainter(shaper);
ITextPainter text = textPainter is null ? new MeasuringTextPainter() : textPainter;
if (textPainter is not null) TextMeasurer.Current = textPainter;

foreach (var theme in new[] { FluentTheme.Light, FluentTheme.Dark })
{
    var mode = theme.IsLight ? "light" : "dark";
    using var catalog = new FluentGalleryPage { Width = 720 }; catalog.EnsureInitialized();
    var painter = new ElementPainter(theme, text); painter.Prepare(catalog);
    using var host = new HeadlessRoot(catalog) { AvailableSize = new(720, 960) };
    host.UpdateLayout();
    var size = new Size(720, 960);
    host.AvailableSize = size; host.UpdateLayout();
    Save($"controls-{mode}.png");
    var toggle = catalog.LiveControls.OfType<ToggleSwitch>().First(); toggle.Checked = true;
    var combo = catalog.LiveControls.OfType<ComboBox>().Single(); combo.SelectedIndex = 1; combo.IsDropDownOpen = true;
    host.UpdateLayout(); Save($"selection-popup-{mode}.png");
    combo.IsDropDownOpen = false; host.UpdateLayout();
    var button = catalog.LiveControls.OfType<Button>().First();
    host.Focus.AlwaysShowFocusVisuals = true; host.Focus.Focus(button); Save($"focus-{mode}.png");
    host.Focus.ClearFocus();
    var point = button.PointFromRoot(Point.Zero);
    point = new(-point.X + 5, -point.Y + 5);
    host.Input.PointerMove(point); Save($"hover-{mode}.png");
    host.Input.PointerPress(point); Save($"pressed-{mode}.png");
    host.Input.CancelPointerCapture(); button.IsEnabled = false; Save($"disabled-{mode}.png");
    foreach (var scale in new[] { 1d, 1.25, 1.5, 2d }) StateSheet.Save(output, theme, text, scale);
    void Save(string name)
    {
        var frame = painter.BuildFrame(catalog, size);
        if (frame.List.Validate().Count != 0) throw new InvalidOperationException("Invalid Fluent display list.");
        GallerySnapshot.SavePng(catalog, size, 1, Path.Combine(output, name), theme, text);
    }
}
Console.WriteLine("Fluent light/dark control, selection, popup, focus and pointer-state snapshots passed.");
