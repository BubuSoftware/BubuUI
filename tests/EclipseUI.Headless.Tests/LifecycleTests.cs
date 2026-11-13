using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class LifecycleTests
{
    private sealed class Probe(string name, List<string>? log = null) : StackPanel
    {
        public List<string> Log { get; } = log ?? new();
        public Action<Probe>? InitializedAction;
        public Action<Probe>? AttachedAction;
        public Action<Probe>? DetachedAction;
        public Action<Probe>? ShouldRenderAction;
        public Action<Probe>? UpdatingAction;
        public Action<Probe>? RenderAction;
        public Action<Probe>? DisposingAction;
        public bool CanRender = true;
        public int InitializationCount;
        public int AttachedCount;
        public int DetachedCount;
        public int DisposeCount;

        public void Register(IDisposable resource) => Track(resource);
        protected override void InitializeUi() => InitializationCount++;
        protected override void OnInitialized()
        {
            Log.Add(name + ":initialized");
            InitializedAction?.Invoke(this);
            base.OnInitialized();
        }
        protected override void OnAttached()
        {
            AttachedCount++;
            Log.Add(name + ":attached");
            AttachedAction?.Invoke(this);
            base.OnAttached();
        }
        protected override void OnDetached()
        {
            DetachedCount++;
            Log.Add(name + ":detached");
            DetachedAction?.Invoke(this);
            base.OnDetached();
        }
        protected override bool ShouldRender()
        {
            ShouldRenderAction?.Invoke(this);
            return CanRender;
        }
        protected override void OnUpdating()
        {
            Log.Add(name + ":updating");
            UpdatingAction?.Invoke(this);
            base.OnUpdating();
        }
        protected override void Render()
        {
            Log.Add(name + ":render");
            RenderAction?.Invoke(this);
        }
        protected override void OnUpdated()
        {
            Log.Add(name + ":updated");
            base.OnUpdated();
        }
        protected override void OnDispose()
        {
            DisposeCount++;
            Log.Add(name + ":disposed");
            DisposingAction?.Invoke(this);
            base.OnDispose();
        }
    }

    private sealed class Resource(Action onDispose) : IDisposable
    {
        public int DisposeCount;
        public void Dispose()
        {
            DisposeCount++;
            onDispose();
        }
    }

    [Fact]
    public void OfflineAssembly_InitializesOnce_WithoutAttachment()
    {
        var parent = new StackPanel();
        var child = new Probe("child")
        {
            InitializedAction = node =>
            {
                Assert.True(node.IsInitialized);
                Assert.False(node.IsAttached);
                Assert.Null(node.Parent);
                node.EnsureInitialized();
            },
        };

        parent.Children.Add(child);
        child.EnsureInitialized();

        Assert.Equal(1, child.InitializationCount);
        Assert.Equal(new[] { "child:initialized" }, child.Log);
        Assert.Same(parent, child.Parent);
        Assert.False(child.IsAttached);
    }

    [Fact]
    public void HostAttachment_PublishesWholeSubtreeState_BeforeTopDownHooks()
    {
        var log = new List<string>();
        var parent = new Probe("parent", log);
        var child = new Probe("child", log);
        var grandchild = new Probe("grandchild", log);
        child.Children.Add(grandchild);
        parent.Children.Add(child);
        parent.EnsureInitialized();
        log.Clear();
        parent.AttachedAction = _ =>
        {
            Assert.True(parent.IsAttached);
            Assert.True(child.IsAttached);
            Assert.True(grandchild.IsAttached);
            Assert.Same(parent, child.Parent);
            Assert.Same(child, grandchild.Parent);
        };

        using var host = new HeadlessRoot(parent);

        Assert.Equal(new[] { "parent:attached", "child:attached", "grandchild:attached" }, log);
    }

    [Fact]
    public void HostDisposal_PublishesDetachedState_BeforeBottomUpHooks_AndKeepsTreeReusable()
    {
        var log = new List<string>();
        var parent = new Probe("parent", log);
        var child = new Probe("child", log);
        var grandchild = new Probe("grandchild", log);
        child.Children.Add(grandchild);
        parent.Children.Add(child);
        var resource = new Resource(() => log.Add("resource"));
        parent.Register(resource);
        var host = new HeadlessRoot(parent);
        log.Clear();
        grandchild.DetachedAction = _ =>
        {
            Assert.False(parent.IsAttached);
            Assert.False(child.IsAttached);
            Assert.False(grandchild.IsAttached);
            Assert.Same(parent, child.Parent);
            Assert.Same(child, grandchild.Parent);
        };

        host.Dispose();
        host.Dispose();

        Assert.Equal(new[] { "grandchild:detached", "child:detached", "parent:detached" }, log);
        Assert.False(parent.IsDisposed);
        Assert.False(child.IsDisposed);
        Assert.Equal(0, resource.DisposeCount);

        using var nextHost = new HeadlessRoot(parent);
        Assert.True(grandchild.IsAttached);
        Assert.Equal(1, parent.InitializationCount);
        Assert.Equal(2, parent.AttachedCount);
    }

    [Fact]
    public void RemoveAndReattach_DoesNotInitializeOrDisposeAgain()
    {
        var first = new StackPanel();
        var second = new StackPanel();
        var child = new Probe("child");
        first.Children.Add(child);
        using var firstHost = new HeadlessRoot(first);
        using var secondHost = new HeadlessRoot(second);
        child.DetachedAction = node =>
        {
            Assert.Null(node.Parent);
            Assert.False(node.IsAttached);
        };

        Assert.True(first.Children.Remove(child));
        Assert.False(child.IsDisposed);
        child.DetachedAction = null;
        second.Children.Add(child);

        Assert.Same(second, child.Parent);
        Assert.True(child.IsAttached);
        Assert.Equal(1, child.InitializationCount);
        Assert.Equal(2, child.AttachedCount);
        Assert.Equal(1, child.DetachedCount);
        Assert.Equal(0, child.DisposeCount);
    }

    [Fact]
    public void Clear_CommitsAllRemovals_BeforeNotifications_AndKeepsParentAttached()
    {
        var parent = new StackPanel();
        var first = new Probe("first");
        var second = new Probe("second");
        parent.Children.Add(first);
        parent.Children.Add(second);
        using var host = new HeadlessRoot(parent);
        first.DetachedAction = second.DetachedAction = node =>
        {
            Assert.Empty(parent.Children);
            Assert.Null(node.Parent);
            Assert.False(node.IsAttached);
            Assert.True(parent.IsAttached);
        };

        parent.Children.Clear();

        Assert.Equal(1, first.DetachedCount);
        Assert.Equal(1, second.DetachedCount);
        Assert.False(first.IsDisposed);
        Assert.False(second.IsDisposed);
    }

    [Fact]
    public void DisposingAnAttachedChild_RemovesItBeforeDetach_AndTerminatesOnce()
    {
        var parent = new StackPanel();
        var child = new Probe("child");
        parent.Children.Add(child);
        using var host = new HeadlessRoot(parent);
        child.Log.Clear();
        child.DetachedAction = node =>
        {
            Assert.Null(node.Parent);
            Assert.DoesNotContain(node, parent.Children);
            Assert.False(node.IsAttached);
        };
        child.DisposingAction = node => node.Dispose();

        child.Dispose();
        child.Unmount();
        child.Invalidate();
        host.UpdateLayout();

        Assert.Equal(new[] { "child:detached", "child:disposed" }, child.Log);
        Assert.Equal(1, child.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => parent.Children.Add(child));
        Assert.Throws<ObjectDisposedException>(child.EnsureInitialized);
    }

    [Fact]
    public void Disposal_DetachesBeforeCleanup_AndReleasesChildrenAndResourcesInReverseOrder()
    {
        var log = new List<string>();
        var parent = new Probe("parent", log);
        var first = new Probe("first", log);
        var second = new Probe("second", log);
        parent.Children.Add(first);
        parent.Children.Add(second);
        parent.Register(new Resource(() => log.Add("resource:first")));
        parent.Register(new Resource(() => log.Add("resource:second")));
        using var host = new HeadlessRoot(parent);
        log.Clear();

        parent.Dispose();

        var firstCleanup = log.FindIndex(item => item.EndsWith(":disposed", StringComparison.Ordinal));
        Assert.All(log.Take(firstCleanup), item => Assert.EndsWith(":detached", item));
        Assert.Equal(new[] { "second:disposed", "first:disposed", "resource:second", "resource:first", "parent:disposed" },
            log.Skip(firstCleanup));
        Assert.All(new[] { parent, first, second }, node =>
        {
            Assert.False(node.IsAttached);
            Assert.True(node.IsDisposed);
            Assert.Equal(1, node.DetachedCount);
            Assert.Equal(1, node.DisposeCount);
        });
    }

    [Fact]
    public void InitializedHookFailure_DisposesRegisteredResources_AndCannotRetry()
    {
        var failure = new InvalidOperationException("initialized hook failed");
        var node = new Probe("node") { InitializedAction = _ => throw failure };
        var resource = new Resource(() => { });
        node.Register(resource);

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(node.EnsureInitialized));

        Assert.True(node.IsDisposed);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(1, node.DisposeCount);
        Assert.Throws<ObjectDisposedException>(node.EnsureInitialized);
    }

    [Fact]
    public void InitializedHook_InvalidationWaitsUntilInitializationCompletes()
    {
        var node = new Probe("node")
        {
            InitializedAction = item =>
            {
                item.Invalidate();
                Assert.False(item.ProcessInvalidations());
                Assert.Equal(new[] { "node:initialized" }, item.Log);
            },
        };

        node.EnsureInitialized();

        Assert.Equal(new[] { "node:initialized" }, node.Log);
        Assert.True(node.ProcessInvalidations());
        Assert.Equal(new[] { "node:initialized", "node:updating", "node:render", "node:updated" }, node.Log);
        Assert.Equal(1, node.InitializationCount);
        Assert.False(node.ProcessInvalidations());
    }

    [Fact]
    public void AttachmentFailure_NotifiesRemainingNodes_AndRollsBackHostAttachment()
    {
        var parent = new Probe("parent");
        var first = new Probe("first") { AttachedAction = _ => throw new InvalidOperationException("attach failed") };
        var second = new Probe("second");
        parent.Children.Add(first);
        parent.Children.Add(second);

        var error = Assert.Throws<AggregateException>(() => new HeadlessRoot(parent));

        Assert.Contains(error.Flatten().InnerExceptions, item => item.Message == "attach failed");
        Assert.All(new[] { parent, first, second }, node =>
        {
            Assert.False(node.IsAttached);
            Assert.False(node.IsDisposed);
            Assert.Equal(1, node.AttachedCount);
            Assert.Equal(1, node.DetachedCount);
        });

        first.AttachedAction = null;
        using var next = new HeadlessRoot(parent);
        Assert.True(second.IsAttached);
    }

    [Fact]
    public void DetachmentFailure_DoesNotPreventOtherHooksOrResourceCleanup()
    {
        var parent = new Probe("parent");
        var first = new Probe("first") { DetachedAction = _ => throw new InvalidOperationException("detach failed") };
        var second = new Probe("second");
        parent.Children.Add(first);
        parent.Children.Add(second);
        var resource = new Resource(() => { });
        parent.Register(resource);
        using var host = new HeadlessRoot(parent);

        var error = Assert.Throws<AggregateException>(parent.Dispose);

        Assert.Contains(error.Flatten().InnerExceptions, item => item.Message == "detach failed");
        Assert.All(new[] { parent, first, second }, node =>
        {
            Assert.True(node.IsDisposed);
            Assert.False(node.IsAttached);
            Assert.Equal(1, node.DetachedCount);
            Assert.Equal(1, node.DisposeCount);
        });
        Assert.Equal(1, resource.DisposeCount);
        parent.Dispose();
    }

    [Fact]
    public void AttachedHook_CanRemovePendingChildAndAddAnother_WithoutUnpairedNotifications()
    {
        var parent = new Probe("parent");
        var removed = new Probe("removed");
        var added = new Probe("added");
        parent.Children.Add(removed);
        parent.AttachedAction = _ =>
        {
            parent.Children.Remove(removed);
            parent.Children.Add(added);
        };

        using var host = new HeadlessRoot(parent);

        Assert.Equal(0, removed.AttachedCount);
        Assert.Equal(0, removed.DetachedCount);
        Assert.False(removed.IsAttached);
        Assert.Equal(1, added.AttachedCount);
        Assert.Equal(0, added.DetachedCount);
        Assert.True(added.IsAttached);
    }

    [Fact]
    public void AttachedHook_CanDisposeItsOwnNode_WithoutDuplicateNotifications()
    {
        var parent = new StackPanel();
        var child = new Probe("child") { AttachedAction = node => node.Dispose() };
        parent.Children.Add(child);

        using var host = new HeadlessRoot(parent);

        Assert.Empty(parent.Children);
        Assert.True(child.IsDisposed);
        Assert.False(child.IsAttached);
        Assert.Equal(1, child.AttachedCount);
        Assert.Equal(1, child.DetachedCount);
        Assert.Equal(1, child.DisposeCount);
    }

    [Fact]
    public void DetachingHook_CannotDestroyAnAncestorAwaitingItsNotification()
    {
        var parent = new Probe("parent");
        var child = new Probe("child") { DetachedAction = _ => parent.Dispose() };
        parent.Children.Add(child);
        var host = new HeadlessRoot(parent);

        Assert.Throws<AggregateException>(host.Dispose);

        Assert.False(parent.IsAttached);
        Assert.False(child.IsAttached);
        Assert.Equal(1, parent.DetachedCount);
        Assert.Equal(1, child.DetachedCount);
        Assert.False(parent.IsDisposed);
        child.DetachedAction = null;
        parent.Dispose();
        Assert.True(child.IsDisposed);
    }

    [Fact]
    public void ATreeCannotBelongToTwoHosts_AndCanMoveAfterHostDisposal()
    {
        var parent = new Probe("parent");
        var child = new Probe("child");
        parent.Children.Add(child);
        var first = new HeadlessRoot(parent);

        Assert.Throws<InvalidOperationException>(() => new HeadlessRoot(parent));
        Assert.Throws<InvalidOperationException>(() => new HeadlessRoot(child));
        Assert.True(parent.IsAttached);
        Assert.Equal(1, parent.AttachedCount);

        first.Dispose();
        using var second = new HeadlessRoot(parent);
        Assert.True(child.IsAttached);
        Assert.Equal(2, parent.AttachedCount);
    }

