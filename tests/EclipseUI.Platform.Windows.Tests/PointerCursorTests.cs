using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class PointerCursorTests
{
    [WindowsFact]
    public void NativeCursorTracksHoverAndCaptureAndSurvivesSetCursorMessages()
    {
        var previous = GetCursor();
        try
        {
            using var window = new Win32Window("Cursor test", 320, 180);
            using var root = new Grid();
            var grip = new Border { Width = 8, HorizontalAlignment = HorizontalAlignment.Left, Cursor = PointerCursor.ResizeHorizontal };
            root.Children.Add(grip);
            using var presenter = new Presenter();
            using var host = new WindowHost(window, presenter, root);
            root.Measure(new(320, 180));
            root.Arrange(new(0, 0, 320, 180));
            var resize = LoadCursorW(0, 32644);
            var arrow = LoadCursorW(0, 32512);
            Assert.NotEqual(0, resize);

            Move(4, 10);
            Assert.Equal(resize, GetCursor());
            Assert.Equal((nint)1, Native.SendMessageW(window.Handle, 0x0020, window.Handle, 1)); // WM_SETCURSOR, HTCLIENT
            Assert.Equal(resize, GetCursor());
            host.Input.PointerPress(new(4, 10));
            Move(100, 50);
            Assert.Equal(resize, GetCursor());
            Native.SendMessageW(window.Handle, 0x0202, 0, Position(100, 50)); // WM_LBUTTONUP
            Assert.Equal(arrow, GetCursor());

            Move(4, 10);
            host.Input.PointerPress(new(4, 10));
            Native.SendMessageW(window.Handle, 0x0215, 0, 0); // WM_CAPTURECHANGED
            Move(100, 50);
            Assert.Equal(arrow, GetCursor());
            grip.IsEnabled = false;
            Move(4, 10);
            Assert.Equal(arrow, GetCursor());

            void Move(int x, int y) => Native.SendMessageW(window.Handle, 0x0200, 0, Position(x, y));
            nint Position(int x, int y) => (nint)(((int)(y * window.ScaleFactor) << 16) | (int)(x * window.ScaleFactor));
        }
        finally { SetCursor(previous); }
    }

    private sealed class Presenter : ISurfacePresenter
    {
        public string Name => "cursor-test";
        public string Description => Name;
        public RenderCapabilities Capabilities => RenderCapabilities.All;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsVsync => false;
        public void Resize(int width, int height) { Width = width; Height = height; }
        public void Render(DisplayList list) { }
        public void Dispose() { }
    }

    [DllImport("user32.dll")] private static extern nint GetCursor();
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint instance, nint name);
}
