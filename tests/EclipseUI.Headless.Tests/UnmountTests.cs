using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// 通用终止入口：摘除活动子树、释放资源与绑定，并在下一次布局修剪焦点。
/// </summary>
public class UnmountTests
{
    private sealed class ProbeComponent : global::EclipseUI.Elements.StackPanel
    {
        public bool Source;

        protected override void InitializeUi()
        {
            var check = new CheckBox();
            Children.Add(check);
            Track(UiBinding.TwoWay<bool>(
                check, CheckBox.CheckedProperty,
                () => check.Checked, v => check.Checked = v,
                () => Source, v => Source = v,
                Invalidate));
        }
    }

    // ---- 1. 通用摘除钩子 ----

    [Fact]
    public void Unmount_NotifiesAttachedSubtree_BottomUp()
    {
        var order = new List<Element>();
        var panel = new HookPanel { Detached = element => order.Add(element) };
        var button = new HookPanel { Detached = element => order.Add(element) };
        var text = new TextBlock();
        panel.Children.Add(button);
        panel.Children.Add(text);
        using var host = new HeadlessRoot(panel);

        panel.Unmount();

        Assert.Null(panel.Parent);
        Assert.Equal(2, order.Count);
        Assert.Same(button, order[0]);
        Assert.Same(panel, order[1]);
    }

    [Fact]
    public void Unmount_DetachesFromParent_AndIsIdempotent()
    {
        var root = new StackPanel();
        var child = new StackPanel();
        root.Children.Add(child);

        child.Unmount();
        Assert.DoesNotContain(child, root.Children);
        Assert.Null(child.Parent);

        child.Unmount(); // 已脱离父级：幂等，不抛
        Assert.Null(child.Parent);
    }

    [Fact]
    public void OnDetached_HookRuns_WithRemovedRootParentAlreadyNull()
    {
        var panel = new HookPanel();
        var root = new StackPanel();
        root.Children.Add(panel);
        using var host = new HeadlessRoot(root);

        panel.Unmount();

        Assert.Contains("hook", panel.Log);
        Assert.Null(panel.ParentAtDetach);
        Assert.Null(panel.Parent);
    }

    private sealed class HookPanel : StackPanel
    {
        public List<string> Log { get; } = new();
        public Element? ParentAtDetach;
        public Action<Element>? Detached;

        protected override void OnDetached()
        {
            ParentAtDetach = Parent;
            Log.Add("hook");
            Detached?.Invoke(this);
            base.OnDetached();
        }
    }

    // ---- 2. 组件根卸载 → 释放 ----

    [Fact]
    public void Unmount_ComponentRoot_DisposesComponent()
    {
        var component = new ProbeComponent();
        component.EnsureInitialized();

        component.Unmount();

        Assert.True(component.IsDisposed);
        Assert.True(component.IsInitialized);
    }

    [Fact]
    public void Unmount_ComponentRoot_ReleasesBindingSubscription()
    {
        var component = new ProbeComponent();
        component.EnsureInitialized();
        var check = component.Descendants<CheckBox>().Single();

        check.Toggle(); // 绑定生效：回写 Source
        Assert.True(component.Source);

        component.Unmount();
        Assert.True(component.IsDisposed);

        check.Toggle(); // 绑定已随 Dispose 解除：Checked 自由翻转但不回写 Source，也不抛
        Assert.False(check.Checked);
        Assert.True(component.Source);
    }

    [Fact]
    public void Unmount_NestedTrackedComponents_DisposesAll()
    {
        var parent = new ParentProbe();
        parent.EnsureInitialized();

        parent.Unmount();

        Assert.True(parent.IsDisposed);
        Assert.True(parent.Child.IsDisposed);
    }

    private sealed class ParentProbe : global::EclipseUI.Elements.StackPanel
    {
        public ProbeComponent Child = new();

        protected override void InitializeUi()
        {
            Children.Add(Child);
        }
    }

    // ---- 3. 原语义锁定 ----

    [Fact]
    public void Remove_WithoutUnmount_DoesNotDisposeComponent()
    {
        var component = new ProbeComponent();
        component.EnsureInitialized();
        var host = new StackPanel();
        host.Children.Add(component);

        host.Children.Remove(component);

        Assert.False(component.IsDisposed); // 手工摘除仍不触发释放（K33 保守口径）
    }

    // ---- 4. 焦点离树修剪 ----

    [Fact]
    public void FocusInsideUnmountedSubtree_IsPrunedOnNextLayout()
    {
        var component = new ProbeComponent();
        component.EnsureInitialized();
        var root = new HeadlessRoot(component);
        root.UpdateLayout();

        var button = new Button();
        component.Children.Add(button);
        root.UpdateLayout();
        Assert.True(root.Focus.Focus(button));
        Assert.Same(button, root.Focus.Focused);

        button.Unmount();
        root.UpdateLayout(); // 布局前修剪

        Assert.Null(root.Focus.Focused);
    }

    [Fact]
    public void ClearFocusIfDetached_KeepsFocusInsideRoot()
    {
        var panel = new StackPanel();
        var button = new Button();
        panel.Children.Add(button);
        var focus = new FocusManager(panel);
        Assert.True(focus.Focus(button));

        Assert.False(focus.ClearFocusIfDetached()); // 焦点仍在根子树内：不清
        Assert.Same(button, focus.Focused);

        var detached = new Button();
        Assert.True(focus.Focus(detached)); // 焦点移到离树元素（构造场景）
        Assert.True(focus.ClearFocusIfDetached());
        Assert.Null(focus.Focused);
    }
}