#if DEBUG
    [Theory]
    [InlineData("Add")]
    [InlineData("Remove")]
    [InlineData("Clear")]
    public void CrossThreadStructureMutation_IsRejectedBeforeAnyTreeChanges(string operation)
    {
        var parent = new StackPanel();
        var first = new Probe("first");
        var second = new Probe("second");
        var candidate = new Probe("candidate");
        parent.Children.Add(first);
        parent.Children.Add(second);
        using var host = new HeadlessRoot(parent);
        using var scope = host.InstallSynchronizationContext();
        var originalChildren = parent.Children.ToArray();
        Exception? error = null;
        var workerThreadId = 0;
        var worker = new Thread(() =>
        {
            workerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                switch (operation)
                {
                    case "Add": parent.Children.Add(candidate); break;
                    case "Remove": parent.Children.Remove(first); break;
                    case "Clear": parent.Children.Clear(); break;
                    default: throw new ArgumentOutOfRangeException(nameof(operation));
                }
            }
            catch (Exception failure) { error = failure; }
        }) { IsBackground = true };

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "The structure mutation worker did not finish.");

        var violation = Assert.IsType<UiThreadViolationException>(error);
        Assert.Equal(scope.Context.UiThreadId, violation.ExpectedThreadId);
        Assert.Equal(workerThreadId, violation.ActualThreadId);
        Assert.NotEqual(scope.Context.UiThreadId, workerThreadId);
        Assert.Equal(originalChildren, parent.Children);
        Assert.All(new[] { first, second }, child =>
        {
            Assert.Same(parent, child.Parent);
            Assert.True(child.IsAttached);
            Assert.Equal(0, child.DetachedCount);
        });
        Assert.Null(candidate.Parent);
        Assert.False(candidate.IsInitialized);
        Assert.False(candidate.IsAttached);

        host.Dispose();
        Assert.False(parent.IsAttached);
        Assert.False(first.IsAttached);
        Assert.False(second.IsAttached);
        parent.Dispose();
        candidate.Dispose();
        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
    }
