using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

#if DEBUG
public class HotReloadTests
{
    private sealed class Probe : StackPanel
    {
        public int Builds, Initialized, Cleanups, BuildThread;
        public bool Fail;
        protected override void InitializeUi()
        {
            Builds++;
            BuildThread = Environment.CurrentManagedThreadId;
            Children.Add(new TextBlock());
            Track(new Resource(() => Cleanups++));
            if (Fail) throw new InvalidOperationException("broken edit");
        }
        protected override void OnInitialized() => Initialized++;
        protected override void ReloadUi() => RebuildUi();
    }

    private sealed class Resource(Action cleanup) : IDisposable
    {
        public void Dispose() => cleanup();
    }

    private class GenericProbe<T> : StackPanel
    {
        public int Reloads;
        protected override void ReloadUi() => Reloads++;
    }

    [Fact]
    public void GeneratedTextRoot_ReplacesRunsInsteadOfAppending()
    {
        using var view = new HotReloadText();
        view.EnsureInitialized();
        var oldRun = Assert.Single(view.Runs);
        view.Message = "after";
        for (var i = 0; i < 3; i++)
        {
            HotReload.UpdateApplication([typeof(HotReloadText)]);
            view.ProcessInvalidations();
            Assert.Equal("after", view.FullText());
            Assert.Single(view.Runs);
        }
        Assert.True(oldRun.IsDisposed);
    }

    [Fact]
    public void OpenGenericUpdate_RefreshesConstructedInstance()
    {
        using var view = new GenericProbe<int>();
        view.EnsureInitialized();
        HotReload.UpdateApplication([typeof(GenericProbe<>)]);
        view.ProcessInvalidations();
        Assert.Equal(1, view.Reloads);
    }

    [Fact]
    public void GeneratedRootHandlers_DoNotAccumulateAcrossRebuilds()
    {
        using var view = new HotReloadButton();
        view.EnsureInitialized();
        var externalClicks = 0;
        view.AddHandler(Button.ClickEvent, () => externalClicks++);
        for (var i = 0; i < 3; i++)
        {
            HotReload.UpdateApplication([typeof(HotReloadButton)]);
            view.ProcessInvalidations();
            view.Click();
        }
        Assert.Equal(3, view.Clicks);
        Assert.Equal(3, externalClicks);
    }

    [Fact]
    public void WorkerNotification_IsDeferredAndCoalescedOnUiThread()
    {
        using var view = new Probe();
        using var host = new HeadlessRoot(view);
        using var scope = host.InstallSynchronizationContext();
        var thread = new Thread(() =>
        {
            HotReload.UpdateApplication([typeof(Probe)]);
            HotReload.UpdateApplication([typeof(Probe)]);
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, view.Builds);
        host.UpdateLayout();
        Assert.Equal(2, view.Builds);
        Assert.Equal(Environment.CurrentManagedThreadId, view.BuildThread);
        Assert.Equal(1, view.Initialized);
    }

    [Fact]
    public void Rebuild_DisposesOwnedContentAndResources_PreservesCallerContent()
    {
        using var view = new Probe();
        var supplied = new TextBlock();
        view.Children.Add(supplied);
        view.EnsureInitialized();
        var oldChild = view.Children.Last();
        HotReload.UpdateApplication([typeof(Probe)]);
        view.ProcessInvalidations();
        Assert.True(oldChild.IsDisposed);
        Assert.False(supplied.IsDisposed);
        Assert.Contains(supplied, view.Children);
        Assert.Equal(2, view.Children.Count);
        Assert.Equal(1, view.Cleanups);
    }

    [Fact]
    public void GeneratedView_RebuildsRefsAndRetainsUserFields()
    {
        using var view = new LifecycleControl();
        using var host = new HeadlessRoot(view);
        var oldLabel = view.Label;
        view.Message = "retained state";
        HotReload.UpdateApplication([typeof(LifecycleControl)]);
        host.UpdateLayout();
        Assert.True(oldLabel!.IsDisposed);
        Assert.NotSame(oldLabel, view.Label);
        Assert.Equal("retained state", view.Label!.FullText());
        Assert.Single(view.Log, item => item == "initialized");
        Assert.True(view.Label!.IsAttached);
    }

    [Fact]
    public void FailedRebuild_CanRecoverOnNextUpdate()
    {
        using var view = new Probe();
        view.EnsureInitialized();
        view.Fail = true;
        HotReload.UpdateApplication([typeof(Probe)]);
        view.ProcessInvalidations();
        var partial = Assert.Single(view.Children);
        view.Fail = false;
        HotReload.UpdateApplication([typeof(Probe)]);
        view.ProcessInvalidations();
        Assert.True(partial.IsDisposed);
        Assert.Single(view.Children);
        Assert.Equal(3, view.Builds);
        Assert.Equal(2, view.Cleanups);
    }

    [Fact]
    public void UnrelatedUpdatesAndDisposedInstances_AreIgnored()
    {
        using var view = new Probe();
        view.EnsureInitialized();
        HotReload.UpdateApplication([typeof(string)]);
        view.ProcessInvalidations();
        Assert.Equal(1, view.Builds);
        view.Dispose();
        HotReload.UpdateApplication([typeof(Probe)]);
        Assert.False(view.ProcessInvalidations());
        Assert.Equal(1, view.Builds);
    }
}
#endif
