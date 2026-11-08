using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// G4/K33 卸载链路回归：
///  1) Element.Unmount 子树级 UnmountedEvent（Direct，自上而下）与 OnUnmount 钩子；
///  2) 组件根被卸载 → 组件 Dispose → Track 的资源/绑定随之解除（修复 v1「手工移除不释放」限制）；
///  3) 直接 Children.Remove 仍不触发释放（保守原语义，锁定契约）；
///  4) 焦点离树修剪（FocusManager.ClearFocusIfDetached，宿主布局前调用）。
/// </summary>
public class UnmountTests
{
    private sealed class ProbeComponent : Component
    {
        public bool Source;

        protected override Element Build()
        {
            var panel = new StackPanel();
            var check = new CheckBox();
            panel.Children.Add(check);
            Track(UiBinding.TwoWay<bool>(
                check, CheckBox.CheckedProperty,
                () => check.Checked, v => check.Checked = v,
                () => Source, v => Source = v,
                Invalidate));
            return panel;
        }
    }

    // ---- 1. 事件与钩子 ----

    [Fact]
    public void Unmount_RaisesEventOnEverySubtreeElement_TopDown()
    {
        var panel = new StackPanel();
        var button = new Button();
        var text = new TextBlock();
        panel.Children.Add(button);
        panel.Children.Add(text);

        var order = new List<Element>();
        panel.AddHandler(Element.UnmountedEvent, e => order.Add(e.Source!));
        button.AddHandler(Element.UnmountedEvent, e => order.Add(e.Source!));

        panel.Unmount();

        Assert.Null(panel.Parent);
        Assert.Equal(2, order.Count);
        Assert.Same(panel, order[0]); // 自上而下：先父后子
        Assert.Same(button, order[1]);
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
    public void OnUnmount_HookRuns_WithParentAlreadyNull()
    {
        var panel = new HookPanel();
        var root = new StackPanel();
        root.Children.Add(panel);

        panel.Unmount();

        Assert.Contains("hook", panel.Log);
        Assert.Null(panel.ParentAtUnmount); // 钩子触发时已摘除
        Assert.Null(panel.Parent);
    }

    private sealed class HookPanel : StackPanel
    {
        public List<string> Log { get; } = new();
        public Element? ParentAtUnmount;

        protected override void OnUnmount()
        {
            ParentAtUnmount = Parent;
            Log.Add("hook");
        }
    }

    // ---- 2. 组件根卸载 → 释放 ----

    [Fact]
    public void Unmount_ComponentRoot_DisposesComponent()
    {
        var component = new ProbeComponent();
        component.Mount();

        component.Root.Unmount();

        Assert.True(component.IsDisposed);
        Assert.False(component.IsMounted);
    }

    [Fact]
    public void Unmount_ComponentRoot_ReleasesBindingSubscription()
    {
        var component = new ProbeComponent();
        component.Mount();
        var check = component.Root.Descendants<CheckBox>().Single();

        check.Toggle(); // 绑定生效：回写 Source
        Assert.True(component.Source);

        component.Root.Unmount();
        Assert.True(component.IsDisposed);

        check.Toggle(); // 绑定已随 Dispose 解除：Checked 自由翻转但不回写 Source，也不抛
        Assert.False(check.Checked);
        Assert.True(component.Source);
    }

    [Fact]
    public void Unmount_NestedTrackedComponents_DisposesAll()
    {
        var parent = new ParentProbe();
        parent.Mount();

        parent.Root.Unmount();

        Assert.True(parent.IsDisposed);
        Assert.True(parent.Child.IsDisposed);
    }

    private sealed class ParentProbe : Component
    {
        public ProbeComponent Child = new();

        protected override Element Build()
        {
            Child.Mount();
            Track(Child);
            var panel = new StackPanel();
            panel.Children.Add(Child.Root);
            return panel;
        }
    }

    // ---- 3. 原语义锁定 ----

    [Fact]
    public void Remove_WithoutUnmount_DoesNotDisposeComponent()
    {
        var component = new ProbeComponent();
        component.Mount();
        var host = new StackPanel();
        host.Children.Add(component.Root);

        host.Children.Remove(component.Root);

        Assert.False(component.IsDisposed); // 手工摘除仍不触发释放（K33 保守口径）
    }

    // ---- 4. 焦点离树修剪 ----

    [Fact]
    public void FocusInsideUnmountedSubtree_IsPrunedOnNextLayout()
    {
        var component = new ProbeComponent();
        component.Mount();
        var root = new HeadlessRoot(component);
        root.UpdateLayout();

        var button = new Button();
        Assert.IsType<StackPanel>(component.Root).Children.Add(button);
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
