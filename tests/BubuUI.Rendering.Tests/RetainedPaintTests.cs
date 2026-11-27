using System;
using BubuUI;
using BubuUI.Elements;
using BubuUI.Rendering;
using BubuUI.Rendering.Vello;
using Xunit;

namespace BubuUI.Rendering.Tests;

/// <summary>
/// G2 retained 段缓存（D93）：树未变的元素复用自绘命令段，失效元素只重画自身。
/// 金标准是「retained 构建与全量构建渲染结果逐像素一致」——复用只是省构建，
/// 绝不允许改变绘制语义。同时验证 GPU 后端分支上的 retained 合成等价性。
/// </summary>
public class RetainedPaintTests
{
    private static readonly Size Surface = new(360, 240);

    private static Point Center(Element element) => new(element.FinalRect.X + element.FinalRect.Width / 2, element.FinalRect.Y + element.FinalRect.Height / 2);

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
    {
        using var host = new HeadlessRoot(FluentTest.Skin(root)) { AvailableSize = Surface };
        host.UpdateLayout();
    }

    /// <summary>命令类型序列（忽略 record 值细节：PathGeometry 等引用每次重建）。</summary>
    private static string CommandShape(DisplayList list) =>
        string.Join(",", list.Commands.ConvertAll(c => c.GetType().Name));

    [Fact]
    public void UnchangedTree_SecondBuild_ReusesAllSegments()
    {
        var root = Tree();
        Layout(root);
        var painter = new ElementPainter(FluentTheme.Dark, textPainter: new FakeTextPainter());

        _ = painter.Build(root, Surface);
        var (reused0, repainted0) = painter.LastBuildStats;
        Assert.Equal(0, reused0);          // 首帧全量
        Assert.Equal(FluentTest.VisualCount(root), repainted0);       // Includes each button's template visuals.

        _ = painter.Build(root, Surface);
        var (reused1, repainted1) = painter.LastBuildStats;
        Assert.Equal(FluentTest.VisualCount(root), reused1);          // 全部复用
        Assert.Equal(0, repainted1);
    }

    [Fact]
    public void RetainedBuild_RendersIdenticalToFullBuild_AfterStateChanges()
    {
        var root = Tree();
        Layout(root);
        var retainedPainter = new ElementPainter(FluentTheme.Dark, textPainter: new FakeTextPainter());
        var input = new InputRouter(root);

        _ = retainedPainter.Build(root, Surface);

        // 一组失效来源：按下态、焦点迁移、释放。
        input.PointerPress(Center(root.Children[0]));
        (FocusManager.GetFor(root) ?? new FocusManager(root)).Focus(root.Children[1] as Button, FocusOrigin.Keyboard);
        retainedPainter.Build(root, Surface);
        input.PointerRelease(Center(root.Children[0]));
        (FocusManager.GetFor(root) ?? new FocusManager(root)).Focus(root.Children[1] as Button, FocusOrigin.Keyboard);
        retainedPainter.Build(root, Surface);

        var retained = retainedPainter.Build(root, Surface);
        var reference = new ElementPainter(FluentTheme.Dark, textPainter: new FakeTextPainter()).Build(root, Surface);

        Assert.Equal(CommandShape(reference), CommandShape(retained));
        Assert.Equal(Render.Frame(retained).ContentHash(), Render.Frame(reference).ContentHash());
    }

    [Fact]
    public void PressedButton_RepaintsOnlyThatSegment()
    {
        var root = Tree();
        Layout(root);
        var painter = new ElementPainter(FluentTheme.Dark, textPainter: new FakeTextPainter());
        var input = new InputRouter(root);

        _ = painter.Build(root, Surface);
        input.PointerPress(Center(root.Children[0])); // Alpha 按下

        _ = painter.Build(root, Surface);
        var (reused, repainted) = painter.LastBuildStats;
        var untouched = FluentTest.VisualCount(root.Children[1]) + FluentTest.VisualCount(root.Children[2]);
        Assert.Equal(untouched, reused);
        Assert.Equal(FluentTest.VisualCount(root) - untouched, repainted);
    }

