using BubuUI.Elements;
using BubuUI.Rendering;
using Xunit;

namespace BubuUI.Platform.MacOS.Tests;

public class LiveResizeTests
{
    internal sealed class Presenter : ISurfacePresenter
    {
        public string Name => "live resize test";
        public string Description => Name;
        public RenderCapabilities Capabilities => RenderCapabilities.All;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsVsync => false;
        public int Presents { get; private set; }
        public int MaxDepth { get; private set; }
        public Action? OnRender { get; set; }
        public bool ThrowOnResize { get; set; }
        private int _depth;
        public void Resize(int width, int height)
        {
            Assert.Equal(0, _depth); // No surface reconfiguration while Render owns resources.
            if (ThrowOnResize) throw new InvalidOperationException("resize failure");
            Width = width; Height = height;
        }
        public void Render(DisplayList list)
        {
            _depth++;
            MaxDepth = Math.Max(MaxDepth, _depth);
            try { OnRender?.Invoke(); Presents++; }
            finally { _depth--; }
        }
        public void Dispose() { }
    }

    [Fact]
    public void ResizeReflowsAndPresentsBeforeReturningWithoutAdvancingOuterLoop()
    {
        using var window = new TestWindow();
        using var presenter = new Presenter();
        using var root = new StackPanel();
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.AddRun(string.Join(" ", Enumerable.Repeat("responsive layout", 30)));
        var button = new Button { Width = 100, Height = 32 };
        root.Children.Add(text); root.Children.Add(button);
        using var host = new WindowHost(window, presenter, root);
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            var originalLines = text.TextLines.Count;
            foreach (var (width, height, scale) in new[] { (400, 800, 2.0), (900, 600, 1.5), (600, 800, 2.0) })
            {
                var before = presenter.Presents;
                window.Resize(width, height, scale);
                Assert.True(presenter.Presents > before);
                Assert.Equal(window.LogicalClientSize, root.FinalRect.Size);
                Assert.Equal((width, height), (presenter.Width, presenter.Height));
                Assert.Equal(100, button.FinalRect.Width);
                Assert.Equal(1, host.Ticks);
                if (width == 400) Assert.True(text.TextLines.Count > originalLines);
            }
        };
        host.Run(2);
        Assert.Equal(1, presenter.MaxDepth);
    }

    [Fact]
    public void ExposeDrainsPostedWorkAndReflowsBeforeOuterLoopResumes()
    {
        using var window = new TestWindow(); using var presenter = new Presenter();
        using var root = new StackPanel();
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.AddRun("short"); root.Children.Add(text);
        using var host = new WindowHost(window, presenter, root);
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            var thread = Environment.CurrentManagedThreadId;
            var ran = false;
            SynchronizationContext.Current!.Post(_ =>
            {
                Assert.Equal(thread, Environment.CurrentManagedThreadId);
                text.Runs[0].Text = string.Join(" ", Enumerable.Repeat("updated content", 30));
                ran = true;
            }, null);
            var before = presenter.Presents;
            window.Expose();
            Assert.True(ran);
            Assert.True(presenter.Presents > before);
            Assert.True(text.TextLines.Count > 1);
            Assert.Equal(1, host.Ticks);
        };
        host.Run(2);
    }

    [Fact]
    public void ResizeDuringPresentationWaitsForResourcesAndUsesLatestSize()
    {
        using var window = new TestWindow(); using var presenter = new Presenter();
        using var root = new StackPanel();
        using var host = new WindowHost(window, presenter, root);
        var changed = false;
        presenter.OnRender = () =>
        {
            if (changed) return;
            changed = true;
            window.Resize(600, 500, 1.5);
            window.Resize(500, 400, 1);
        };
        host.Run(1);
        Assert.True(changed);
        Assert.Equal(1, presenter.MaxDepth);
        Assert.Equal(2, presenter.Presents);
        Assert.Equal(new Size(500, 400), root.FinalRect.Size);
        Assert.Equal((500, 400), (presenter.Width, presenter.Height));
    }

    [Fact]
    public void NativeFrameFailureReturnsToManagedLoopAndRestoresContextAndClipboard()
    {
        var context = SynchronizationContext.Current;
        var clipboard = Clipboard.Current;
        using var window = new TestWindow(); using var presenter = new Presenter();
        using var root = new StackPanel();
        using var host = new WindowHost(window, presenter, root);
        var returnedFromCallback = false;
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            presenter.ThrowOnResize = true;
            window.Resize(500, 400, 1);
            returnedFromCallback = true;
        };
        var error = Assert.Throws<InvalidOperationException>(() => host.Run(2));
        Assert.True(returnedFromCallback);
        Assert.Equal("resize failure", error.Message);
        Assert.Same(context, SynchronizationContext.Current);
        Assert.Same(clipboard, Clipboard.Current);
        using var headless = new HeadlessRoot(root);
    }

    [Fact]
    public void CallbacksOutsideRunDoNotRenderAndNextRunUsesLatestSize()
    {
        using var window = new TestWindow(); using var presenter = new Presenter();
        using var root = new StackPanel();
        using var host = new WindowHost(window, presenter, root);
        window.Resize(500, 400, 1);
        window.Expose();
        Assert.Equal(0, presenter.Presents);
        host.Run(1);
        window.Resize(600, 500, 1);
        window.Expose();
        Assert.Equal(1, presenter.Presents);
        host.Run(1);
        Assert.Equal(new Size(600, 500), root.FinalRect.Size);
        host.Dispose();
        window.Resize(700, 600, 1);
        window.Expose();
        Assert.Equal(2, presenter.Presents);
    }
}