#endif

    [Fact]
    public void UpdateHooks_OnlyBracketAcceptedUpdates_AndDoNotRunDuringInitialization()
    {
        var node = new Probe("node") { CanRender = false };
        using var host = new HeadlessRoot(node);
        Assert.DoesNotContain(node.Log, item => item.EndsWith(":updating", StringComparison.Ordinal));
        node.Log.Clear();
        node.Invalidate();
        host.UpdateLayout();
        Assert.Empty(node.Log);

        node.CanRender = true;
        node.Invalidate();
        host.UpdateLayout();

        Assert.Equal(new[] { "node:updating", "node:render", "node:updated" }, node.Log);
    }

    [Fact]
    public void FailedRender_DoesNotCallUpdatedHook()
    {
        var node = new Probe("node") { RenderAction = _ => throw new InvalidOperationException("render failed") };
        using var host = new HeadlessRoot(node);
        node.Log.Clear();
        node.Invalidate();

        Assert.Throws<InvalidOperationException>(host.UpdateLayout);

        Assert.Equal(new[] { "node:updating", "node:render" }, node.Log);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecursiveUpdateProcessing_DefersInvalidationUntilCurrentUpdateCompletes(bool invalidateDuringUpdating)
    {
        var node = new Probe("node");
        var requested = false;
        Action<Probe> invalidateAgain = item =>
        {
            if (requested) return;
            requested = true;
            item.Invalidate();
            Assert.False(item.ProcessInvalidations());
            Assert.DoesNotContain("node:updated", item.Log);
        };
        if (invalidateDuringUpdating) node.UpdatingAction = invalidateAgain;
        else node.RenderAction = invalidateAgain;
        node.EnsureInitialized();
        node.Log.Clear();
        node.Invalidate();

        Assert.True(node.ProcessInvalidations());

        Assert.Equal(new[]
        {
            "node:updating", "node:render", "node:updated",
            "node:updating", "node:render", "node:updated",
        }, node.Log);
        Assert.False(node.ProcessInvalidations());
    }

    [Fact]
    public void UpdatingHook_CanDisposeTheObject_WithoutRunningGeneratedUpdateAfterward()
    {
        var node = new Probe("node") { UpdatingAction = item => item.Dispose() };
        node.EnsureInitialized();
        node.Log.Clear();
        node.Invalidate();

        node.ProcessInvalidations();

        Assert.Equal(new[] { "node:updating", "node:disposed" }, node.Log);
        Assert.True(node.IsDisposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovingDuringUpdateHooks_TransfersRenderingToTheNewHost(bool moveDuringShouldRender)
    {
        var first = new StackPanel();
        var second = new StackPanel();
        var node = new Probe("node");
        first.Children.Add(node);
        using var firstHost = new HeadlessRoot(first);
        using var secondHost = new HeadlessRoot(second);
        Action<Probe> move = item =>
        {
            if (!ReferenceEquals(item.Parent, first)) return;
            first.Children.Remove(item);
            second.Children.Add(item);
        };
        if (moveDuringShouldRender) node.ShouldRenderAction = move;
        else node.UpdatingAction = move;
        node.Log.Clear();
        node.Invalidate();

        firstHost.UpdateLayout();

        Assert.Same(second, node.Parent);
        Assert.DoesNotContain("node:render", node.Log);
        Assert.DoesNotContain("node:updated", node.Log);

        secondHost.UpdateLayout();

        Assert.Equal(1, node.Log.Count(item => item == "node:render"));
        Assert.Equal(1, node.Log.Count(item => item == "node:updated"));
        Assert.Equal(moveDuringShouldRender ? 1 : 2, node.Log.Count(item => item == "node:updating"));
        Assert.Equal(new[] { "node:updating", "node:render", "node:updated" }, node.Log.TakeLast(3));
    }

    [Fact]
    public void UiControlHooks_SeeCompletedRefsAndValues_AndObserveGeneratedUpdates()
    {
        var control = new LifecycleControl();
        using var host = new HeadlessRoot(control);

        Assert.True(control.ReadyWhenInitialized);
        Assert.Equal(new[] { "initialized", "attached" }, control.Log);
        Assert.True(control.Label!.IsAttached);

        control.Message = "second";
        control.Invalidate();
        host.UpdateLayout();
        Assert.Equal("second", control.TextAfterUpdate);
        Assert.Equal(new[] { "initialized", "attached", "updating", "updated" }, control.Log);

        control.Unmount();
        control.Dispose();
        Assert.Equal(new[] { "initialized", "attached", "updating", "updated", "detached", "disposed" }, control.Log);
    }

    [Fact]
    public void UiWindowHooks_UseTheSameInitializationUpdateAndTerminationMechanism()
    {
        var window = new LifecycleWindow();
        using var host = new HeadlessRoot(window);

        Assert.True(window.ReadyWhenInitialized);
        Assert.True(window.IsAttached);
        Assert.True(window.Panel!.IsAttached);
        Assert.Equal(new[] { "initialized", "attached" }, window.Log);

        window.TitleText = "second";
        window.Invalidate();
        host.UpdateLayout();
        Assert.Equal("second", window.TitleAfterUpdate);
        var updateLog = window.Log.Skip(2).ToArray();
        Assert.NotEmpty(updateLog);
        Assert.Equal(0, updateLog.Length % 2);
        for (var i = 0; i < updateLog.Length; i += 2)
            Assert.Equal(new[] { "updating", "updated" }, updateLog.Skip(i).Take(2));

        window.Unmount();
        window.Dispose();
        Assert.False(window.IsAttached);
        Assert.True(window.Panel.IsDisposed);
        Assert.Equal(new[] { "initialized", "attached" }.Concat(updateLog).Concat(new[] { "detached", "disposed" }), window.Log);
    }
}
