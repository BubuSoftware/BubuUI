using System.Collections.Generic;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// D78：Adopt 接管时迁移已入队失效；深度按所有权链实时计算（接管后不残留旧深度）。
/// 规范条文：spec/03 §2.4/§2.7。
/// </summary>
public class ComponentOwnershipTests
{
    private sealed class Probe : Component
    {
        private readonly List<string>? _log;
        private readonly string _name;

        public Probe(string name, List<string>? log = null)
        {
            _name = name;
            _log = log;
        }

        public int Renders { get; private set; }

        protected override Element Build() => new StackPanel();

        protected override void Render()
        {
            Renders++;
            _log?.Add(_name);
        }
    }

    private sealed class Owner : Component
    {
        private readonly Component[] _children;

        public Owner(params Component[] children) => _children = children;

        protected override Element Build()
        {
            foreach (var child in _children)
                Track(child);
            return new StackPanel();
        }
    }

    [Fact]
    public void InvalidateBeforeAdopt_StillRenderedByNewRoot()
    {
        var child = new Probe("child");
        child.Mount();
        child.Invalidate();

        var root = new Owner(child);
        root.Mount();
        Assert.True(root.ProcessInvalidations());
        Assert.Equal(1, child.Renders);
    }

    [Fact]
    public void PendingDescendant_MigratesWithAdoptedChild()
    {
        var grandchild = new Probe("grandchild");
        grandchild.Mount();
        var child = new Owner(grandchild);
        child.Mount();
        grandchild.Invalidate(); // 入队到 child 自己的根队列

        var root = new Owner(child);
        root.Mount();
        Assert.True(root.ProcessInvalidations());
        Assert.Equal(1, grandchild.Renders);
    }

    [Fact]
    public void LateAdoption_KeepsParentBeforeChildOrder()
    {
        var log = new List<string>();
        var grandchild = new Probe("grandchild", log);
        grandchild.Mount();
        var child = new Probe("child", log);
        child.Mount();
        child.Adopt(grandchild); // child 深度 0 时接管 → 旧实现 grandchild 深度缓存为 1
        grandchild.Invalidate(); // 先入队孙：旧实现同深度按入队序会把孙排在父前面
        child.Invalidate();

        var root = new Owner(child); // child 深度变 1，grandchild 应为 2
        root.Mount();
        root.ProcessInvalidations();
        Assert.Equal(new[] { "child", "grandchild" }, log);
    }

    [Fact]
    public void Adopt_Idempotent_DoesNotDuplicatePendingRender()
    {
        var child = new Probe("child");
        child.Mount();
        var root = new Owner(child);
        root.Mount();
        child.Invalidate();
        root.Adopt(child);
        root.ProcessInvalidations();
        Assert.Equal(1, child.Renders);
    }

    [Fact]
    public void MigratedPending_CoalescesWithNewRootInvalidation()
    {
        var child = new Probe("child");
        child.Mount();
        child.Invalidate();

        var root = new Owner(child);
        root.Mount();
        child.Invalidate(); // 接管后直接入新根队列，应与迁移来的失效合并
        root.ProcessInvalidations();
        Assert.Equal(1, child.Renders);
    }
}