    [Fact]
    public void FocusChange_RepaintsOldAndNewFocusOnly()
    {
        var root = Tree();
        Layout(root);
        var alpha = (Button)root.Children[0];
        var gamma = (Button)root.Children[2];
        var painter = new ElementPainter(FluentTheme.Dark, textPainter: new FakeTextPainter());

        (FocusManager.GetFor(root) ?? new FocusManager(root)).Focus(alpha, FocusOrigin.Keyboard);
        _ = painter.Build(root, Surface);
        (FocusManager.GetFor(root) ?? new FocusManager(root)).Focus(gamma, FocusOrigin.Keyboard);
        _ = painter.Build(root, Surface);

        var (reused, repainted) = painter.LastBuildStats;
        var untouched = 1 + FluentTest.VisualCount(root.Children[1]);
        Assert.Equal(untouched, reused);
        Assert.Equal(FluentTest.VisualCount(alpha) + FluentTest.VisualCount(gamma), repainted);
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

        var painter = new ElementPainter(FluentTheme.Dark, textPainter: new FakeTextPainter());
        _ = painter.Build(root, Surface);
        Assert.Equal((0, FluentTest.VisualCount(root)), painter.LastBuildStats); // Includes the viewer template and projected content.

        viewer.ScrollToEnd(); // offsets 走 StyledProperty → Arrange 失效（RenderVersion 不 bump）
        Layout(root);         // arrange 重跑 → content FinalRect 平移

        _ = painter.Build(root, Surface);
        var (reused, repainted) = painter.LastBuildStats;
        Assert.Equal(3, reused);       // grid + label + run（viewer 的 offset SetValue bump RenderVersion → 保守重画）
        Assert.Equal(FluentTest.VisualCount(root) - 3, repainted);   // viewer + content（FinalRect 平移）+ 80 行（绝对位置随父偏移变化）

        // 渲染等价：retained 构建与全量构建逐像素一致。
        var retained = painter.Build(root, Surface);
        var reference = new ElementPainter(FluentTheme.Dark, textPainter: new FakeTextPainter()).Build(root, Surface);
        Assert.Equal(Render.Frame(retained).ContentHash(), Render.Frame(reference).ContentHash());
    }

    [Fact]
    public void AppendedChild_RepaintsNewChildOnly()
    {
        var root = Tree(); // 3 buttons
        Layout(root);
        var painter = new ElementPainter(FluentTheme.Dark, textPainter: new FakeTextPainter());
        _ = painter.Build(root, Surface);

        root.Children.Add(MakeButton("Delta")); // 追加在尾部：既有按钮位置不变
        Layout(root);

        _ = painter.Build(root, Surface);
        var (reused, repainted) = painter.LastBuildStats;
        Assert.Equal(root.Children.Take(3).Sum(FluentTest.VisualCount), reused);
        Assert.Equal(1 + FluentTest.VisualCount(root.Children[3]), repainted);
    }

