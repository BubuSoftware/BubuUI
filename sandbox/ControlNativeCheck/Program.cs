using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Platform.Windows;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using Gallery;

using var shaper = TextShaperFactory.TryCreate();
using var text = shaper is null ? null : new HarfBuzzTextPainter(shaper);
if (text is not null) TextMeasurer.Current = text;
using var window = new Win32Window("EclipseUI control input selfcheck", 500, 280);
using var presenter = VelloSurfaceBackend.Create(window.Handle, window.ClientWidth, window.ClientHeight, new VelloSurfaceOptions { PreferVsync = false, ValidateDisplayList = true });
using var root = new StackPanel { Padding = 16, Gap = 12 };
var editor = new TextBox(); var combo = new ComboBox { ItemsSource = new[] { "one", "two" } };
root.Children.Add(editor); root.Children.Add(combo); root.Children.Add(new ProgressBar { IsIndeterminate = true });
using var host = new WindowHost(window, presenter, root, new WindowHostOptions { TextPainter = text, CloseOnEscape = true, MaxFramesPerSecond = 60 });
int keyUps = 0;
bool completed = false;
editor.AddHandler(Element.KeyUpEvent, _ => keyUps++);
host.FrameTicked += (_, e) =>
{
    if (e.Iteration == 1)
    {
        // Queue focus and text together through the native pointer path, after startup focus messages.
        window.PostPointerClick(new(24 * window.ScaleFactor, 24 * window.ScaleFactor));
        window.PostText("A你😀");
    }
    if (e.Iteration == 4)
    {
        if (editor.Text != "A你😀") throw new InvalidOperationException($"WM_CHAR Unicode bridge: {editor.Text}");
        var glyphs = host.Painter.Build(root, e.LogicalSize, focused: editor).Commands.OfType<DrawGlyphs>().ToArray();
        if (!glyphs.Any(r => r.Font.Name == "msyh.ttc") || !glyphs.Any(r => r.Font.Name == "seguiemj.ttf") || glyphs.SelectMany(r => r.Glyphs.Glyphs).Any(g => g.GlyphId == 0))
            throw new InvalidOperationException("Native input reached Text but did not produce supported CJK/emoji glyphs.");
        GallerySnapshot.SavePng(root, e.LogicalSize, window.ScaleFactor, editor, "out/unicode-native.png", new ElementTheme(), text!);
        window.PostKey(Key.End); window.PostKey(Key.Backspace);
    }
    if (e.Iteration == 7) { if (editor.Text != "A你" || keyUps != 2) throw new InvalidOperationException("Native editing or KeyUp forwarding failed."); combo.IsDropDownOpen = true; }
    if (e.Iteration == 9) window.PostKey(Key.Escape);
    if (e.Iteration == 12)
    {
        if (combo.IsDropDownOpen || window.IsClosed) throw new InvalidOperationException("Escape must close the popup before closing the window.");
        completed = true; window.Close();
    }
};
host.Run(maxFrames: 20);
if (!completed) throw new InvalidOperationException("Native check ended before all assertions ran.");
Console.WriteLine($"native input selfcheck passed: text={editor.Text}, KeyUp={keyUps}, frames={host.RenderedFrames}, backend={presenter.Name}");
