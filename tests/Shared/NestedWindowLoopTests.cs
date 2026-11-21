using EclipseUI.Elements;
using EclipseUI.Rendering;
using Xunit;
#if SCHEDULER_WINDOWS
using WindowHost = EclipseUI.Platform.Windows.WindowHost;
using WindowHostOptions = EclipseUI.Platform.Windows.WindowHostOptions;
using TestWindow = EclipseUI.Platform.Windows.Win32Window;
#elif SCHEDULER_MACOS
using WindowHost = EclipseUI.Platform.MacOS.WindowHost;
using WindowHostOptions = EclipseUI.Platform.MacOS.WindowHostOptions;
using TestWindow = EclipseUI.Platform.MacOS.Tests.TestWindow;
#else
using WindowHost = EclipseUI.Platform.Linux.WindowHost;
using WindowHostOptions = EclipseUI.Platform.Linux.WindowHostOptions;
using TestWindow = EclipseUI.Platform.Linux.Tests.TestWindow;
#endif

namespace EclipseUI.Platform.Tests;

public sealed class NestedLoopFactAttribute : FactAttribute
{
    public NestedLoopFactAttribute()
    {
#if SCHEDULER_WINDOWS
        if (!OperatingSystem.IsWindows()) Skip = "Requires Win32 windows.";
#endif
    }
}

#if SCHEDULER_WINDOWS
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
public sealed class NestedWindowLoopTests : IDisposable
{
    private readonly IClipboard _clipboard = Clipboard.Current;
    public NestedWindowLoopTests() => ClearNativeQuit();
    private static TestWindow CreateWindow()
    {
#if SCHEDULER_WINDOWS
        return new("EclipseUI nested frame test", 280, 160, quitOnDestroy: false);
#else
        return new();
#endif
    }

