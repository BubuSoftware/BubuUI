using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BubuUI;
using BubuUI.Elements;
using BubuUI.Platform.Windows;
using BubuUI.Rendering.Vello;
using BubuUI.Text.Shaping;
using Gallery;

[assembly: SupportedOSPlatform("windows")]

if (!OperatingSystem.IsWindows()) return 0;
using var shaper = TextShaperFactory.TryCreate();
using var text = shaper is null ? null : new HarfBuzzTextPainter(shaper);
if (text is not null) TextMeasurer.Current = text;
using var window = new Win32Window("BubuUI live resize selfcheck", 720, 480);
using var presenter = VelloSurfaceBackend.Create(window.Handle, window.ClientWidth, window.ClientHeight,
    new VelloSurfaceOptions { PreferVsync = false, ValidateDisplayList = true });
using var root = new StackPanel { Padding = 16, Gap = 12 };
var heading = new TextBlock { FontSize = 18, TextWrapping = TextWrapping.Wrap };
heading.AddRun("Drag resizing rebuilds layout immediately: 中文和 emoji 😀 stay at the same font size while lines and control positions follow the available width.");
var buttons = new WrapPanel { Gap = 8 };
for (var i = 0; i < 16; i++) buttons.Children.Add(new Button { Width = 96, Height = 32, Label = $"Button {i + 1}" });
root.Children.Add(heading); root.Children.Add(buttons);
using var host = new WindowHost(window, presenter, root,
    new WindowHostOptions { TextPainter = text, CloseOnEscape = false, MaxFramesPerSecond = 60 });

int step = 0, outerTick = 0;
var rows = new List<int>(); var lines = new List<int>();
var sizes = new[] { 320, 640, 420 };
var escaped = false;
window.LiveResizeFrameRequested += (_, _) =>
{
    if (step >= sizes.Length) return;
    Check(window.IsInSizeMove, "timer ran outside the native move/size loop");
    Check(host.Ticks == outerTick, "the outer frame loop ran during the native modal loop");
    var before = presenter.PresentedFrames;
    var width = sizes[step];
    window.SetClientSize(width, 480);
    Check(presenter.PresentedFrames > before, "resize did not present before WM_SIZE returned");
    Check(presenter.Width == window.ClientWidth && presenter.Height == window.ClientHeight, "surface does not match client pixels");
    Check(root.FinalRect.Size == window.LogicalClientSize, "root layout does not match the new client size");
    Check(buttons.Children.All(button => button.FinalRect.Width == 96), "fixed controls were scaled instead of rearranged");
    Check(heading.FontSize == 18, "text font size changed during resizing");
    rows.Add(buttons.Children.Select(button => button.FinalRect.Y).Distinct().Count());
    lines.Add(heading.TextLines.Count);
    if (text is not null)
        GallerySnapshot.SavePng(root, window.LogicalClientSize, window.ScaleFactor,
            $"out/live-resize-{width}.png", host.Painter.Theme, text);
    Console.WriteLine($"live resize: width={window.ClientWidth}, rows={rows[^1]}, lines={lines[^1]}, outer-tick={host.Ticks}, presented={presenter.PresentedFrames}");
    step++;
    if (step == sizes.Length) window.PostKey(Key.Escape); // Exit the real DefWindowProc sizing loop.
};
host.FrameTicked += (_, e) =>
{
    if (e.Iteration == 1)
    {
        outerTick = host.Ticks;
        // SC_SIZE + WMSZ_BOTTOMRIGHT enters the same modal loop as dragging a sizing border.
        Native.PostMessageW(window.Handle, 0x0112, 0xF008, 0);
    }
    if (step == sizes.Length && !window.IsInSizeMove) { escaped = true; window.Close(); }
};

using var stop = new CancellationTokenSource();
var timeout = Task.Run(async () =>
{
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stop.Token);
        Native.PostMessageW(window.Handle, 0x001F, 0, 0); // Cancel only this selfcheck's move/size loop.
        Native.PostMessageW(window.Handle, 0x0010, 0, 0);
    }
    catch (OperationCanceledException) { }
});
try { host.Run(30); }
finally { stop.Cancel(); timeout.GetAwaiter().GetResult(); }
Check(step == sizes.Length && escaped, "native sizing loop did not complete all steps and exit");
Check(rows[0] > rows[1] && lines[0] > lines[1], "content did not reflow between narrow and wide client sizes");
Console.WriteLine($"live resize selfcheck passed: rows={string.Join(',', rows)}, lines={string.Join(',', lines)}, backend={presenter.Name}");
return 0;

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
internal static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
