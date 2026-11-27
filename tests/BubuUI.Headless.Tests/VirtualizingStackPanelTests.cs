using System;
using System.Collections.Generic;
using BubuUI;
using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

/// <summary>
/// G4 布局切片：VirtualizingStackPanel 列表虚拟化——只实例化视口窗口（含上下 1 缓冲）、
/// 滚动同步窗口、无滚动祖先时退化全量、Gap 行距、Refresh 重建。
/// </summary>
public class VirtualizingStackPanelTests
{
    [Fact]
    public void RealizesOnlyVisibleWindow_NotAllItems()
    {
        var (panel, sv, root, calls) = Build(itemCount: 1000, viewportHeight: 800, itemHeight: 40);
        root.UpdateLayout();

        // 窗口 = floor(0)-1 .. ceil(800/40)+1 → 0..21 = 22 个容器（而非 1000）
        Assert.Equal(22, panel.RealizedCount);
        Assert.Equal(22, calls.Count);
        Assert.Equal(40000, sv.ExtentHeight, 5);      // 1000 × 40
        Assert.Equal(800, sv.ViewportHeight, 5);

        // item 0 在窗口内，落位 0；item 21 落位 21×40
        Assert.Equal(0, panel.Children[0].FinalRect.Y, 5);
        Assert.Equal(21 * 40, panel.Children[21].FinalRect.Y, 5);
    }

    [Fact]
    public void RealizedCount_IsIndependentOfItemCount()
    {
        var (panel, sv, root, _) = Build(itemCount: 100000, viewportHeight: 800, itemHeight: 40);
        root.UpdateLayout();

        Assert.Equal(22, panel.RealizedCount);        // 10 万行仍然只实例化 22 个
        Assert.Equal(4000000, sv.ExtentHeight, 5);
    }

    [Fact]
    public void ScrollToEnd_SyncsWindow()
    {
        var (panel, sv, root, _) = Build(itemCount: 1000, viewportHeight: 800, itemHeight: 40);
        root.UpdateLayout();

        sv.ScrollToEnd();
        root.UpdateLayout();                          // 偏移失效 → 重排 → 窗口同步

        // offset 39200 → lo = floor(39200/40)-1 = 979, hi = 999 → 21 个容器
        Assert.Equal(21, panel.RealizedCount);
        Assert.Equal(979 * 40, panel.Children[0].FinalRect.Y, 5);   // 上缓冲行（视口上方 -40）
        // 视口底部对齐：末行 index 999 绝对 y = 39960 - 39200 = 760 = 800 - 40
        double absolute = panel.Children[20].FinalRect.Y;
        for (var node = panel.Children[20].Parent; node is not null; node = node.Parent)
            absolute += node.FinalRect.Y;
        Assert.Equal(760, absolute, 5);
    }

    [Fact]
    public void WheelScroll_AdvancesWindow()
    {
        var (panel, sv, root, _) = Build(itemCount: 1000, viewportHeight: 800, itemHeight: 40);
        root.UpdateLayout();

        root.PointerWheel(new Point(200, 400), -120);  // 下滚一格 = +48px
        root.UpdateLayout();

        Assert.Equal(48, sv.VerticalOffset, 5);
        // lo = floor(48/40)-1 = 0, hi = ceil(848/40)+1 = 23 → 0..23 = 24 个容器
        Assert.Equal(24, panel.RealizedCount);
        Assert.Equal(23 * 40, panel.Children[23].FinalRect.Y, 5);
    }

    [Fact]
    public void Degrade_WithoutScrollViewer_RealizesAll_LikeStackPanel()
    {
        var calls = new List<int>();
        var panel = new VirtualizingStackPanel { ItemCount = 50 };
        panel.ItemFactory = i => { calls.Add(i); return new FixedBox(100, 40); };

        var stack = new StackPanel();
        stack.Children.Add(panel);
        var root = new HeadlessRoot(stack) { AvailableSize = new Size(400, 2000) };
        root.UpdateLayout();

        // 无滚动祖先：全量实例化（变高可用），布局与 StackPanel 一致
        Assert.Equal(50, panel.RealizedCount);
        Assert.Equal(50, calls.Count);
        Assert.Equal(0, panel.Children[0].FinalRect.Y, 5);
        Assert.Equal(40, panel.Children[1].FinalRect.Y, 5);
        Assert.Equal(2000, panel.DesiredSize.Height, 5);
    }

    [Fact]
    public void Gap_SpacesStride_AndExtent()
    {
        var (panel, sv, root, _) = Build(itemCount: 10, viewportHeight: 200, itemHeight: 40, gap: 6);
        root.UpdateLayout();

        Assert.Equal(46, panel.UniformItemHeight + 6, 5);
        Assert.Equal(46 * 10 - 6, sv.ExtentHeight, 5);            // stride × n - gap
        Assert.Equal(46, panel.Children[1].FinalRect.Y, 5);       // 行位含 gap
        // hi = ceil(200/46)+1 = 6 → 0..6 = 7 个容器
        Assert.Equal(7, panel.RealizedCount);
    }

    [Fact]
    public void ItemCountChange_AndRefresh_Rebuild()
    {
        var (panel, sv, root, calls) = Build(itemCount: 100, viewportHeight: 800, itemHeight: 40);
        root.UpdateLayout();

        panel.ItemCount = 5;                          // Measure 失效 → extent 收缩
        root.UpdateLayout();
        Assert.Equal(200, sv.ExtentHeight, 5);
        Assert.Equal(5, panel.RealizedCount);

        var callsAfterRefresh = calls.Count;
        panel.Refresh();                              // 换数据/工厂后的强制重建
        root.UpdateLayout();
        Assert.True(calls.Count > callsAfterRefresh); // 重新走工厂
        Assert.Equal(5, panel.RealizedCount);
        Assert.Equal(0, panel.Children[0].FinalRect.Y, 5);
    }

    private static (VirtualizingStackPanel Panel, ScrollViewer Viewer, HeadlessRoot Root, List<int> Calls)
        Build(int itemCount, double viewportHeight, double itemHeight, double gap = 0)
    {
        var calls = new List<int>();
        var panel = new VirtualizingStackPanel { Gap = gap, ItemCount = itemCount };
        panel.ItemFactory = i => { calls.Add(i); return new FixedBox(300, itemHeight); };

        var viewer = new ScrollViewer();
        viewer.Children.Add(panel);
        var root = new HeadlessRoot(viewer) { AvailableSize = new Size(400, viewportHeight) };
        return (panel, viewer, root, calls);
    }

    /// <summary>固定期望尺寸的探针元素（绕过 Measure 递归）。</summary>
    private sealed class FixedBox : Panel
    {
        private readonly Size _size;
        public FixedBox(double width, double height) => _size = new Size(width, height);

        // 跨程序集重写 protected internal 成员只能收窄为 protected（C# 规则）。
        protected override Size MeasureCore(Size available) => _size;

        protected override void ArrangeCore(Rect final)
        {
        }
    }
}
