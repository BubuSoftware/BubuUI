using System;
using System.Collections.Generic;
using System.Linq;
using BubuUI;
using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

public class BlockLifecycleTests
{
    [Fact]
    public void ForEach_ReorderingRetainsNodesWithoutDetachingOrAttachingAgain()
    {
        var first = new Item("first");
        var second = new Item("second");
        var third = new Item("third");
        Item[] items = [first, second, third];
        var probes = new Dictionary<Item, Probe>();
        using var block = CreateList(() => items, probes);
        block.Refresh();
        using var host = new HeadlessRoot(block);
        var initial = block.Children.ToArray();

        items = [third, first, second];
        block.Refresh();
        block.Refresh();

        Assert.Equal(new[] { initial[2], initial[0], initial[1] }, block.Children.ToArray());
        Assert.All(probes.Values, probe =>
        {
            Assert.True(probe.IsAttached);
            Assert.Equal(1, probe.InitializedCount);
            Assert.Equal(1, probe.AttachedCount);
            Assert.Equal(0, probe.DetachedCount);
            Assert.Equal(0, probe.DisposedCount);
        });
    }

    [Fact]
    public void ForEach_AddRemoveAndSameKeyReplacementOnlyChangeAffectedLifetimes()
    {
        var first = new Item("first");
        var retained = new Item("retained");
        Item[] items = [first, retained];
        var probes = new Dictionary<Item, Probe>();
        using var block = CreateList(() => items, probes);
        block.Refresh();
        using var host = new HeadlessRoot(block);
        var retired = probes[first];
        var retainedProbe = probes[retained];
        var replacement = new Item("first");
        var added = new Item("added");

        items = [retained, replacement, added];
        block.Refresh();

        Assert.Equal(new Element[] { retainedProbe, probes[replacement], probes[added] }, block.Children.ToArray());
        AssertEndedOnce(retired);
        Assert.Equal(1, retainedProbe.AttachedCount);
        Assert.Equal(0, retainedProbe.DetachedCount);
        Assert.Equal(0, retainedProbe.DisposedCount);
        Assert.Equal(1, probes[replacement].AttachedCount);
        Assert.Equal(1, probes[added].AttachedCount);

        items = [retained];
        block.Refresh();

        AssertEndedOnce(probes[replacement]);
        AssertEndedOnce(probes[added]);
        Assert.Same(retainedProbe, Assert.Single(block.Children));
        Assert.Equal(1, retainedProbe.AttachedCount);
        Assert.Equal(0, retainedProbe.DetachedCount);
    }

    [Fact]
    public void ForEach_DetachFailureStillNotifiesAndDisposesOtherRetiredEntries()
    {
        var first = new Item("first");
        var second = new Item("second");
        Item[] items = [first, second];
        var probes = new Dictionary<Item, Probe>();
        using var block = CreateList(() => items, probes);
        block.Refresh();
        using var host = new HeadlessRoot(block);
        probes[first].FailOnDetach = true;
        var replacement = new Item("new");

        items = [replacement];
        var error = Assert.Throws<AggregateException>(() => block.Refresh());

        Assert.Contains(error.Flatten().InnerExceptions, failure => failure.Message == "detach failed");
        AssertEndedOnce(probes[first]);
        AssertEndedOnce(probes[second]);
        Assert.Same(probes[replacement], Assert.Single(block.Children));
        Assert.True(probes[replacement].IsAttached);
        Assert.Equal(1, probes[replacement].AttachedCount);
    }

    [Fact]
    public void ForEach_RemovalNotifiesDescendantsAndSiblingsInReverseContentOrder()
    {
        int[] items = [1, 2];
        var order = new List<string>();
        using var block = new ForEachBlock<int>();
        block.SetItems(() => items);
        block.SetTemplate((item, scope) =>
        {
            var parent = new Probe { DetachedAction = () => order.Add($"{item}:parent") };
            var child = new Probe { DetachedAction = () => order.Add($"{item}:child") };
            parent.Children.Add(child);
            scope.AddRoot(parent);
        });
        block.Refresh();
        using var host = new HeadlessRoot(block);

        items = [];
        block.Refresh();

        Assert.Equal(new[] { "2:child", "2:parent", "1:child", "1:parent" }, order);
        Assert.Empty(block.Children);
    }

    [Fact]
    public void Conditional_ChangedBranchDetachesAndDisposesEachInstanceOnce()
    {
        var active = true;
        var probes = new List<Probe>();
        using var block = new ConditionalBlock();
        block.SetCondition(() => active);
        block.SetContent(scope =>
        {
            var probe = new Probe();
            probes.Add(probe);
            scope.AddRoot(probe);
        });
        block.Refresh();
        using var host = new HeadlessRoot(block);

        block.Refresh();
        Assert.Equal(1, probes[0].AttachedCount);
        active = false;
        block.Refresh();

        Assert.Empty(block.Children);
        AssertEndedOnce(probes[0]);
        active = true;
        block.Refresh();
        Assert.Equal(2, probes.Count);
        Assert.Equal(1, probes[1].AttachedCount);

        block.Dispose();
        block.Dispose();

        Assert.All(probes, AssertEndedOnce);
    }

    [Fact]
    public void Switch_ChangedCaseAndDefaultDetachAndDisposeEachInstanceOnce()
    {
        var selected = 1;
        var probes = new List<Probe>();
        using var block = new SwitchBlock<int>();
        block.SetSelector(() => selected);
        void Build(BlockScope scope)
        {
            var probe = new Probe();
            probes.Add(probe);
            scope.AddRoot(probe);
        }
        block.AddCase(1, Build);
        block.AddCase(2, Build);
        block.SetDefault(Build);
        block.Refresh();
        using var host = new HeadlessRoot(block);

        selected = 2;
        block.Refresh();
        AssertEndedOnce(probes[0]);
        selected = 99;
        block.Refresh();
        AssertEndedOnce(probes[1]);
        block.Refresh();
        Assert.Equal(3, probes.Count);
        Assert.Equal(1, probes[2].AttachedCount);

        block.Dispose();
        block.Dispose();

        Assert.All(probes, AssertEndedOnce);
    }

    private static ForEachBlock<Item> CreateList(Func<IEnumerable<Item>> items, Dictionary<Item, Probe> probes)
    {
        var block = new ForEachBlock<Item>();
        block.SetItems(items);
        block.SetKeySelector(item => item.Key);
        block.SetTemplate((item, scope) =>
        {
            var probe = new Probe();
            probes.Add(item, probe);
            scope.AddRoot(probe);
        });
        return block;
    }

    private static void AssertEndedOnce(Probe probe)
    {
        Assert.False(probe.IsAttached);
        Assert.True(probe.IsDisposed);
        Assert.Null(probe.Parent);
        Assert.Equal(1, probe.AttachedCount);
        Assert.Equal(1, probe.DetachedCount);
        Assert.Equal(1, probe.DisposedCount);
    }

    private sealed class Item(string key)
    {
        public string Key { get; } = key;
    }

    private sealed class Probe : StackPanel
    {
        public int InitializedCount;
        public int AttachedCount;
        public int DetachedCount;
        public int DisposedCount;
        public bool FailOnDetach;
        public Action? DetachedAction;

        protected override void OnInitialized() => InitializedCount++;
        protected override void OnAttached() => AttachedCount++;

        protected override void OnDetached()
        {
            DetachedCount++;
            DetachedAction?.Invoke();
            if (FailOnDetach) throw new InvalidOperationException("detach failed");
        }

        protected override void OnDispose()
        {
            DisposedCount++;
            base.OnDispose();
        }
    }
}
