using System;
using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using Xunit;

namespace EclipseUI.Rendering.Tests;

/// <summary>
/// G2 retained 段缓存（D93）：树未变的元素复用自绘命令段，失效元素只重画自身。
/// 金标准是「retained 构建与全量构建渲染结果逐像素一致」——复用只是省构建，
/// 绝不允许改变绘制语义。同时验证 GPU 后端分支上的 retained 合成等价性。
/// </summary>
public class RetainedPaintTests
{
    private static readonly Size Surface = new(360, 240);

    private static StackPanel Tree()
    {
        var root = new StackPanel();
        root.Children.Add(MakeButton("Alpha"));
        root.Children.Add(MakeButton("Beta"));
        root.Children.Add(MakeButton("Gamma"));
        return root;
    }

    private static Button MakeButton(string label)
    {
        var button = new Button { Label = label };
        return button;
    }

    private static void Layout(Element root)
        => new HeadlessRoot(root) { AvailableSize = Surface }.UpdateLayout();

    /// <summary>命令类型序列（忽略 record 值细节：PathGeometry 等引用每次重建）。</summary>
    private static string CommandShape(DisplayList list) =>
        string.Join(",", list.Commands.ConvertAll(c => c.GetType().Name));

    [Fact]
    public void UnchangedTree_SecondBuild_ReusesAllSegments()
    {
        var root = Tree();
        Layout(root);
        var painter = new ElementPainter(textPainter: new FakeTextPainter());

        _ = painter.Build(root, Surface);
        var (reused0, repainted0) = painter.LastBuildStats;
        Assert.Equal(0, reused0);          // 首帧全量
        Assert.Equal(4, repainted0);       // root + 3 buttons

        _ = painter.Build(root, Surface);
        var (reused1, repainted1) = painter.LastBuildStats;
        Assert.Equal(4, reused1);          // 全部复用
        Assert.Equal(0, repainted1);
    }

    [Fact]
    public void RetainedBuild_RendersIdenticalToFullBuild_AfterStateChanges()
    {
        var root = Tree();
        Layout(root);
        var retainedPainter = new ElementPainter(textPainter: new FakeTextPainter());
        var input = new InputRouter(root);

        _ = retainedPainter.Build(root, Surface);

        // 一组失效来源：按下态、焦点迁移、释放。
        input.PointerPress(new Point(60, 40));
        retainedPainter.Build(root, Surface, focused: root.Children[1] as Button);
        input.PointerRelease(new Point(60, 40));
        retainedPainter.Build(root, Surface, focused: root.Children[1] as Button);

        var retained = retainedPainter.Build(root, Surface);
        var reference = new ElementPainter(textPainter: new FakeTextPainter()).Build(root, Surface);

        Assert.Equal(CommandShape(reference), CommandShape(retained));
        Assert.Equal(Render.Frame(retained).ContentHash(), Render.Frame(reference).ContentHash());
    }

    [Fact]
    public void PressedButton_RepaintsOnlyThatSegment()
    {
        var root = Tree();
        Layout(root);
        var painter = new ElementPainter(textPainter: new FakeTextPainter());
        var input = new InputRouter(root);

        _ = painter.Build(root, Surface);
        input.PointerPress(new Point(60, 40)); // Alpha 按下

        _ = painter.Build(root, Surface);
        var (reused, repainted) = painter.LastBuildStats;
        Assert.Equal(3, reused);   // root + Beta + Gamma 复用
        Assert.Equal(1, repainted);
    }

    [Fact]
    public void FocusChange_RepaintsOldAndNewFocusOnly()
    {
        var root = Tree();
        Layout(root);
        var alpha = (Button)root.Children[0];
        var gamma = (Button)root.Children[2];
        var painter = new ElementPainter(textPainter: new FakeTextPainter());

        _ = painter.Build(root, Surface, focused: alpha);
        _ = painter.Build(root, Surface, focused: gamma);

        var (reused, repainted) = painter.LastBuildStats;
        Assert.Equal(2, reused);    // root + Beta
        Assert.Equal(2, repainted); // Alpha（失焦）+ Gamma（得焦）
    }

