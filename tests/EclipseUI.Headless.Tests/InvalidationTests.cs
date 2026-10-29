using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class InvalidationTests
{
    private class TestComponent : Component
    {
        public int RenderCount { get; private set; }
        public int ShouldRenderCount { get; private set; }
        public bool AllowRender { get; set; } = true;
        public Action? OnRender { get; set; }

        protected override Element Build() => new StackPanel();

        protected override bool ShouldRender()
        {
            ShouldRenderCount++;
            return AllowRender;
        }

        protected override void Render()
        {
            RenderCount++;
            OnRender?.Invoke();
        }
    }

    private sealed class ParentComponent : Component
    {
        public TestComponent Child { get; } = new();
        public List<string> Rendered { get; } = new();

        protected override Element Build()
        {
            var panel = new StackPanel();
            Track(Child);
            Child.OnRender = () => Rendered.Add("child");
            Child.Mount();
            panel.Children.Add(Child.Root);
            return panel;
        }

        protected override void Render() => Rendered.Add("parent");
    }

    [Fact]
    public void Invalidate_MergesMultipleRequestsIntoOneRender()
    {
        var component = new TestComponent();
        component.Mount();

        component.Invalidate();
        component.Invalidate();
        component.Invalidate();

        Assert.True(component.ProcessInvalidations());
        Assert.Equal(1, component.RenderCount);
    }

    [Fact]
    public void Invalidate_BeforeMountOrAfterDispose_IsNoOp()
    {
        var component = new TestComponent();
        component.Invalidate();
        Assert.False(component.ProcessInvalidations());
        Assert.Equal(0, component.RenderCount);

        component.Mount();
        component.Dispose();
        component.Invalidate();
        Assert.False(component.ProcessInvalidations());
        Assert.Equal(0, component.RenderCount);
    }

    [Fact]
    public void ShouldRenderFalse_ConsumesInvalidationWithoutRender()
    {
        var component = new TestComponent { AllowRender = false };
        component.Mount();
        component.Invalidate();

        Assert.False(component.ProcessInvalidations());
        Assert.Equal(1, component.ShouldRenderCount);
        Assert.Equal(0, component.RenderCount);

        Assert.False(component.ProcessInvalidations());
        Assert.Equal(1, component.ShouldRenderCount);
    }

    [Fact]
    public void ProcessInvalidations_RendersParentBeforeChild()
    {
        var parent = new ParentComponent();
        parent.Mount();

        parent.Child.Invalidate();
        parent.Invalidate();

        Assert.True(parent.ProcessInvalidations());
        Assert.Equal(new[] { "parent", "child" }, parent.Rendered);
    }

    [Fact]
    public void ProcessInvalidations_OnChild_ProcessesRootQueue()
    {
        var parent = new ParentComponent();
        parent.Mount();

        parent.Child.Invalidate();

        Assert.True(parent.Child.ProcessInvalidations());
        Assert.Equal(new[] { "child" }, parent.Rendered);
    }

    [Fact]
    public void Render_ReentrantInvalidate_IsProcessedInSameRound()
    {
        var component = new TestComponent();
        var reentered = false;
        component.OnRender = () =>
        {
            if (reentered) return;
            reentered = true;
            component.Invalidate();
        };
        component.Mount();

        component.Invalidate();
        Assert.True(component.ProcessInvalidations());
        Assert.Equal(2, component.RenderCount);
    }

    [Fact]
    public void Render_LoopBeyondCap_Throws()
    {
        var component = new TestComponent();
        component.OnRender = component.Invalidate;
        component.Mount();
        component.Invalidate();

        var error = Assert.Throws<InvalidOperationException>(() => component.ProcessInvalidations());
        Assert.Contains(nameof(TestComponent), error.Message);
    }

    [Fact]
    public void Invalidate_FromBackgroundThreads_IsCoalesced()
    {
        var component = new TestComponent();
        component.Mount();

        Parallel.For(0, 1_000, _ => component.Invalidate());

        Assert.True(component.ProcessInvalidations());
        Assert.Equal(1, component.RenderCount);
    }

    [Fact]
    public void HeadlessRoot_WithComponent_ProcessesInvalidationsBeforeLayout()
    {
        var component = new TestComponent();
        var host = new HeadlessRoot(component);

        component.Invalidate();
        host.UpdateLayout();

        Assert.Equal(1, component.RenderCount);
        Assert.Same(component, host.RootComponent);
    }

    [Fact]
    public void Adopt_RejectsSecondOwnerAndSelf()
    {
        var first = new TestComponent();
        var second = new TestComponent();
        var child = new TestComponent();

        first.Adopt(child);
        Assert.Throws<InvalidOperationException>(() => second.Adopt(child));
        Assert.Throws<InvalidOperationException>(() => first.Adopt(first));
    }
}
