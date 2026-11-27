using System;
using System.Collections.Generic;
using System.Linq;
using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

public class BlockFailureCleanupTests
{
    private sealed class Resource(string name, List<string> released, bool throws = false) : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            released.Add(name);
            if (throws) throw new InvalidOperationException(name);
        }
    }

    private sealed class Probe(Resource resource) : StackPanel
    {
        protected override void InitializeUi() => Track(resource);
    }

    private sealed record Item(int Key, string Label);

    [Fact]
    public void FailedListRefresh_ReleasesNewEntriesAndPreservesTheExistingTree()
    {
        var released = new List<string>();
        var originalItems = new[] { new Item(1, "old-one"), new Item(2, "old-two") };
        IReadOnlyList<Item> items = originalItems;
        var failure = new InvalidOperationException("second new item failed");
        var failOnSecondNewItem = false;
        var controls = new Dictionary<string, Probe>();
        using var block = new ForEachBlock<Item>();
        block.SetItems(() => items);
        block.SetKeySelector(item => item.Key);
        block.SetTemplate((item, scope) =>
        {
            var control = new Probe(new Resource(item.Label, released));
            control.EnsureInitialized();
            controls[item.Label] = control;
            scope.AddRoot(control);
            if (failOnSecondNewItem && item.Key == 2) throw failure;
        });
        block.Refresh();
        var originalRoots = block.Children.ToArray();
        items = new[] { new Item(1, "new-one"), new Item(2, "new-two") };
        failOnSecondNewItem = true;

        var actual = Assert.Throws<InvalidOperationException>(block.Refresh);

        Assert.Same(failure, actual);
        Assert.Equal(new[] { "new-two", "new-one" }, released);
        Assert.True(controls["new-one"].IsDisposed);
        Assert.True(controls["new-two"].IsDisposed);
        Assert.False(controls["old-one"].IsDisposed);
        Assert.False(controls["old-two"].IsDisposed);
        Assert.Equal(originalRoots, block.Children.ToArray());
        Assert.All(originalRoots, child => Assert.Same(block, child.Parent));
    }

    [Theory]
    [InlineData("if")]
    [InlineData("switch")]
    [InlineData("foreach")]
    public void DisposingBlock_ContinuesCleanupAndReachesTheTerminalState(string kind)
    {
        var released = new List<string>();
        var rootResources = new List<Resource>();
        var scopeResources = new List<Resource>();
        var roots = new List<Probe>();
        void Build(BlockScope scope)
        {
            var index = roots.Count;
            var resource = new Resource("root-" + index, released, throws: true);
            var child = new Probe(resource);
            child.EnsureInitialized();
            rootResources.Add(resource);
            roots.Add(child);
            scope.AddRoot(child);
            var scoped = new Resource("scope-" + index, released, throws: true);
            scopeResources.Add(scoped);
            scope.AddDisposable(scoped);
        }

        StackPanelBase block;
        switch (kind)
        {
            case "if":
                var conditional = new ConditionalBlock();
                conditional.SetCondition(() => true);
                conditional.SetContent(Build);
                conditional.Refresh();
                block = conditional;
                break;
            case "switch":
                var switched = new SwitchBlock<int>();
                switched.SetSelector(() => 1);
                switched.AddCase(1, Build);
                switched.Refresh();
                block = switched;
                break;
            default:
                var repeated = new ForEachBlock<int>();
                repeated.SetItems(() => new[] { 1, 2 });
                repeated.SetTemplate((_, scope) => Build(scope));
                repeated.Refresh();
                block = repeated;
                break;
        }

        var error = Assert.Throws<AggregateException>(block.Dispose);

        Assert.True(block.IsDisposed);
        Assert.Empty(block.Children);
        Assert.All(roots, child => Assert.True(child.IsDisposed));
        Assert.All(rootResources, resource => Assert.Equal(1, resource.DisposeCount));
        Assert.All(scopeResources, resource => Assert.Equal(1, resource.DisposeCount));
        Assert.Equal(rootResources.Count + scopeResources.Count, error.Flatten().InnerExceptions.Count);
        block.Dispose();
        Assert.All(rootResources.Concat(scopeResources), resource => Assert.Equal(1, resource.DisposeCount));
    }

    [Fact]
    public void ConstructionAbort_PreservesTheBuildFailureAndAllCleanupFailures()
    {
        var released = new List<string>();
        var construction = new UiConstructionScope();
        construction.Track(new Probe(new Resource("first cleanup", released, throws: true))).EnsureInitialized();
        construction.Track(new Probe(new Resource("second cleanup", released, throws: true))).EnsureInitialized();
        var original = new InvalidOperationException("build failed");

        var error = Assert.Throws<AggregateException>(() => construction.Abort(original));

        var failures = error.Flatten().InnerExceptions;
        Assert.Contains(original, failures);
        Assert.Contains(failures, item => item.Message == "first cleanup");
        Assert.Contains(failures, item => item.Message == "second cleanup");
        Assert.Equal(new[] { "second cleanup", "first cleanup" }, released);
        construction.Dispose();
        Assert.Equal(2, released.Count);
    }

    [Fact]
    public void ConstructionAbort_WithSuccessfulCleanupLetsTheCallerRethrowTheOriginal()
    {
        var released = new List<string>();
        var original = new InvalidOperationException("build failed");
        var child = new Probe(new Resource("child", released));

        var error = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var construction = new UiConstructionScope();
            try
            {
                construction.Track(child).EnsureInitialized();
                throw original;
            }
            catch (Exception failure)
            {
                construction.Abort(failure);
                throw;
            }
        }));

        Assert.Same(original, error);
        Assert.True(child.IsDisposed);
        Assert.Equal(new[] { "child" }, released);
    }
}
