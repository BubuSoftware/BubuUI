using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class VirtualizingLifecycleTests
{
    [Fact]
    public void ScrollingDisposesRetiredRowsAndReusesTheDetachedProbe()
    {
        var fixture = CreateVirtualized();
        using var viewer = fixture.Viewer;
        using var host = fixture.Host;
        host.UpdateLayout();
        var initial = fixture.Panel.Children.Cast<Probe>().ToArray();
        var probe = initial[0];

        viewer.ScrollToEnd();
        host.UpdateLayout();

        Assert.All(initial.Skip(1), AssertDisposedOnce);
        Assert.False(probe.IsAttached);
        Assert.False(probe.IsDisposed);
        Assert.Null(probe.Parent);
        Assert.Equal(1, probe.DetachedCount);
        viewer.ScrollToHome();
        host.UpdateLayout();

        Assert.Same(probe, fixture.Panel.Children[0]);
        Assert.True(probe.IsAttached);
        Assert.Equal(1, probe.InitializedCount);
        Assert.Equal(2, probe.AttachedCount);
        Assert.Equal(0, probe.DisposedCount);
        Assert.Single(fixture.Created, item => item.Index == 0);
    }

    [Fact]
    public void RefreshDisposesTheProbeAndRealizedRowsWithoutDuplicateDisposal()
    {
        var fixture = CreateVirtualized();
        using var viewer = fixture.Viewer;
        using var host = fixture.Host;
        host.UpdateLayout();
        var previous = fixture.Panel.Children.Cast<Probe>().ToArray();

        fixture.Panel.Refresh();

        Assert.Equal(0, fixture.Panel.RealizedCount);
        Assert.Empty(fixture.Panel.Children);
        Assert.All(previous, probe =>
        {
            AssertDisposedOnce(probe);
            Assert.Equal(1, probe.DisposeCalls);
        });
        host.UpdateLayout();
        Assert.All(fixture.Panel.Children, child => Assert.DoesNotContain(child, previous));
        Assert.Equal(2, fixture.Created.Count(item => item.Index == 0));
    }

    [Fact]
    public void RefreshContinuesCleanupAfterDetachAndDisposeFailures()
    {
        var fixture = CreateVirtualized();
        using var viewer = fixture.Viewer;
        using var host = fixture.Host;
        host.UpdateLayout();
        var previous = fixture.Panel.Children.Cast<Probe>().ToArray();
        previous[1].FailOnDetach = true;
        previous[2].FailOnDispose = true;

        var error = Assert.Throws<AggregateException>(() => fixture.Panel.Refresh());

        var failures = error.Flatten().InnerExceptions;
        Assert.Contains(failures, failure => failure.Message == "detach failed");
        Assert.Contains(failures, failure => failure.Message == "dispose failed");
        Assert.All(previous, AssertDisposedOnce);
        Assert.Equal(0, fixture.Panel.RealizedCount);
        Assert.Empty(fixture.Panel.Children);
        host.UpdateLayout();
        Assert.NotEmpty(fixture.Panel.Children);
        Assert.All(fixture.Panel.Children, child => Assert.False(child.IsDisposed));
    }

    [Fact]
    public void DisposingPanelAlsoDisposesTheCachedProbeOutsideItsContentTree()
    {
        var fixture = CreateVirtualized();
        using var viewer = fixture.Viewer;
        using var host = fixture.Host;
        host.UpdateLayout();
        var probe = Assert.Single(fixture.Created, item => item.Index == 0).Element;
        viewer.ScrollToEnd();
        host.UpdateLayout();
        Assert.False(probe.IsDisposed);
        Assert.Null(probe.Parent);
        var currentRows = fixture.Panel.Children.Cast<Probe>().ToArray();

        fixture.Panel.Dispose();
        fixture.Panel.Dispose();

        AssertDisposedOnce(probe);
        Assert.All(currentRows, AssertDisposedOnce);
        Assert.Equal(0, fixture.Panel.RealizedCount);
        Assert.Empty(fixture.Panel.Children);
        Assert.Empty(viewer.Children);
    }

    [Fact]
    public void WithoutScrollViewerRemeasureRetainsRowsAndCountChangesOnlyRetireMissingIndices()
    {
        var created = new List<(int Index, Probe Element)>();
        using var panel = new VirtualizingStackPanel { ItemCount = 3 };
        panel.ItemFactory = index =>
        {
            var probe = new Probe();
            created.Add((index, probe));
            return probe;
        };
        using var host = new HeadlessRoot(panel) { AvailableSize = new Size(400, 200) };
        host.UpdateLayout();
        var initial = panel.Children.Cast<Probe>().ToArray();

        panel.Gap = 5;
        host.AvailableSize = new Size(500, 200);
        host.UpdateLayout();

        Assert.Equal(initial, panel.Children.Cast<Probe>().ToArray());
        Assert.Equal(3, created.Count);
        Assert.All(initial, probe =>
        {
            Assert.Equal(1, probe.AttachedCount);
            Assert.Equal(0, probe.DetachedCount);
        });
        panel.ItemCount = 2;
        host.UpdateLayout();
        AssertDisposedOnce(initial[2]);
        panel.ItemCount = 4;
        host.UpdateLayout();

        Assert.Same(initial[0], panel.Children[0]);
        Assert.Same(initial[1], panel.Children[1]);
        Assert.NotSame(initial[2], panel.Children[2]);
        Assert.Equal(5, created.Count);
        Assert.Equal(1, initial[0].AttachedCount);
        Assert.Equal(0, initial[0].DetachedCount);
    }

    [Fact]
    public void MovingIntoScrollViewerAdoptsTheExistingItemZeroAsItsProbe()
    {
        var created = new List<Probe>();
        var panel = new VirtualizingStackPanel { ItemCount = 100 };
        panel.ItemFactory = _ =>
        {
            var probe = new Probe();
            created.Add(probe);
            return probe;
        };
        using var originalParent = new StackPanel();
        originalParent.Children.Add(panel);
        using var firstHost = new HeadlessRoot(originalParent) { AvailableSize = new Size(300, 80) };
        firstHost.UpdateLayout();
        var originalProbe = panel.Children[0];
        originalParent.Children.Remove(panel);
        using var viewer = new ScrollViewer();
        viewer.Children.Add(panel);
        using var secondHost = new HeadlessRoot(viewer) { AvailableSize = new Size(300, 80) };

        secondHost.UpdateLayout();

        Assert.Same(originalProbe, panel.Children[0]);
        Assert.Equal(100, created.Count);
        Assert.Equal(6, panel.RealizedCount);
        Assert.Equal(6, panel.Children.Count);
        Assert.All(created.Skip(6), probe => Assert.True(probe.IsDisposed));
        Assert.False(originalProbe.IsDisposed);
    }

    private static (VirtualizingStackPanel Panel, ScrollViewer Viewer, HeadlessRoot Host,
        List<(int Index, Probe Element)> Created) CreateVirtualized()
    {
        var created = new List<(int Index, Probe Element)>();
        var panel = new VirtualizingStackPanel { ItemCount = 100 };
        panel.ItemFactory = index =>
        {
            var probe = new Probe();
            created.Add((index, probe));
            return probe;
        };
        var viewer = new ScrollViewer();
        viewer.Children.Add(panel);
        var host = new HeadlessRoot(viewer) { AvailableSize = new Size(300, 80) };
        return (panel, viewer, host, created);
    }

    private static void AssertDisposedOnce(Probe probe)
    {
        Assert.True(probe.IsDisposed);
        Assert.False(probe.IsAttached);
        Assert.Null(probe.Parent);
        Assert.Equal(1, probe.DetachedCount);
        Assert.Equal(1, probe.DisposedCount);
    }

    private sealed class Probe : Panel
    {
        public int InitializedCount;
        public int AttachedCount;
        public int DetachedCount;
        public int DisposedCount;
        public int DisposeCalls;
        public bool FailOnDetach;
        public bool FailOnDispose;

        protected override void OnInitialized() => InitializedCount++;
        protected override void OnAttached() => AttachedCount++;
        protected override void OnDetached()
        {
            DetachedCount++;
            if (FailOnDetach) throw new InvalidOperationException("detach failed");
        }
        protected override void OnDispose()
        {
            DisposedCount++;
            base.OnDispose();
            if (FailOnDispose) throw new InvalidOperationException("dispose failed");
        }
        public override void Dispose()
        {
            DisposeCalls++;
            base.Dispose();
        }
        protected override Size MeasureCore(Size available) => new Size(100, 20);
        protected override void ArrangeCore(Rect final) { }
    }
}
