using System.Collections.Generic;
using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

/// <summary>
/// 真实内容树挂接时迁移已入队失效；深度按父链实时计算。
/// 规范条文：spec/03 §2.4/§2.7。
/// </summary>
public class ComponentOwnershipTests
{
    private sealed class Probe : global::BubuUI.Elements.StackPanel
    {
        private readonly List<string>? _log;
        private readonly string _name;

        public Probe(string name, List<string>? log = null)
        {
            _name = name;
            _log = log;
        }

        public int Renders { get; private set; }

        protected override void Render()
        {
            Renders++;
            _log?.Add(_name);
        }
    }

    private sealed class Owner : global::BubuUI.Elements.StackPanel
    {
        private readonly Element[] _children;

        public Owner(params Element[] children) => _children = children;

        protected override void InitializeUi()
        {
            foreach (var child in _children)
                Children.Add(child);
        }
    }

    [Fact]
    public void InvalidateBeforeAdopt_StillRenderedByNewRoot()
    {
        var child = new Probe("child");
        child.EnsureInitialized();
        child.Invalidate();

        var root = new Owner(child);
        root.EnsureInitialized();
        Assert.True(root.ProcessInvalidations());
        Assert.Equal(1, child.Renders);
    }

    [Fact]
    public void PendingDescendant_MigratesWithAdoptedChild()
    {
        var grandchild = new Probe("grandchild");
        grandchild.EnsureInitialized();
        var child = new Owner(grandchild);
        child.EnsureInitialized();
        grandchild.Invalidate(); // 入队到 child 自己的根队列

        var root = new Owner(child);
        root.EnsureInitialized();
        Assert.True(root.ProcessInvalidations());
        Assert.Equal(1, grandchild.Renders);
    }

    [Fact]
    public void LateAdoption_KeepsParentBeforeChildOrder()
    {
        var log = new List<string>();
        var grandchild = new Probe("grandchild", log);
        grandchild.EnsureInitialized();
        var child = new Probe("child", log);
        child.EnsureInitialized();
        child.Children.Add(grandchild);
        grandchild.Invalidate(); // 先入队孙：旧实现同深度按入队序会把孙排在父前面
        child.Invalidate();

        var root = new Owner(child); // child 深度变 1，grandchild 应为 2
        root.EnsureInitialized();
        root.ProcessInvalidations();
        Assert.Equal(new[] { "child", "grandchild" }, log);
    }

    [Fact]
    public void Reparent_DoesNotDuplicatePendingRender()
    {
        var child = new Probe("child");
        child.EnsureInitialized();
        var root = new Owner(child);
        root.EnsureInitialized();
        child.Invalidate();
        root.Children.Remove(child);
        root.Children.Add(child);
        root.ProcessInvalidations();
        Assert.Equal(1, child.Renders);
    }

    [Fact]
    public void MigratedPending_CoalescesWithNewRootInvalidation()
    {
        var child = new Probe("child");
        child.EnsureInitialized();
        child.Invalidate();

        var root = new Owner(child);
        root.EnsureInitialized();
        child.Invalidate(); // 接管后直接入新根队列，应与迁移来的失效合并
        root.ProcessInvalidations();
        Assert.Equal(1, child.Renders);
    }
}
