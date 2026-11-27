using System;
using System.Collections.Generic;
using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

public class UiConstructionScopeTests
{
    private sealed class Cleanup(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class Probe : StackPanel
    {
        private readonly Action _dispose;

        public Probe(Action dispose) => _dispose = dispose;

        protected override void InitializeUi() => Track(new Cleanup(_dispose));
    }

    [Fact]
    public void FailedConstruction_ReleasesInitializedObjectsNotYetAttached()
    {
        var released = 0;
        var child = new Probe(() => released++);

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var construction = new UiConstructionScope();
            construction.Track(child).EnsureInitialized();
            throw new InvalidOperationException("a later parameter failed");
        }));

        Assert.True(child.IsDisposed);
        Assert.Null(child.Parent);
        Assert.Equal(1, released);
    }

    [Fact]
    public void CompletedConstruction_LeavesTheContentTreeAlive()
    {
        var released = 0;
        var root = new StackPanel();
        var child = new Probe(() => released++);

        using (var construction = new UiConstructionScope())
        {
            root.Children.Add(construction.Track(child));
            construction.Complete();
        }

        Assert.False(child.IsDisposed);
        Assert.Equal(0, released);
        root.Dispose();
        Assert.True(child.IsDisposed);
        Assert.Equal(1, released);
    }

    [Fact]
    public void FailedConstruction_CleansInReverseOrderAndContinuesAfterFailure()
    {
        var released = new List<int>();
        var construction = new UiConstructionScope();
        construction.Track(new Probe(() => released.Add(1))).EnsureInitialized();
        construction.Track(new Probe(() =>
        {
            released.Add(2);
            throw new InvalidOperationException("cleanup failed");
        })).EnsureInitialized();
        construction.Track(new Probe(() => released.Add(3))).EnsureInitialized();

        var error = Assert.Throws<AggregateException>(() => construction.Dispose());

        Assert.Equal(new[] { 3, 2, 1 }, released);
        Assert.Contains("cleanup failed", error.ToString());
        construction.Dispose();
        Assert.Equal(3, released.Count);
    }

    [Fact]
    public void RuntimeAssembly_ExposesOnlyTheUnifiedModel()
    {
        var runtime = typeof(UiObject).Assembly;

        Assert.Null(runtime.GetType("BubuUI.Component"));
        Assert.Null(runtime.GetType("BubuUI.WindowComponent"));
        Assert.Null(runtime.GetType("BubuUI.UiTemplateRootAttribute"));
        Assert.Null(typeof(Window).GetProperty("RootComponent"));
        Assert.Null(typeof(HeadlessRoot).GetProperty("RootComponent"));
        Assert.True(typeof(UiObject).IsAssignableFrom(typeof(Window)));
        Assert.False(typeof(Element).IsAssignableFrom(typeof(Window)));
    }

    [Fact]
    public void GeneratedBlock_ParameterFailureReleasesAllUnattachedChildren()
    {
        ConstructionFailureState.Reset();
        var control = new ConstructionFailure();

        var error = Assert.Throws<InvalidOperationException>(() => control.EnsureInitialized());

        Assert.Equal("fixture-parameter-failure", error.Message);
        Assert.Equal(2, ConstructionFailureState.Created.Count);
        Assert.All(ConstructionFailureState.Created, child =>
        {
            Assert.True(child.IsDisposed);
            Assert.Null(((Element)child).Parent);
        });
        Assert.Equal(new[] { "throwing", "first" }, ConstructionFailureState.Released);
        Assert.True(control.IsDisposed);
    }
}
