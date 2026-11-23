using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace EclipseUI.Platform.Windows.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires native Win32 windows."; }
}

[SupportedOSPlatform("windows")]
public sealed class LiveResizeTests : IDisposable
{
    private readonly IClipboard _clipboard = Clipboard.Current;
    public void Dispose()
    {
        Clipboard.Current = _clipboard;
        // Each test owns a complete Win32 app lifetime on this thread. Do not let
        // its WM_QUIT terminate the next test's newly created window.
        while (Native.PeekMessageW(out _, 0, 0x0012, 0x0012, 1)) { }
    }
    private static TextBlock Text(string value)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap }; text.AddRun(value); return text;
    }
    private sealed class AnimationProbe : StackPanel, IFrameAnimation
    {
        public int Advances { get; private set; }
        public void Advance(TimeSpan elapsed) => Advances++;
    }
    private sealed class Presenter : ISurfacePresenter
    {
        public string Name => "resize-test";
        public string Description => Name;
        public RenderCapabilities Capabilities => RenderCapabilities.All;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsVsync => false;
        public int Presents { get; private set; }
        public int Depth { get; private set; }
        public int MaxDepth { get; private set; }
        public bool ThrowOnResize { get; set; }
        public Action? OnRender { get; set; }
        public void Resize(int width, int height)
        {
            if (width == Width && height == Height) return;
            Assert.Equal(0, Depth);
            if (ThrowOnResize) throw new InvalidOperationException("resize failure");
            Width = width; Height = height;
        }
        public void Render(DisplayList list)
        {
            Assert.Empty(list.Validate());
            Presents++; Depth++; MaxDepth = Math.Max(MaxDepth, Depth);
            try { OnRender?.Invoke(); } finally { Depth--; }
        }
        public void Dispose() { }
    }

    [WindowsFact]
    public void NativeInputAfterUiDisposalDoesNotAccessTheDisposedTree()
    {
        using var window = new Win32Window("EclipseUI pending input after close", 240, 180, quitOnDestroy: false);
        using var presenter = new Presenter();
        using var root = new StackPanel();
        using var host = new WindowHost(window, presenter, root);
        root.Dispose(); // The HWND is still alive until the pending WM_CLOSE is processed.
        Assert.True(host.IsClosed);

        // Invoke once through managed code so a regression reports a test failure
        // rather than terminating the runner across reverse P/Invoke.
        var leave = typeof(WindowHost).GetMethod("OnPointerLeft",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Null(Record.Exception(() => leave.Invoke(host, [window, EventArgs.Empty])));
        foreach (var message in new uint[] {
            0x02A3, // WM_MOUSELEAVE (the BubuZip crash)
            0x0200, 0x0201, 0x0202, 0x020A, 0x0215, // move, down, up, wheel, capture changed
            0x0100, 0x0101, 0x0102, 0x0008, // key down/up, character, focus lost
        })
            Native.SendMessageW(window.Handle, message, 0x41, 0);
        Native.SendMessageW(window.Handle, 0x0020, window.Handle, 1); // WM_SETCURSOR, HTCLIENT
        Native.SendMessageW(window.Handle, 0x0010, 0, 0); // WM_CLOSE
        Assert.True(window.IsClosed);
    }

    [WindowsFact]
    public void NativePointerLeaveClearsHoverWithoutCancellingCapture()
    {
        using var window = new Win32Window("EclipseUI pointer leave test", 240, 180);
        using var presenter = new Presenter();
        using var root = new ScrollViewer { ScrollBarThickness = 6, ScrollBarHitThickness = 16, ScrollBarInset = 4 };
        root.Children.Add(new Border { Height = 1000 });
        using var host = new WindowHost(window, presenter, root);
        var checkedLeave = false;
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            root.TryGetScrollBarGeometry(false, out var bar);
            var point = new Point(bar.Thumb.X + 2, bar.Thumb.Y + 2);
            host.Input.PointerMove(point); host.Input.PointerPress(point);
            Assert.True(root.IsPointerWithinScrollArea);
            Native.SendMessageW(window.Handle, 0x02A3, 0, 0);
            Assert.Null(host.Input.Hovered);
            Assert.False(root.IsPointerWithinScrollArea);
            Assert.Same(root, host.Input.Captured);
            host.Input.PointerRelease(new(-10, -10));
            host.Input.PointerMove(point);
            Native.SendMessageW(window.Handle, 0x0008, 0, 0); // WM_KILLFOCUS resets ancestor hover state too.
            Assert.False(root.IsPointerWithinScrollArea);
            checkedLeave = true;
        };
        host.Run(2);
        Assert.True(checkedLeave);
    }

    [WindowsFact]
    public void ResizeReflowsAndPresentsBeforeNativeSizeMessageReturns()
    {
        using var window = new Win32Window("EclipseUI live resize test", 600, 320);
        using var presenter = new Presenter();
        using var root = new StackPanel { Padding = 12 };
        var text = Text(string.Join(" ", Enumerable.Repeat("responsive content", 24)));
        var button = new Button { Width = 100, Label = "Fixed size" };
        root.Children.Add(text); root.Children.Add(button);
        using var host = new WindowHost(window, presenter, root);
        var checkedResize = false;
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            var lines = text.TextLines.Count;
            Native.SendMessageW(window.Handle, 0x0231, 0, 0);
            Assert.True(window.IsInSizeMove);
            foreach (var width in new[] { 260, 430, 300 })
            {
                var before = presenter.Presents;
                window.SetClientSize(width, 320);
                Assert.True(presenter.Presents > before);
                Assert.Equal(window.LogicalClientSize, root.FinalRect.Size);
                Assert.Equal(window.ClientWidth, presenter.Width);
                Assert.Equal(window.ClientHeight, presenter.Height);
                Assert.Equal(100, button.FinalRect.Width);
                Assert.Equal(14, button.GetValue(TextBlock.FontSizeProperty));
                if (width == 260) Assert.True(text.TextLines.Count > lines);
            }
            Native.SendMessageW(window.Handle, 0x0232, 0, 0);
            Assert.False(window.IsInSizeMove);
            checkedResize = true;
        };
        host.Run(2);
        Assert.True(checkedResize);
    }

    [WindowsFact]
    public void ModalTimerDrainsUiWorkAndAnimationsWithoutAdvancingOuterLoop()
    {
        using var window = new Win32Window("EclipseUI modal frame test", 300, 260);
        using var presenter = new Presenter(); using var root = new AnimationProbe();
        var text = Text("short"); root.Children.Add(text);
        using var host = new WindowHost(window, presenter, root);
        var ticks = 0;
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            var advances = root.Advances;
            var changed = false;
            var thread = Environment.CurrentManagedThreadId;
            SynchronizationContext.Current!.Post(_ =>
            {
                Assert.Equal(thread, Environment.CurrentManagedThreadId);
                text.Runs[0].Text = string.Join(" ", Enumerable.Repeat("updated content", 20)); changed = true;
            }, null);
            Native.SendMessageW(window.Handle, 0x0231, 0, 0);
            var watch = Stopwatch.StartNew();
            while ((!changed || root.Advances <= advances + 1) && watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                window.PumpMessages(); Thread.Sleep(2);
            }
            Assert.True(changed); Assert.True(root.Advances > advances + 1);
            Assert.True(text.TextLines.Count > 1);
            Assert.Equal(1, host.Ticks);
            ticks++;
            Native.SendMessageW(window.Handle, 0x0232, 0, 0);
        };
        host.Run(2); Assert.Equal(1, ticks);
    }

    [WindowsFact]
    public void ResizeDuringPresentationIsDeferredUntilTheFrameReleasesResources()
    {
        using var window = new Win32Window("EclipseUI resize reentrancy test", 420, 260);
        using var presenter = new Presenter(); using var root = new StackPanel();
        root.Children.Add(Text("resize during render"));
        using var host = new WindowHost(window, presenter, root);
        var changed = false;
        presenter.OnRender = () =>
        {
            if (changed) return;
            changed = true; window.SetClientSize(260, 280);
        };
        host.Run(2);
        Assert.True(changed); Assert.Equal(1, presenter.MaxDepth);
        Assert.Equal(window.LogicalClientSize, root.FinalRect.Size);
        Assert.Equal(window.ClientWidth, presenter.Width);
        Assert.True(presenter.Presents >= 2);
    }

    [WindowsFact]
    public void NativeFrameFailureReturnsToManagedLoopAndRestoresUiContext()
    {
        var previous = SynchronizationContext.Current;
        using var window = new Win32Window("EclipseUI resize failure test", 420, 260);
        using var presenter = new Presenter(); using var root = new StackPanel();
        using var host = new WindowHost(window, presenter, root);
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            presenter.ThrowOnResize = true;
            window.SetClientSize(260, 280);
        };
        var error = Assert.Throws<InvalidOperationException>(() => host.Run(3));
        Assert.Equal("resize failure", error.Message);
        Assert.Same(previous, SynchronizationContext.Current);
        using var headless = new HeadlessRoot(root); // Failed native frame detached the UI root.
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Hwnd;
        internal uint Id;
        internal nint WParam, LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessageW(out Message message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
