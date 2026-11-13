using System;
using System.Threading;
using System.Threading.Tasks;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class StateUpdateTests
{
    private sealed class Probe : UiObject
    {
        public int Value;
        public int Displayed;
        public int Renders;

        protected override void InitializeUi() => Displayed = Value;
        protected override void Render() { Displayed = Value; Renders++; }
    }

    private static Probe Create()
    {
        var probe = new Probe();
        probe.EnsureInitialized();
        return probe;
    }

    [Fact]
    public void ActionInvalidatesAfterTheCallbackAndDefersRendering()
    {
        using var probe = Create();
        probe.UpdateState(() =>
        {
            probe.Value = 5;
            Assert.False(probe.ProcessInvalidations());
        });

        Assert.Equal(0, probe.Displayed);
        Assert.True(probe.ProcessInvalidations());
        Assert.Equal(5, probe.Displayed);
        Assert.Equal(1, probe.Renders);
    }

    [Fact]
    public void UsingInvalidatesAtTheEndOfTheBlock()
    {
        using var probe = Create();
        using (probe.BeginStateUpdate())
        {
            probe.Value = 2;
            probe.Value += 3;
            Assert.False(probe.ProcessInvalidations());
        }

        Assert.True(probe.ProcessInvalidations());
        Assert.Equal(5, probe.Displayed);
        Assert.Equal(1, probe.Renders);
    }

    [Fact]
    public void NestedScopesAndActionsCoalesceInTheExistingQueue()
    {
        using var probe = Create();
        using (probe.BeginStateUpdate())
        {
            probe.UpdateState(() => probe.Value++);
            using (probe.BeginStateUpdate()) probe.Value++;
            probe.UpdateState(() => probe.Value++);
        }

        Assert.True(probe.ProcessInvalidations());
        Assert.Equal(3, probe.Displayed);
        Assert.Equal(1, probe.Renders);
        Assert.False(probe.ProcessInvalidations());
    }

    [Fact]
    public void DisposingTheSameScopeAgainDoesNotScheduleAnotherUpdate()
    {
        using var probe = Create();
        var scope = probe.BeginStateUpdate();
        probe.Value = 7;
        scope.Dispose();
        Assert.True(probe.ProcessInvalidations());

        scope.Dispose();
        Assert.False(probe.ProcessInvalidations());
        Assert.Equal(1, probe.Renders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExceptionalExitPreservesTheErrorAndRefreshesPartiallyChangedState(bool useScope)
    {
        using var probe = Create();
        var expected = new InvalidOperationException("state-change-failed");
        void Change() { probe.Value = 9; throw expected; }

        var actual = Assert.Throws<InvalidOperationException>(() =>
        {
            if (useScope) { using (probe.BeginStateUpdate()) Change(); }
            else probe.UpdateState(Change);
        });

        Assert.Same(expected, actual);
        Assert.True(probe.ProcessInvalidations());
        Assert.Equal(9, probe.Displayed);
    }

    [Fact]
    public void UsingDeclarationAlsoInvalidatesOnEarlyReturn()
    {
        using var probe = Create();
        void Change()
        {
            using var update = probe.BeginStateUpdate();
            probe.Value = 4;
            return;
        }
        Change();

        Assert.True(probe.ProcessInvalidations());
        Assert.Equal(4, probe.Displayed);
    }

    [Fact]
    public void UpdatingBeforeInitializationDoesNotInitializeOrQueueAnExtraRender()
    {
        using var probe = new Probe();
        probe.UpdateState(() => probe.Value = 1);
        using (probe.BeginStateUpdate()) probe.Value = 2;

        Assert.False(probe.IsInitialized);
        Assert.False(probe.ProcessInvalidations());
        Assert.Equal(2, probe.Displayed);
        Assert.Equal(0, probe.Renders);
    }

    [Fact]
    public void DisposingAScopeAfterItsOwnerIsDisposedIsHarmless()
    {
        var probe = Create();
        var scope = probe.BeginStateUpdate();
        probe.Dispose();
        scope.Dispose();
        scope.Dispose();

        Assert.False(probe.ProcessInvalidations());
        Assert.Equal(0, probe.Renders);
    }

    [Fact]
    public async Task AsyncActionInvalidatesAfterCompletionRatherThanTheFirstAwait()
    {
        using var probe = Create();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = probe.UpdateStateAsync(async () =>
        {
            probe.Value = 1;
            await gate.Task;
            probe.Value = 2;
        });

        Assert.False(update.IsCompleted);
        Assert.False(probe.ProcessInvalidations());
        gate.SetResult();
        await update;

        Assert.True(probe.ProcessInvalidations());
        Assert.Equal(2, probe.Displayed);
        Assert.Equal(1, probe.Renders);
    }

    [Fact]
    public async Task AsyncFailurePreservesTheErrorAndInvalidates()
    {
        using var probe = Create();
        var expected = new InvalidOperationException("async-state-change-failed");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => probe.UpdateStateAsync(async () =>
        {
            await Task.Yield();
            probe.Value = 3;
            throw expected;
        }));

        Assert.Same(expected, actual);
        Assert.True(probe.ProcessInvalidations());
        Assert.Equal(3, probe.Displayed);
    }

    [Fact]
    public async Task AsyncCancellationStillInvalidatesStateAlreadyChanged()
    {
        using var probe = Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var update = probe.UpdateStateAsync(() =>
        {
            probe.Value = 6;
            return Task.FromCanceled(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        Assert.True(update.IsCanceled);
        Assert.True(probe.ProcessInvalidations());
        Assert.Equal(6, probe.Displayed);
    }

    [Fact]
    public async Task NullCallbacksDoNotQueueUpdates()
    {
        using var probe = Create();
        Assert.Throws<ArgumentNullException>(() => probe.UpdateState(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => probe.UpdateStateAsync(null!));

        Assert.False(probe.ProcessInvalidations());
    }

    [Theory]
    [InlineData(false, "action")]
    [InlineData(true, "scope")]
    public void GeneratedTemplateMethodsRefreshWithoutAnEventWrapper(bool useScope, string expected)
    {
        using var component = new StateUpdateProbe();
        using var host = new HeadlessRoot(component);
        var text = Assert.Single(component.Descendants<TextBlock>());
        Assert.Equal("0:start", text.FullText());

        if (useScope) component.ApplyScope();
        else component.ApplyAction();
        Assert.Equal("0:start", text.FullText());
        host.UpdateLayout();

        Assert.Equal("1:" + expected, text.FullText());
    }

    [Theory]
    [InlineData(false, "async")]
    [InlineData(true, "scope-async")]
    public void GeneratedAsyncTemplateReturnsToTheUiThreadAndRefreshes(bool useScope, string expected)
    {
        using var component = new StateUpdateProbe();
        using var host = new HeadlessRoot(component);
        using var ui = UiSynchronizationContext.Install(component);
        var text = Assert.Single(component.Descendants<TextBlock>());
        var task = useScope ? component.ApplyScopeAsync() : component.ApplyAsync();
        Assert.False(task.IsCompleted);
        host.UpdateLayout();
        Assert.Equal("0:start", text.FullText());

        // Drain the context directly, so no host-wide automatic invalidation can mask a missing notification.
        Assert.True(ui.Context.Drain() > 0);
        Assert.True(task.IsCompletedSuccessfully);
        host.UpdateLayout();

        Assert.Equal(ui.Context.UiThreadId, component.ContinuationThreadId);
        Assert.Equal("1:" + expected, text.FullText());
    }
}
