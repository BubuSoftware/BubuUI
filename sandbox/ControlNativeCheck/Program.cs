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
using var window = new Win32Window("EclipseUI control input selfcheck", 500, 430);
using var presenter = VelloSurfaceBackend.Create(window.Handle, window.ClientWidth, window.ClientHeight, new VelloSurfaceOptions { PreferVsync = false, ValidateDisplayList = true });
using var root = new StackPanel { Padding = 16, Gap = 12 };
var editor = new TextBox(); var combo = new ComboBox { ItemsSource = new[] { "one", "two" } };
var button = new Button { Label = "Pointer / keyboard focus" };
var scroll = new ScrollViewer { Height = 120, Padding = 8 }; scroll.Children.Add(new Border { Height = 2000 });
root.Children.Add(editor); root.Children.Add(combo); root.Children.Add(button); root.Children.Add(scroll); root.Children.Add(new ProgressBar { IsIndeterminate = true });
using var host = new WindowHost(window, presenter, root, new WindowHostOptions { TextPainter = text, CloseOnEscape = true, MaxFramesPerSecond = 60 });
int keyUps = 0;
int buttonClicks = 0;
double firstPage = 0, releasedOffset = 0, cancelledOffset = 0;
Point trackPoint = default;
bool completed = false;
button.AddHandler(Button.ClickEvent, _ => buttonClicks++);
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
        var glyphs = host.Painter.Build(root, e.LogicalSize).Commands.OfType<DrawGlyphs>().ToArray();
        if (!glyphs.Any(r => r.Font.Name == "msyh.ttc") || !glyphs.Any(r => r.Font.Name == "seguiemj.ttf") || glyphs.SelectMany(r => r.Glyphs.Glyphs).Any(g => g.GlyphId == 0))
            throw new InvalidOperationException("Native input reached Text but did not produce supported CJK/emoji glyphs.");
        GallerySnapshot.SavePng(root, e.LogicalSize, window.ScaleFactor, "out/unicode-native.png", new ElementTheme(), text!);
        window.PostKey(Key.End); window.PostKey(Key.Backspace);
    }
    if (e.Iteration == 7) { if (editor.Text != "A你" || keyUps != 2) throw new InvalidOperationException("Native editing or KeyUp forwarding failed."); combo.IsDropDownOpen = true; }
    if (e.Iteration == 9) window.PostKey(Key.Escape);
    if (e.Iteration == 12)
    {
        if (combo.IsDropDownOpen || window.IsClosed) throw new InvalidOperationException("Escape must close the popup before closing the window.");
        if (!editor.IsFocusVisible || host.Input.Focus.LastFocusOrigin != FocusOrigin.Restore) throw new InvalidOperationException("Escape focus restoration failed.");
        ClickButton();
    }
    if (e.Iteration == 14)
    {
        if (!button.IsFocused || button.IsFocusVisible || buttonClicks != 1) throw new InvalidOperationException("Native pointer focus or click was lost.");
        window.PostKey(Key.Space);
    }
    if (e.Iteration == 16)
    {
        if (!button.IsFocusVisible || buttonClicks != 2) throw new InvalidOperationException("Native keyboard input should show the outline on the same button.");
        GallerySnapshot.SavePng(root, e.LogicalSize, window.ScaleFactor, "out/focus-native.png", new ElementTheme(), text!);
        combo.IsDropDownOpen = true;
    }
    if (e.Iteration == 18)
    {
        var outside = new Point(e.LogicalSize.Width - 8, e.LogicalSize.Height - 8);
        if (combo.Descendants().OfType<Popup>().Single().ContainsRootPoint(outside)) throw new InvalidOperationException("Outside-click probe landed inside popup.");
        window.PostPointerClick(new(outside.X * window.ScaleFactor, outside.Y * window.ScaleFactor));
    }
    if (e.Iteration == 21)
    {
        if (combo.IsDropDownOpen || !button.IsFocused || button.IsFocusVisible) throw new InvalidOperationException("Outside click must restore pointer focus.");
        scroll.TryGetScrollBarGeometry(false, out var geometry);
        window.PostPointerPress(DevicePoint(scroll.FinalRect.Width - 2, geometry.Thumb.Top + geometry.Thumb.Height / 2));
    }
    if (e.Iteration == 23)
    {
        if (!ReferenceEquals(host.Input.Captured, scroll) || scroll.VerticalOffset != 0) throw new InvalidOperationException("Thumb padding did not start a native drag.");
        window.PostPointerMove(new((e.LogicalSize.Width - 18) * window.ScaleFactor, (e.LogicalSize.Height + 50) * window.ScaleFactor));
    }
    if (e.Iteration == 25)
    {
        if (scroll.VerticalOffset != scroll.ScrollableHeight) throw new InvalidOperationException("Native drag outside the window did not reach the scroll limit.");
        window.PostPointerRelease(new((e.LogicalSize.Width - 18) * window.ScaleFactor, (e.LogicalSize.Height + 50) * window.ScaleFactor));
    }
    if (e.Iteration == 27)
    {
        if (host.Input.Captured is not null || scroll.IsPressed) throw new InvalidOperationException("Native release did not clear scrollbar capture.");
        scroll.ScrollToHome(); trackPoint = DevicePoint(scroll.FinalRect.Width - 2, scroll.FinalRect.Height - 12);
        window.PostPointerPress(trackPoint);
    }
    if (e.Iteration == 29) firstPage = scroll.VerticalOffset;
    if (e.Iteration == 60)
    {
        if (firstPage <= 0 || scroll.VerticalOffset <= firstPage) throw new InvalidOperationException("Holding the native track did not auto-repeat.");
        window.PostPointerRelease(trackPoint);
    }
    if (e.Iteration == 62)
    {
        if (host.Input.Captured is not null || scroll.IsPressed) throw new InvalidOperationException("Track release did not end auto-repeat.");
        releasedOffset = scroll.VerticalOffset;
    }
    if (e.Iteration == 64)
    {
        if (scroll.VerticalOffset != releasedOffset) throw new InvalidOperationException("Track scrolling continued after release.");
        scroll.ScrollToHome(); window.PostPointerPress(trackPoint);
    }
    if (e.Iteration == 66)
    {
        if (!ReferenceEquals(host.Input.Captured, scroll)) throw new InvalidOperationException("Native cancellation check has no capture.");
        NativeCapture.ReleaseCapture(); cancelledOffset = scroll.VerticalOffset;
        if (host.Input.Captured is not null || scroll.IsPressed) throw new InvalidOperationException("WM_CAPTURECHANGED did not clear runtime capture.");
    }
    if (e.Iteration == 95)
    {
        if (scroll.VerticalOffset != cancelledOffset) throw new InvalidOperationException("Scrolling continued after native capture was lost.");
        GallerySnapshot.SavePng(root, e.LogicalSize, window.ScaleFactor, "out/scrollbar-native.png", new ElementTheme(), text!);
        completed = true; window.Close();
    }
    void ClickButton()
    {
        var p = button.PointFromRoot(Point.Zero);
        window.PostPointerClick(new((-p.X + 8) * window.ScaleFactor, (-p.Y + 8) * window.ScaleFactor));
    }
    Point DevicePoint(double x, double y)
    {
        var p = scroll.PointFromRoot(Point.Zero); return new((x - p.X) * window.ScaleFactor, (y - p.Y) * window.ScaleFactor);
    }
};
host.Run(maxFrames: 100);
if (!completed) throw new InvalidOperationException("Native check ended before all assertions ran.");
Console.WriteLine($"native input selfcheck passed: text={editor.Text}, KeyUp={keyUps}, frames={host.RenderedFrames}, backend={presenter.Name}");

static class NativeCapture
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ReleaseCapture();
}
