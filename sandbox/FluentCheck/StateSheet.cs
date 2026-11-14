using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Themes.Fluent;

/// <summary>Actual input-driven control states, with no Gallery class styles or hand-painted controls.</summary>
internal static class StateSheet
{
    internal static void Save(string output, FluentTheme theme, ITextPainter text, double scale)
    {
        var rows = Gallery.FluentControlExamples.All;
        string[] states = ["Rest", "Hover", "Pressed", "Disabled", "Keyboard focus"];
        var size = new Size(1116, 120 + rows.Count * 48);
        var list = new DisplayList(size);
        list.Add(new PushTransform(Transform.Scale(scale)));
        list.Add(new FillRect(new(Point.Zero, size), theme.Surface));
        text.Paint(list, "Fluent / Windows 11 control states", 24, new(24, 20), theme.Foreground);
        text.Paint(list, $"{(theme.IsLight ? "Light" : "Dark")}  |  {scale * 100:0}% DPI  |  EclipseUI rendering - not a native WinUI reference", 13, new(24, 56), theme.MutedForeground);
        for (var col = 0; col < states.Length; col++) text.Paint(list, states[col], 14, new(202 + col * 180, 91), theme.MutedForeground);
        for (var row = 0; row < rows.Count; row++)
        {
            text.Paint(list, rows[row].Name, 14, new(24, 130 + row * 48), theme.Foreground);
            for (var col = 0; col < states.Length; col++)
            {
                var root = new Canvas(); var control = rows[row].Create();
                root.Children.Add(control); Canvas.SetLeft(control, 8); Canvas.SetTop(control, 8);
                var painter = new ElementPainter(theme, text); painter.Prepare(root);
                var cellSize = new Size(180, 48);
                using var host = new HeadlessRoot(root) { AvailableSize = cellSize }; host.UpdateLayout();
                var point = new Point(20, 22);
                if (col is 1 or 2) host.Input.PointerMove(point);
                if (col == 2) host.Input.PointerPress(point);
                if (col == 3) control.IsEnabled = false;
                if (col == 4) { host.Focus.AlwaysShowFocusVisuals = true; host.Focus.Focus(control); }
                list.Add(new PushTransform(Transform.Translation(194 + col * 180, 120 + row * 48)));
                list.AddRange(painter.Build(root, cellSize).Commands);
                list.Add(new PopTransform());
            }
        }
        list.Add(new PopTransform());
        using var backend = new VelloCpuBackend(new VelloBackendOptions { UseCpu = true, ValidateDisplayList = true });
        var target = new RenderTarget((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));
        backend.Render(list, target);
        var path = System.IO.Path.Combine(output, $"states-{(theme.IsLight ? "light" : "dark")}-{scale * 100:0}.png");
        PngEncoder.Write(path, target);
        Console.WriteLine($"states: {path}");
    }
}