    [NestedLoopFact]
    public void SlowModalChildKeepsOwnerWorkAnimationThemeAndFramesRunning()
    {
        using var window = CreateWindow();
        using var presenter = new Presenter();
        using var root = new AnimationProbe();
        var preference = new ThemeSource();
        using var host = new WindowHost(window, presenter, root, new() { Theme = preference });
        var finished = false;
        var completed = false;
        var clicked = false;
        var button = new Button { Width = 80, Height = 40 };
        button.AddHandler(Button.ClickEvent, _ => clicked = true);
        root.Children.Add(button);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            var ownerContext = SynchronizationContext.Current!;
            var ownerClipboard = Clipboard.Current;
            ownerContext.Post(async _ =>
            {
                await gate.Task;
                Assert.Same(ownerContext, SynchronizationContext.Current);
                Assert.Same(ownerClipboard, Clipboard.Current);
                completed = true;
            }, null);
            ownerContext.Post(_ =>
            {
                var initialTicks = host.Ticks;
                var initialPresents = presenter.Presents;
                var initialAnimations = root.Advances;
                root.IsEnabled = false;
                using var child = CreateWindow();
                using var childPresenter = new Presenter();
                using var childRoot = new Grid();
                using var childHost = new WindowHost(child, childPresenter, childRoot,
                    new WindowHostOptions { MaxFramesPerSecond = 20 });
                childHost.FrameTicked += (_, frame) =>
                {
                    if (frame.Iteration == 1)
                    {
                        preference.Dark = true;
                        _ = Task.Run(() => gate.SetResult());
                    }
                    if (frame.Iteration < 3) return;
                    Assert.True(completed, "The owner's await continuation must run before the child closes.");
                    Assert.True(host.Ticks >= initialTicks + 3, "A slow child must not throttle its owner.");
                    Assert.True(presenter.Presents > initialPresents);
                    Assert.True(root.Advances > initialAnimations);
                    Assert.False(host.Painter.Theme.IsLight);
                    Assert.False(root.IsEnabled);
                    host.Input.PointerPress(new(10, 10));
                    host.Input.PointerRelease(new(10, 10));
                    Assert.False(clicked);
                    finished = true;
                    child.Close();
                };
                childHost.Run(10);
                Assert.True(finished);
                window.Close();
            }, null);
        };
        host.Run(100);
        Assert.True(finished);
        Assert.Equal(1, presenter.MaxDepth);
    }

    [NestedLoopFact]
    public void GrandchildServicesEveryAncestorAndRestoresEachContext()
    {
        var hosts = new List<WindowHost>();
        var contexts = new List<SynchronizationContext>();
        var clipboards = new List<IClipboard>();
        var verified = false;
        void RunLevel(int level)
        {
            using var window = CreateWindow();
            using var presenter = new Presenter();
            using var root = new Grid();
            using var host = new WindowHost(window, presenter, root, new() { MaxFramesPerSecond = 60 });
            hosts.Add(host);
            host.FrameTicked += (_, e) =>
            {
                if (e.Iteration == 1)
                {
                    contexts.Add(SynchronizationContext.Current!);
                    clipboards.Add(Clipboard.Current);
                    if (level < 2)
                        SynchronizationContext.Current!.Post(_ =>
                        {
                            RunLevel(level + 1);
                            Assert.Same(contexts[level], SynchronizationContext.Current);
                            window.Close();
                        }, null);
                }
                Assert.Same(contexts[level], SynchronizationContext.Current);
                Assert.Same(clipboards[level], Clipboard.Current);
                if (level == 2 && e.Iteration == 4)
                {
                    Assert.All(hosts, ancestor => Assert.True(ancestor.Ticks >= 3));
                    verified = true;
                    window.Close();
                }
            };
            host.Run(50);
        }
        RunLevel(0);
        Assert.True(verified);
    }

    [NestedLoopFact]
    public void ClosingOwnerStopsItsFramesWithoutClosingTheChild()
    {
        using var window = CreateWindow();
        using var presenter = new Presenter();
        using var root = new Grid();
        using var host = new WindowHost(window, presenter, root);
        var verified = false;
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            SynchronizationContext.Current!.Post(_ =>
            {
                using var child = CreateWindow();
                using var childPresenter = new Presenter();
                using var childRoot = new Grid();
                using var childHost = new WindowHost(child, childPresenter, childRoot,
                    new() { MaxFramesPerSecond = 60 });
                var stoppedAt = -1;
                childHost.FrameTicked += (_, frame) =>
                {
                    if (frame.Iteration == 1) window.Close();
                    if (frame.Iteration == 2)
                    {
                        Assert.True(window.IsClosed);
                        stoppedAt = host.Ticks;
                    }
                    if (frame.Iteration == 4)
                    {
                        Assert.Equal(stoppedAt, host.Ticks);
                        Assert.False(child.IsClosed);
                        verified = true;
                        child.Close();
                    }
                };
                childHost.Run(10);
            }, null);
        };
        host.Run(100);
        Assert.True(verified);
    }

    [NestedLoopFact]
    public void NestedSchedulingHonorsTheOwnersTickLimit()
    {
        using var window = CreateWindow();
        using var presenter = new Presenter();
        using var root = new Grid();
        using var host = new WindowHost(window, presenter, root);
        var verified = false;
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration != 1) return;
            SynchronizationContext.Current!.Post(_ =>
            {
                using var child = CreateWindow();
                using var childPresenter = new Presenter();
                using var childRoot = new Grid();
                using var childHost = new WindowHost(child, childPresenter, childRoot,
                    new() { MaxFramesPerSecond = 60 });
                childHost.FrameTicked += (_, frame) =>
                {
                    if (frame.Iteration != 5) return;
                    Assert.Equal(3, host.Ticks);
                    verified = true;
                    child.Close();
                };
                childHost.Run(10);
            }, null);
        };
        host.Run(3);
        Assert.Equal(3, host.Ticks);
        Assert.True(verified);
    }

    [NestedLoopFact]
    public void OwnerFailureUnwindsNestedLoopsAndDoesNotLeakScheduledWindows()
    {
        using var window = CreateWindow();
        using var presenter = new Presenter();
        using var root = new Grid();
        using var host = new WindowHost(window, presenter, root);
        var expected = new InvalidOperationException("scheduled owner failed");
        host.FrameTicked += (_, e) =>
        {
            if (e.Iteration > 1) throw expected;
            SynchronizationContext.Current!.Post(_ =>
            {
                using var child = CreateWindow();
                using var childPresenter = new Presenter();
                using var childRoot = new Grid();
                using var childHost = new WindowHost(child, childPresenter, childRoot);
                childHost.Run(10);
            }, null);
        };
        var error = Assert.ThrowsAny<Exception>(() => host.Run(20));
        Assert.Contains(expected.Message, error.ToString());
        ClearNativeQuit();
        var ticks = host.Ticks;
        using var nextWindow = CreateWindow();
        using var nextPresenter = new Presenter();
        using var nextRoot = new Grid();
        using var next = new WindowHost(nextWindow, nextPresenter, nextRoot);
        next.Run(3);
        Assert.Equal(ticks, host.Ticks);
        Assert.Equal(3, next.Ticks);
    }

    private static void ClearNativeQuit()
    {
#if SCHEDULER_WINDOWS
        while (EclipseUI.Platform.Windows.Tests.Native.PeekMessageW(out _, 0, 0x0012, 0x0012, 1)) { }
#endif
    }

    public void Dispose() { Clipboard.Current = _clipboard; ClearNativeQuit(); }
    private sealed class ThemeSource : IHostThemeSource
    {
        public bool Dark;
        public IHostTheme Resolve(ISystemThemeProvider? systemTheme = null) => Dark ? ElementTheme.Dark : ElementTheme.Light;
    }
    private sealed class AnimationProbe : StackPanel, IFrameAnimation
    {
        public int Advances;
        public void Advance(TimeSpan elapsed) { Advances++; Padding = Advances % 2 == 0 ? 2 : 3; }
    }
    private sealed class Presenter : ISurfacePresenter
    {
        public string Name => "nested-loop-test";
        public string Description => Name;
        public RenderCapabilities Capabilities => RenderCapabilities.All;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsVsync => false;
        public int Presents, MaxDepth;
        private int _depth;
        public void Resize(int width, int height) { Assert.Equal(0, _depth); Width = width; Height = height; }
        public void Render(DisplayList list)
        {
            _depth++;
            try { MaxDepth = Math.Max(MaxDepth, _depth); Presents++; Assert.Empty(list.Validate()); }
            finally { _depth--; }
        }
        public void Dispose() { }
    }
}
