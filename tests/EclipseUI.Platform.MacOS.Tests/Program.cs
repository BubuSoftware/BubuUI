using System.Runtime.InteropServices;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using EclipseUI.Text.Shaping;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EclipseUI.Platform.MacOS.Tests;

internal static class Program
{
    // Deliberately synchronous: awaiting before AppKit initialization may change the OS thread.
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS() || args is not ["--native-smoke", var scenario]) return 2;
        try
        {
            switch (scenario)
            {
                case "window": WindowScenario(); break;
                case "live-resize": LiveResizeScenario(); break;
                case "lifetime": LifetimeScenario(); break;
                case "thread": ThreadScenario(); break;
                default: throw new ArgumentException("Unknown native scenario.");
            }
            Console.WriteLine($"macOS native {scenario}: passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void LiveResizeScenario()
    {
        using var window = new MacOSWindow("Cocoa live resize", 500, 360);
        using var other = new MacOSWindow("Other window", 200, 100);
        using var presenter = VelloSurfaceBackend.Create(window.SurfaceHandle,
            Math.Max(1, window.ClientWidth), Math.Max(1, window.ClientHeight));
        using var root = new StackPanel();
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.AddRun(string.Join(" ", Enumerable.Repeat("live resize content", 30)));
        root.Children.Add(text);
        using var host = new WindowHost(window, presenter, root);
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            var lines = text.TextLines.Count;
            foreach (var width in new[] { 240, 420, 300 })
            {
                window.SetClientSize(width, 360);
                var before = presenter.PresentedFrames;
                // SDL's Cocoa tracking timer emits this event inside its nested loop.
                // Do not poll: the watcher must finish the frame before PushEvent returns.
                var expose = new SdlNative.Event { Type = SdlNative.WindowExposed, WindowId = window.Id };
                SdlNative.Check(SdlNative.SDL_PushEvent(ref expose));
                Assert.True(presenter.PresentedFrames > before);
                Assert.Equal(window.LogicalClientSize, root.FinalRect.Size);
                Assert.Equal((window.ClientWidth, window.ClientHeight), (presenter.Width, presenter.Height));
                Assert.Equal(1, host.Ticks);
                if (width == 240) Assert.True(text.TextLines.Count > lines);
            }
            var previous = presenter.PresentedFrames;
            var unrelated = new SdlNative.Event { Type = SdlNative.WindowExposed, WindowId = other.Id };
            SdlNative.Check(SdlNative.SDL_PushEvent(ref unrelated));
            Assert.Equal(previous, presenter.PresentedFrames);
            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    var expose = new SdlNative.Event { Type = SdlNative.WindowExposed, WindowId = window.Id };
                    SdlNative.Check(SdlNative.SDL_PushEvent(ref expose));
                }
                catch (Exception error) { failure = error; }
            });
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
            Assert.Equal(previous, presenter.PresentedFrames);
        };
        host.Run(2);
    }

    private static void WindowScenario()
    {
        using var window = new MacOSWindow("EclipseUI macOS smoke", 400, 300);
        window.Show();
        Assert.Equal("cocoa", window.VideoDriver);
        Assert.Equal(NativeWindowKind.AppKit, window.SurfaceHandle.Kind);
        Assert.NotEqual((nint)0, window.SurfaceHandle.Handle);
        var previousClipboard = window.Clipboard.GetText();
        try
        {
            window.Clipboard.SetText("macOS 剪贴板 😀");
            Assert.Equal("macOS 剪贴板 😀", window.Clipboard.GetText());
            using var presenter = VelloSurfaceBackend.Create(window.SurfaceHandle,
                Math.Max(1, window.ClientWidth), Math.Max(1, window.ClientHeight));
            Assert.Equal(VelloSharp.WgpuBackend.Metal, presenter.BackendType);
            using var editor = new TextBox();
            using var host = new WindowHost(window, presenter, editor, new() { AlwaysRender = true });
            host.FrameTicked += (_, e) =>
            {
                if (e.Iteration == 1)
                {
                    host.Input.Focus.Focus(editor);
                    window.SetTextInputEnabled(true);
                    PushText(window, SdlNative.TextInput, "A ");
                    var compositions = new List<CompositionStage>();
                    var typed = new List<string>();
                    window.TextComposition += (_, message) => compositions.Add(message.Stage);
                    window.CharacterTyped += (_, message) => typed.Add(message.Text);
                    // Cocoa clears marked text immediately before committing the same text.
                    PushComposition(window, "zhong", "中文 😀");
                    Assert.Equal(new[] { CompositionStage.Started, CompositionStage.Updated, CompositionStage.Committed }, compositions);
                    Assert.Empty(typed);
                    PushText(window, SdlNative.TextEditing, "cancelled");
                    PushText(window, SdlNative.TextEditing, "");
                    Assert.Equal(CompositionStage.Cancelled, compositions[^1]);
                    Assert.Equal("A 中文 😀", editor.Text);
                    window.SetTitle("EclipseUI macOS 已更新");
                    window.SetClientSize(500, 360);
                }
                if (e.Iteration == 2)
                {
                    // Preserve the native Command modifier through SDL, host routing and TextBox.
                    var selectAll = new SdlNative.Event
                    {
                        Key = new() { Type = SdlNative.KeyDown, WindowId = window.Id, Key = 'a', Mod = 0x0400, Down = 1 },
                    };
                    SdlNative.Check(SdlNative.SDL_PushEvent(ref selectAll));
                    window.PumpMessages();
                    PushText(window, SdlNative.TextInput, "替换 ✅");
                }
            };
            Assert.True(host.Run(4) >= 4); // Native callbacks can present between outer ticks.
            Assert.Equal("替换 ✅", editor.Text);
            Assert.True(presenter.PresentedFrames >= 4);
            Assert.True(window.ScaleFactor >= 1);
            Assert.Equal((window.ClientWidth, window.ClientHeight), (presenter.Width, presenter.Height));
            Assert.InRange(window.LogicalClientSize.Width, 499, 501);
            Assert.InRange(window.LogicalClientSize.Height, 359, 361);
        }
        finally { window.Clipboard.SetText(previousClipboard); }
    }

    private static void PushText(MacOSWindow window, uint type, string text)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            var message = new SdlNative.Event { Text = new() { Type = type, WindowId = window.Id, Text = pointer } };
            SdlNative.Check(SdlNative.SDL_PushEvent(ref message));
            window.PumpMessages();
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    private static void PushComposition(MacOSWindow window, string preedit, string committed)
    {
        var pointers = new[] { preedit, "", committed }.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        try
        {
            for (var index = 0; index < pointers.Length; index++)
            {
                var message = new SdlNative.Event
                {
                    Text = new() { Type = index == 2 ? SdlNative.TextInput : SdlNative.TextEditing,
                        WindowId = window.Id, Text = pointers[index] },
                };
                SdlNative.Check(SdlNative.SDL_PushEvent(ref message));
            }
            window.PumpMessages();
        }
        finally { foreach (var pointer in pointers) Marshal.FreeCoTaskMem(pointer); }
    }

    private static void LifetimeScenario()
    {
        var previousMeasurer = TextMeasurer.Current;
        var app = AppBuilder.Configure<NativeApp>().UseMacOS().UseVello().UseHarfBuzzText().Build();
        using var services = (IDisposable)app.Services;
        try
        {
            var painter = app.Services.GetRequiredService<ITextPainter>();
            var list = new DisplayList(new(500, 90));
            painter.Paint(list, "Hello 中文 😀", 28, new(8, 8), Color.Black);
            var glyphs = list.Commands.OfType<DrawGlyphs>().SelectMany(run => run.Glyphs.Glyphs).ToArray();
            Assert.NotEmpty(glyphs);
            Assert.All(glyphs, glyph => Assert.NotEqual(0, glyph.GlyphId));
            using var cpu = new VelloCpuBackend();
            var target = new RenderTarget(500, 90);
            cpu.Render(list, target);
            Assert.True(target.CountPixelsDifferentFrom(Color.Transparent) > 100);
            Assert.Equal(0, app.Services.GetRequiredService<IApplicationLifetime>().Run(app, []));
            Assert.True(app.MainWindow!.IsDisposed);
            Assert.Null(app.Host);
        }
        finally
        {
            app.MainWindow?.Dispose();
            TextMeasurer.Current = previousMeasurer;
        }
    }

    private static void ThreadScenario()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { using var invalid = new MacOSWindow("Invalid worker window", 200, 100); }
            catch (Exception error) { failure = error; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(failure);

        using var window = new MacOSWindow("Main-thread ownership", 200, 100);
        failure = null;
        thread = new Thread(() =>
        {
            try { window.PumpMessages(); }
            catch (Exception error) { failure = error; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(failure);
    }

    public sealed class NativeApp : Application
    {
        public override void OnFrameworkInitializationCompleted()
            => MainWindow = new Window { Title = "macOS lifetime smoke", Width = 320, Height = 240,
                Content = new TextBox { Text = "macOS 中文 😀" }, MaxTicks = 3 };
    }
}
