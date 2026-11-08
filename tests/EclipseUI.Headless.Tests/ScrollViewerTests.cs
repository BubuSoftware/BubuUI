using System;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// G4 布局切片：ScrollViewer 滚动与裁剪——extent/viewport 语义、偏移钳制、
/// 滚轮冒泡路由与 Handled 消费、约束父容器下的可用性。
/// </summary>
public class ScrollViewerTests
{
    [Fact]
    public void Overflow_ExtentViewport_Clamp_AndShiftContent()
    {
        var (sv, content, root) = BuildViewer(itemCount: 30, viewportHeight: 800);
        root.UpdateLayout();

        Assert.Equal(1200, sv.ExtentHeight, 5);   // 30 × 40
        Assert.Equal(800, sv.ViewportHeight, 5);
        Assert.Equal(400, sv.ScrollableHeight, 5);
        Assert.Equal(0, content.FinalRect.Y, 5);

        sv.ScrollToEnd();
        root.UpdateLayout();                      // 偏移变更 → 下一帧重排
        Assert.Equal(400, sv.VerticalOffset, 5);
        Assert.Equal(-400, content.FinalRect.Y, 5);

        // 越界钳制：ScrollTo 收敛到边界
        sv.ScrollToVerticalOffset(99999);
        Assert.Equal(400, sv.VerticalOffset, 5);
        sv.ScrollToVerticalOffset(-50);
        Assert.Equal(0, sv.VerticalOffset, 5);
        root.UpdateLayout();
        Assert.Equal(0, content.FinalRect.Y, 5);
    }

    [Fact]
    public void Wheel_OverContent_ScrollsViewer_AndSetsHandled()
    {
        var (sv, content, root) = BuildViewer(itemCount: 30, viewportHeight: 800);

        var viewer = sv;
        var lateHandlerFired = false;
        // 同节点后注册的 handler：SV 构造器 handler 消费后不得再触发（节点内逐 handler 检查 Handled）。
        viewer.AddHandler(Element.PointerWheelChangedEvent, () => lateHandlerFired = true);

        sv.VerticalOffset = 200;
        root.UpdateLayout();

        // 命中第 6 个盒子（绝对 y=10 的可见内容）：delta 120（上滚）→ offset 减小 48
        root.PointerWheel(new Point(50, 10), 120);
        root.UpdateLayout();
        Assert.Equal(152, sv.VerticalOffset, 5);
        Assert.Equal(-152, content.FinalRect.Y, 5);
        Assert.False(lateHandlerFired);

        // 下滚（delta 为负）→ offset 增大
        root.PointerWheel(new Point(50, 10), -120);
        root.UpdateLayout();
        Assert.Equal(200, sv.VerticalOffset, 5);
    }

    [Fact]
    public void Wheel_NoOverflow_DoesNotHandle()
    {
        var (sv, _, root) = BuildViewer(itemCount: 10, viewportHeight: 800);   // 400 < 800

        var handled = false;
        sv.AddHandler(Element.PointerWheelChangedEvent, (RoutedEventArgs args) => handled = args.Handled);

        root.PointerWheel(new Point(50, 10), 120);

        Assert.Equal(0, sv.VerticalOffset, 5);
        Assert.False(handled);                    // 未消费，继续冒泡
    }

    [Fact]
    public void FixedHeightGridRow_ConstrainsViewport()
    {
        var sv = new ScrollViewer();
        var content = new StackPanel();
        for (int i = 0; i < 30; i++) content.Children.Add(new FixedBox(100, 40));
        sv.Children.Add(content);

        var grid = new Grid { RowDefinitions = "200" };
        grid.Children.Add(sv);
        var root = new HeadlessRoot(grid) { AvailableSize = new Size(400, 1000) };
        root.UpdateLayout();

        Assert.Equal(200, sv.FinalRect.Height, 5);
        Assert.Equal(200, sv.ViewportHeight, 5);
        Assert.Equal(1200, sv.ExtentHeight, 5);
        Assert.Equal(1000, sv.ScrollableHeight, 5);

        sv.ScrollToEnd();
        root.UpdateLayout();
        Assert.Equal(-1000, content.FinalRect.Y, 5);
    }

    [Fact]
    public void InfiniteAvailable_NoScroll_GrowsToContent()
    {
        var sv = new ScrollViewer();
        var content = new StackPanel();
        for (int i = 0; i < 30; i++) content.Children.Add(new FixedBox(100, 40));
        sv.Children.Add(content);

        var stack = new StackPanel();
        stack.Children.Add(sv);
        var root = new HeadlessRoot(stack) { AvailableSize = new Size(400, 2000) };
        root.UpdateLayout();

        // StackPanel 给无穷高约束：视口 = 内容期望，不出滚动
        Assert.Equal(1200, sv.ExtentHeight, 5);
        Assert.Equal(1200, sv.ViewportHeight, 5);
        Assert.Equal(0, sv.ScrollableHeight, 5);
    }

    [Fact]
    public void Padding_ShrinksViewport_AndOffsetsContent()
    {
        var sv = new ScrollViewer { Padding = 10 };
        var content = new StackPanel();
        for (int i = 0; i < 30; i++) content.Children.Add(new FixedBox(100, 40));
        sv.Children.Add(content);

        var root = new HeadlessRoot(sv) { AvailableSize = new Size(400, 800) };
        root.UpdateLayout();

        // 根按 DesiredSize 排列：视口宽 = min(可用 380, 内容 100)，高 = min(780, 1200)
        Assert.Equal(780, sv.ViewportHeight, 5);  // 800 - 2×10
        Assert.Equal(420, sv.ScrollableHeight, 5);
        Assert.Equal(new Rect(10, 10, 100, 1200), content.FinalRect);

        sv.ScrollToEnd();
        root.UpdateLayout();
        Assert.Equal(new Rect(10, -410, 100, 1200), content.FinalRect);   // 10 - 420
    }

    [Fact]
    public void EmptyViewer_IsSafe()
    {
        var sv = new ScrollViewer();
        var root = new HeadlessRoot(sv) { AvailableSize = new Size(400, 300) };
        root.UpdateLayout();

        Assert.Equal(0, sv.ExtentHeight, 5);
        Assert.Equal(0, sv.ScrollableHeight, 5);
        Assert.Equal(0, sv.VerticalOffset, 5);
    }

    private static (ScrollViewer Viewer, StackPanel Content, HeadlessRoot Root) BuildViewer(
        int itemCount, double viewportHeight)
    {
        var sv = new ScrollViewer();
        var content = new StackPanel();
        for (int i = 0; i < itemCount; i++) content.Children.Add(new FixedBox(100, 40));
        sv.Children.Add(content);
        var root = new HeadlessRoot(sv)
        {
            AvailableSize = new Size(400, viewportHeight),
        };
        return (sv, content, root);
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