    [Fact]
    public void ResetSegments_ForcesFullRepaint()
    {
        var root = Tree();
        Layout(root);
        var painter = new ElementPainter(FluentTheme.Dark, textPainter: new FakeTextPainter());
        _ = painter.Build(root, Surface);
        Assert.Equal((0, FluentTest.VisualCount(root)), painter.LastBuildStats); // 首帧全量

        _ = painter.Build(root, Surface);
        Assert.Equal((FluentTest.VisualCount(root), 0), painter.LastBuildStats); // 复用

        painter.ResetSegments();
        _ = painter.Build(root, Surface);
        Assert.Equal((0, FluentTest.VisualCount(root)), painter.LastBuildStats); // 强制全量
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
            var painter = new ElementPainter(FluentTheme.Dark, textPainter: new FakeTextPainter());
            var input = new InputRouter(root);

            var compositor = new RetainedFrameCompositor(gpu, (int)Surface.Width, (int)Surface.Height);
            var fullDamage = new FrameDamage();
            fullDamage.AddFull();
            var first = painter.BuildFrame(root, Surface);
            compositor.Composite(first.List, fullDamage);
            var before = compositor.SnapshotPixels();

            // 状态变化：Alpha 按下 → retained 构建 + 元素级脏区。
            input.PointerPress(Center(root.Children[0]));
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

    // ---- D97 回归：Button/CheckBox 标签必须写进段缓冲 ----
    // PaintButton/PaintCheckBox 的 _text.Paint 曾写主帧列表 _list（D93 段缓存重构漏改）：
    // 首帧文字命令先于背景 AddRange（背景后画盖掉居中文字），且文字不进段、复用帧彻底丢字。
    // 两条路径同错导致「retained == 全量」金标准抓不到——这里用按颜色可识别的探针
    // 绘制器锁两个语义：顺序（文字在控件背景之后）+ 复用帧存在（段完整）。

    [Fact]
    public void ButtonLabel_CommandsInsideSegment_AndReusedAcrossFrames()
    {
        var root = new StackPanel();
        root.Children.Add(new Button { Label = "Hit" });
        Layout(root);
        var painter = new ElementPainter(FluentTheme.Dark, textPainter: new RectTextPainter());

        var first = painter.Build(root, Surface);
        AssertLabelAfterBackground(first.Commands, "button label before/without background");

        var second = painter.Build(root, Surface); // 全复用帧
        Assert.Equal((FluentTest.VisualCount(root), 0), painter.LastBuildStats);
        AssertLabelAfterBackground(second.Commands, "label missing on reused frame");
    }

    [Fact]
    public void CheckBoxLabel_CommandsInsideSegment_AndReusedAcrossFrames()
    {
        var root = new StackPanel();
        root.Children.Add(new CheckBox { Label = "Opt" });
        Layout(root);
        var painter = new ElementPainter(FluentTheme.Dark, textPainter: new RectTextPainter());

        var first = painter.Build(root, Surface);
        AssertLabelAfterBackground(first.Commands, "checkbox label before/without box");

        var second = painter.Build(root, Surface); // 全复用帧
        Assert.Equal((FluentTest.VisualCount(root), 0), painter.LastBuildStats);
        AssertLabelAfterBackground(second.Commands, "label missing on reused frame");
    }

    /// <summary>文字探针命令必须在最后的圆角矩形背景之后出现（每帧至少一条）。</summary>
    private static void AssertLabelAfterBackground(List<DrawCommand> commands, string context)
    {
        var lastRounded = -1;
        for (var i = 0; i < commands.Count; i++)
            if (commands[i] is FillRoundedRect)
                lastRounded = i;

        var textProbe = -1;
        for (var i = 0; i < commands.Count; i++)
            if (commands[i] is FillRect { Brush: SolidColorBrush { Color: var c } }
                && c.R == RectTextPainter.ProbeR && c.G == 0 && c.B == 0)
                textProbe = i;

        Assert.True(lastRounded >= 0, $"{context}: no control background found");
        Assert.True(textProbe > lastRounded,
            $"{context}: label probe at {textProbe}, background at {lastRounded}");
    }

    // ---- D93 回归：run 文字变化必须失效 TextBlock 段 ----
    // PaintTextBlock 把全部 run 的文字画进 block 自身段（段键 block.RenderVersion）；
    // 曾只 bump run 版本 → block 段原样复用，模型更新但像素陈旧（画廊点 +1 数字不变，
    // 悬停 TextBlock 空白区才重画）。「retained == 全量」金标准两边同复用旧段抓不到，
    // 用记录型绘制器锁「run 改字后的构建必须画新字」。

    [Fact]
    public void RunTextChanged_RepaintsBlockSegment_WithNewText()
    {
        var root = new StackPanel();
        var block = new TextBlock { FontSize = 14 };
        var run = block.AddRun("0");
        root.Children.Add(block);
        Layout(root);
        var recorder = new RecordingTextPainter();
        var painter = new ElementPainter(FluentTheme.Dark, textPainter: recorder);

        _ = painter.Build(root, Surface);
        Assert.Contains("0", recorder.Painted);

        recorder.Painted.Clear();
        run.Text = "5"; // 同宽字符：block 矩形不变，排除尺寸失配兜底
        Layout(root);
        _ = painter.Build(root, Surface);
        Assert.Contains("5", recorder.Painted);
    }

    /// <summary>记录每次 Paint 收到的文本，供段复用回归断言。</summary>
    private sealed class RecordingTextPainter : ITextPainter
    {
        public List<string> Painted { get; } = new();

        public TextLineMetrics Measure(string text, double fontSize)
            => new(fontSize, fontSize * 0.4, text.Length * fontSize * 0.5);

        public void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color)
            => Painted.Add(text);
    }

    /// <summary>产出品红小方块的探针文本绘制器：命令可按颜色识别、无字体依赖。</summary>
    private sealed class RectTextPainter : ITextPainter
    {
        public const byte ProbeR = 255;

        public TextLineMetrics Measure(string text, double fontSize)
            => new(fontSize, fontSize * 0.4, text.Length * fontSize * 0.5);

        public void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color)
            => list.Add(new FillRect(new Rect(topLeft.X, topLeft.Y, fontSize, fontSize),
                new Color(ProbeR, 0, 0, 255)));
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
