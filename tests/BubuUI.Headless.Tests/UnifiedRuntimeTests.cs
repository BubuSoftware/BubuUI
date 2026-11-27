using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

public class UnifiedRuntimeTests
{
    private sealed class CountingControl : StackPanel
    {
        public int RenderCount { get; private set; }
        protected override void Render() => RenderCount++;
    }

    private sealed class LoopingControl : StackPanel
    {
        protected override void Render() => Invalidate();
    }

    private sealed class InitializationProbe : StackPanel
    {
        public int Input { get; set; }
        public int CapturedInput { get; private set; }
        public int InitializationCount { get; private set; }
        protected override void InitializeUi()
        {
            CapturedInput = Input;
            InitializationCount++;
        }
    }

    private sealed class Resource : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class ResourceControl : StackPanel
    {
        public Resource Resource { get; } = new();
        protected override void InitializeUi() => Track(Resource);
    }

    private sealed class FailingInitializationControl : StackPanel
    {
        public Resource Resource { get; } = new();
        public ResourceControl Child { get; } = new();
        public InvalidOperationException Failure { get; } = new("Initialization failed.");

        protected override void InitializeUi()
        {
            Track(Resource);
            Children.Add(Child);
            throw Failure;
        }
    }

    private sealed class NormalizingControl : StackPanel
    {
        public static readonly StyledProperty<double> AmountProperty =
            StyledProperty<double>.Register<NormalizingControl>("Amount", 0);

        public double Amount
        {
            get => GetValue(AmountProperty);
            set => SetValue(AmountProperty, Math.Max(0, value));
        }
    }

    [Fact]
    public void MoreThanOneHundredDirtyControls_UpdateInOneBatch()
    {
        var controls = Enumerable.Range(0, 150).Select(_ => new CountingControl()).ToArray();
        var panel = new StackPanel();
        foreach (var control in controls) panel.Children.Add(control);
        var root = new HeadlessRoot(panel);
        foreach (var control in controls) control.Invalidate();

        root.UpdateLayout();

        Assert.All(controls, control => Assert.Equal(1, control.RenderCount));
    }

    [Fact]
    public void RepeatedSelfInvalidation_IsRejectedAsAnUpdateLoop()
    {
        var control = new LoopingControl();
        var root = new HeadlessRoot(control);
        control.Invalidate();

        var error = Assert.Throws<InvalidOperationException>(() => root.UpdateLayout());

        Assert.Contains("100", error.Message);
        Assert.Contains("round", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Initialization_SeesObjectInitializerValues_AndRunsOnlyOnce()
    {
        var control = new InitializationProbe { Input = 42 };
        var parent = new StackPanel();
        parent.Children.Add(control);
        control.EnsureInitialized();

        Assert.Equal(42, control.CapturedInput);
        Assert.Equal(1, control.InitializationCount);
        Assert.Same(parent, control.Parent);
    }

    [Fact]
    public void WindowAndContent_ShareTheActualTreeUpdateQueue()
    {
        var child = new CountingControl();
        var window = new Window { Content = child };
        var root = new HeadlessRoot(window);
        child.Invalidate();

        Assert.True(window.ProcessInvalidations());
        Assert.Equal(1, child.RenderCount);
        Assert.Same(child, root.Content);
    }

    [Fact]
    public void DisposingTheWindow_ReleasesResourcesAlongItsContentTreeOnce()
    {
        var child = new ResourceControl();
        var window = new Window { Content = child };
        _ = new HeadlessRoot(window);

        window.Dispose();
        window.Dispose();

        Assert.True(child.IsDisposed);
        Assert.Equal(1, child.Resource.DisposeCount);
    }

    [Fact]
    public void TemplateScopes_KeepSetterNormalization_AndRestoreLocalWrites()
    {
        var panel = new NormalizingControl();
        using (panel.BeginTemplateValues())
        {
            panel.Amount = -8;
            using (panel.BeginTemplateValues()) panel.Amount = -3;
            Assert.Equal(0, panel.Amount);
        }

        panel.Amount = 9;
        using (panel.BeginTemplateValues()) panel.Amount = 4;

        Assert.Equal(9, panel.Amount);
    }

    [Fact]
    public void PendingDescendantUpdate_FollowsSubtreeToItsNewHost()
    {
        var child = new CountingControl();
        var subtree = new StackPanel();
        subtree.Children.Add(child);
        var first = new StackPanel();
        var second = new StackPanel();
        first.Children.Add(subtree);
        _ = new HeadlessRoot(first);
        var destination = new HeadlessRoot(second);
        child.Invalidate();

        first.Children.Remove(subtree);
        second.Children.Add(subtree);
        destination.UpdateLayout();

        Assert.Equal(1, child.RenderCount);
        Assert.False(first.ProcessInvalidations());
    }

    [Fact]
    public async Task BackgroundInvalidation_StaysQueuedWhileSubtreeMovesBetweenHosts()
    {
        var child = new CountingControl();
        var subtree = new StackPanel();
        subtree.Children.Add(child);
        var first = new StackPanel();
        var second = new StackPanel();
        first.Children.Add(subtree);
        _ = new HeadlessRoot(first);
        var destination = new HeadlessRoot(second);
        using var started = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var background = Task.Run(() =>
        {
            started.Set();
            do { child.Invalidate(); } while (!finish.IsSet);
            child.Invalidate();
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            for (var i = 0; i < 100; i++)
            {
                first.Children.Remove(subtree);
                second.Children.Add(subtree);
                second.Children.Remove(subtree);
                first.Children.Add(subtree);
            }
            first.Children.Remove(subtree);
            second.Children.Add(subtree);
        }
        finally { finish.Set(); }
        await background;

        Assert.Equal(0, child.RenderCount);
        first.ProcessInvalidations();
        destination.UpdateLayout();

        Assert.Equal(1, child.RenderCount);
    }

    [Fact]
    public void InitializationFailure_ReleasesRegisteredResourcesAndChildTree()
    {
        var control = new FailingInitializationControl();

        var failure = Assert.Throws<InvalidOperationException>(control.EnsureInitialized);

        Assert.Same(control.Failure, failure);
        Assert.True(control.IsDisposed);
        Assert.False(control.IsInitialized);
        Assert.True(control.Child.IsDisposed);
        Assert.Equal(1, control.Resource.DisposeCount);
        Assert.Equal(1, control.Child.Resource.DisposeCount);
        Assert.Throws<ObjectDisposedException>(control.EnsureInitialized);
        control.Dispose();
        Assert.Equal(1, control.Resource.DisposeCount);
    }
}