    [Fact]
    public void ScrolledViewer_RepaintsViewerSubtree_ReusesOutside()
    {
        // Grid 固定行给 viewer 有限视口（StackPanel 纵向给无穷约束 → 视口=extent，滚不动）。
        var root = new Grid { RowDefinitions = "Auto,200" };
        var label = new TextBlock { FontSize = 14 };
        label.AddRun(new TextRun("outside"));
        root.Children.Add(label);
        Grid.SetRow(label, 0);

        var viewer = new ScrollViewer();
        var content = new StackPanel();
        for (int i = 0; i < 40; i++)
        {
            var item = new TextBlock { FontSize = 14 };
            item.AddRun(new TextRun("row " + i));
            content.Children.Add(item);
        }
        viewer.Children.Add(content);
        root.Children.Add(viewer);
        Grid.SetRow(viewer, 1);
        Layout(root);
        Assert.True(viewer.ScrollableHeight > 0, "测试前提：内容超出视口才可滚动");

        var painter = new ElementPainter(textPainter: new FakeTextPainter());
        _ = painter.Build(root, Surface);
        Assert.Equal((0, 85), painter.LastBuildStats); // grid+label+run+viewer+content+40×(block+run)

        viewer.ScrollToEnd(); // offsets 走 StyledProperty → Arrange 失效（RenderVersion 不 bump）
        Layout(root);         // arrange 重跑 → content FinalRect 平移

        _ = painter.Build(root, Surface);
        var (reused, repainted) = painter.LastBuildStats;
        Assert.Equal(3, reused);       // grid + label + run（viewer 的 offset SetValue bump RenderVersion → 保守重画）
        Assert.Equal(82, repainted);   // viewer + content（FinalRect 平移）+ 80 行（绝对位置随父偏移变化）

        // 渲染等价：retained 构建与全量构建逐像素一致。
        var retained = painter.Build(root, Surface);
        var reference = new ElementPainter(textPainter: new FakeTextPainter()).Build(root, Surface);
        Assert.Equal(Render.Frame(retained).ContentHash(), Render.Frame(reference).ContentHash());
    }

    [Fact]
    public void AppendedChild_RepaintsNewChildOnly()
    {
        var root = Tree(); // 3 buttons
        Layout(root);
        var painter = new ElementPainter(textPainter: new FakeTextPainter());
        _ = painter.Build(root, Surface);

        root.Children.Add(MakeButton("Delta")); // 追加在尾部：既有按钮位置不变
        Layout(root);

        _ = painter.Build(root, Surface);
        var (reused, repainted) = painter.LastBuildStats;
        Assert.Equal(3, reused);    // 3 个既有 buttons（root 变高，其段失配重画）
        Assert.Equal(2, repainted); // root + 新 Delta
    }

    [Fact]
    public void ResetSegments_ForcesFullRepaint()
    {
        var root = Tree();
        Layout(root);
        var painter = new ElementPainter(textPainter: new FakeTextPainter());
        _ = painter.Build(root, Surface);
        Assert.Equal((0, 4), painter.LastBuildStats); // 首帧全量

        _ = painter.Build(root, Surface);
        Assert.Equal((4, 0), painter.LastBuildStats); // 复用

        painter.ResetSegments();
        _ = painter.Build(root, Surface);
        Assert.Equal((0, 4), painter.LastBuildStats); // 强制全量
    }

    /// <summary>
    /// GPU 后端分支的 retained 合成验证：脏区外保持上一帧像素、脏区内与 GPU 整帧渲染一致。
    /// （VelloSharp 无 scissor API，光栅化仍整帧；retained 语义由合成层与段复用共同保证。）
    /// </summary>
    [Fact]
    public void RetainedCompositor_GpuBackend_MatchesCpu()
    {
        if (!Render.TryCreateGpuBackend(out var gpu))
            return; // 无 GPU 留痕跳过（CrossBackendGateTests 有完整矩阵）

        using (gpu)
        {
            var root = Tree();
            Layout(root);
            var painter = new ElementPainter(textPainter: new FakeTextPainter());
            var input = new InputRouter(root);

            var compositor = new RetainedFrameCompositor(gpu, (int)Surface.Width, (int)Surface.Height);
            var fullDamage = new FrameDamage();
            fullDamage.AddFull();
            var first = painter.BuildFrame(root, Surface);
            compositor.Composite(first.List, fullDamage);
            var before = compositor.SnapshotPixels();

            // 状态变化：Alpha 按下 → retained 构建 + 元素级脏区。
            input.PointerPress(new Point(60, 40));
            var frame = painter.BuildFrame(root, Surface);
            Assert.False(frame.Damage.IsFull);
            compositor.Composite(frame.List, frame.Damage);

            // 参照：GPU 对新状态整帧渲染。
            var reference = new RenderTarget((int)Surface.Width, (int)Surface.Height);
            gpu.Render(frame.List, reference);

            // retained 合成结果与 GPU 整帧逐像素一致（脏区内同后端确定性、脏区外画面未变）。
            PixelProbe.On(compositor.Frame, "gpu-retained")
                .SameAs(reference, new Rect(0, 0, Surface.Width, Surface.Height))
                .Verify();

            // 脏区外与上一帧逐字节一致。
            PixelProbe.On(compositor.Frame, "gpu-retained")
                .SameAsOutside(before, frame.Damage.Rects.Count > 0 ? frame.Damage.Rects[0] : default)
                .Verify();
        }
    }

    private sealed class FakeTextPainter : ITextPainter
    {
        public TextLineMetrics Measure(string text, double fontSize)
            => new(fontSize, fontSize * 0.4, text.Length * fontSize * 0.5);

        public void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color)
        {
        }
    }
}
