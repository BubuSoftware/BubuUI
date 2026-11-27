using System.Runtime.InteropServices;
using BubuUI.Elements;
using BubuUI.Rendering;
using BubuUI.Rendering.Vello;
using BubuUI.Text.Shaping;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BubuUI.Platform.Linux.Tests;

public sealed class LinuxDesktopFactAttribute : FactAttribute
{
    public LinuxDesktopFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Requires a Linux desktop (X11 or Wayland).";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                 string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            Skip = "Requires DISPLAY or WAYLAND_DISPLAY. Use xvfb-run for CI.";
    }
}

public class NativeDesktopTests
{
    [LinuxDesktopFact]
    public void SdlWindowPresentsResizesAndRoutesUtf8InputThroughNativeQueue()
    {
        using var window = new LinuxWindow("BubuUI Linux smoke", 400, 300);
        window.Show();
        Assert.Contains(window.VideoDriver, new[] { "x11", "wayland" });
        window.Clipboard.SetText("Linux 剪贴板 😀");
        Assert.Equal("Linux 剪贴板 😀", window.Clipboard.GetText());
        using var presenter = VelloSurfaceBackend.Create(window.SurfaceHandle,
            Math.Max(1, window.ClientWidth), Math.Max(1, window.ClientHeight));
        using var editor = new TextBox();
        using var host = new WindowHost(window, presenter, editor, new() { AlwaysRender = true });
        var pointer = Marshal.StringToCoTaskMemUTF8("中文 😀");
        try
        {
            host.FrameTicked += (_, e) =>
            {
                if (e.Iteration != 1) return;
                host.Input.Focus.Focus(editor);
                window.SetTextInputEnabled(true);
                // Raw keys must still type when older SDL cannot activate Wayland IME.
                // Normal SDL input uses text events, so supply the corresponding commit
                // on backends with native text input. Both paths insert exactly once.
                var letter = new SdlNative.Event
                {
                    Key = new() { Type = SdlNative.KeyDown, WindowId = window.Id, Key = 'A', Mod = 1, Down = 1 },
                };
                SdlNative.Check(SdlNative.SDL_PushEvent(ref letter));
                var space = new SdlNative.Event
                {
                    Key = new() { Type = SdlNative.KeyDown, WindowId = window.Id, Scancode = 44, Key = ' ', Down = 1 },
                };
                SdlNative.Check(SdlNative.SDL_PushEvent(ref space));
                var shortcut = new SdlNative.Event
                {
                    Key = new() { Type = SdlNative.KeyDown, WindowId = window.Id, Key = 'c', Mod = 0x40, Down = 1 },
                };
                SdlNative.Check(SdlNative.SDL_PushEvent(ref shortcut));
                if (!window.UsesBasicTextInput)
                {
                    var latin = Marshal.StringToCoTaskMemUTF8("A ");
                    try
                    {
                        var commit = new SdlNative.Event { Text = new() { Type = SdlNative.TextInput, WindowId = window.Id, Text = latin } };
                        SdlNative.Check(SdlNative.SDL_PushEvent(ref commit));
                        window.PumpMessages();
                    }
                    finally { Marshal.FreeCoTaskMem(latin); }
                }
                // Feed the real SDL queue; PumpMessages must decode this UTF-8 pointer.
                var text = new SdlNative.Event
                {
                    Text = new() { Type = SdlNative.TextInput, WindowId = window.Id, Text = pointer },
                };
                SdlNative.Check(SdlNative.SDL_PushEvent(ref text));
                window.SetTitle("BubuUI Linux 已更新");
                window.SetClientSize(500, 360);
            };
            Assert.Equal(4, host.Run(4));
            Assert.Equal("A 中文 😀", editor.Text);
            Assert.True(presenter.PresentedFrames >= 4);
            Assert.True(presenter.Width > 0 && presenter.Height > 0);
            Assert.True(window.ScaleFactor > 0);
            Assert.Equal((window.ClientWidth, window.ClientHeight), (presenter.Width, presenter.Height));
            Assert.InRange(window.LogicalClientSize.Width, 499, 501);
            Assert.InRange(window.LogicalClientSize.Height, 359, 361);
        }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    [LinuxDesktopFact]
    public void LinuxBuilderRunsFullLifetimeAndDefaultFontsDrawCjkAndEmoji()
    {
        var app = AppBuilder.Configure<NativeApp>().UseLinux().UseVello().UseHarfBuzzText().Build();
        var previousMeasurer = TextMeasurer.Current;
        using var services = (IDisposable)app.Services;
        try
        {
            var painter = app.Services.GetRequiredService<ITextPainter>();
            var list = new DisplayList(new(500, 90));
            painter.Paint(list, "Hello 中文 😀", 28, new(8, 8), Color.Black);
            Assert.All(list.Commands.OfType<DrawGlyphs>().SelectMany(run => run.Glyphs.Glyphs),
                glyph => Assert.NotEqual(0, glyph.GlyphId));
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

    public sealed class NativeApp : Application
    {
        public override void OnFrameworkInitializationCompleted()
            => MainWindow = new Window { Title = "Linux lifetime smoke", Width = 320, Height = 240,
                Content = new TextBox { Text = "Linux 中文 😀" }, MaxTicks = 3 };
    }
}
