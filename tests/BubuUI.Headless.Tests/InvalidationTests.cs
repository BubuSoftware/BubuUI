using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BubuUI;
using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

public class InvalidationTests
{
    private class TestComponent : global::BubuUI.Elements.StackPanel
    {
        public int RenderCount { get; private set; }
        public int ShouldRenderCount { get; private set; }
        public bool AllowRender { get; set; } = true;
        public Action? OnRender { get; set; }

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

    private sealed class ParentComponent : global::BubuUI.Elements.StackPanel
    {
        public TestComponent Child { get; } = new();
        public List<string> Rendered { get; } = new();

        protected override void InitializeUi()
        {
            Child.OnRender = () => Rendered.Add("child");
            Children.Add(Child);
        }

        protected override void Render() => Rendered.Add("parent");
    }

    [Fact]
    public void Invalidate_MergesMultipleRequestsIntoOneRender()
    {
        var component = new TestComponent();
        component.EnsureInitialized();

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

        component.EnsureInitialized();
        component.Dispose();
        component.Invalidate();
        Assert.False(component.ProcessInvalidations());
        Assert.Equal(0, component.RenderCount);
    }

    [Fact]
    public void ShouldRenderFalse_ConsumesInvalidationWithoutRender()
    {
        var component = new TestComponent { AllowRender = false };
        component.EnsureInitialized();
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
        parent.EnsureInitialized();

        parent.Child.Invalidate();
        parent.Invalidate();

        Assert.True(parent.ProcessInvalidations());
        Assert.Equal(new[] { "parent", "child" }, parent.Rendered);
    }

    [Fact]
    public void ProcessInvalidations_OnChild_ProcessesRootQueue()
    {
        var parent = new ParentComponent();
        parent.EnsureInitialized();

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
        component.EnsureInitialized();

        component.Invalidate();
        Assert.True(component.ProcessInvalidations());
        Assert.Equal(2, component.RenderCount);
    }

    [Fact]
    public void Render_LoopBeyondCap_Throws()
    {
        var component = new TestComponent();
        component.OnRender = component.Invalidate;
        component.EnsureInitialized();
        component.Invalidate();

        var error = Assert.Throws<InvalidOperationException>(() => component.ProcessInvalidations());
        Assert.Contains(nameof(TestComponent), error.Message);
    }

    [Fact]
    public void Invalidate_FromBackgroundThreads_IsCoalesced()
    {
        var component = new TestComponent();
        component.EnsureInitialized();

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
        Assert.Same(component, host.Content);
    }

    [Fact]
    public void Children_RejectsSecondParentAndSelf()
    {
        var first = new TestComponent();
        var second = new TestComponent();
        var child = new TestComponent();

        first.Children.Add(child);
        Assert.Throws<InvalidOperationException>(() => second.Children.Add(child));
        Assert.Throws<InvalidOperationException>(() => first.Children.Add(first));
    }

    // spec/03 §2.6：一轮 ProcessInvalidations() 必须有迭代上限（实现定义 100 次 Render），
    // 超过上限视为渲染死循环并 fail-fast。S5 最终审计（D96）补测试锁住该契约。
    [Fact]
    public void RenderLoop_SelfInvalidatingRender_ThrowsAtIterationLimit()
    {
        var component = new TestComponent();
        component.EnsureInitialized();
        component.OnRender = component.Invalidate; // 每次渲染再自我失效 → 死循环
        component.Invalidate();                    // 启动第一次失效

        var ex = Assert.Throws<InvalidOperationException>(() => component.ProcessInvalidations());
        Assert.Contains("UI update loop exceeded", ex.Message);
        // 上限口径 = 100 次 Render；第 101 次进入前拦截
        Assert.Equal(100, component.RenderCount);
    }

    [Fact]
    public void RenderLoop_ExactlyAtIterationLimit_ConvergesWithoutThrow()
    {
        var component = new TestComponent();
        component.EnsureInitialized();
        var remaining = 100; // 前 99 次 Render 各自失效，第 100 次停 → 恰好在上限内收敛
        component.OnRender = () =>
        {
            if (--remaining > 0)
                component.Invalidate();
        };
        component.Invalidate(); // 启动第一次失效

        Assert.True(component.ProcessInvalidations());
        Assert.Equal(100, component.RenderCount);
    }
}
