using System;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// G3 块布局透明化（design/03 D16 → D77）。规范条文：spec/01 §4.10/§4.11。
/// 块协调器不引入自己的间距/内边距/尺寸约束：条目按最近布局祖先的 Gap 排布，
/// 空块不占间距槽。块仍是元素树节点（绘制/命中/脏区坐标不变）。
/// </summary>
public class BlockLayoutTests
{
    private const double H = 19.6; // NaiveTextMeasurer：14 × 1.4，单行文本高度

    private static TextBlock Text(string s)
    {
        var block = new TextBlock();
        block.AddRun(new TextRun(s));
        return block;
    }

    private static HeadlessRoot Layout(Element root)
    {
        var host = new HeadlessRoot(root) { AvailableSize = new Size(1000, 1000) };
        host.UpdateLayout();
        return host;
    }

    private static ForEachBlock<int> Items(StackPanel parent, params int[] keys)
    {
        var block = new ForEachBlock<int>();
        parent.Children.Add(block);
        block.SetItems(() => keys);
        block.SetTemplate((item, scope) => scope.AddRoot(Text(item.ToString())));
        block.Refresh();
        return block;
    }

    /// <summary>design/09 G3 出口标准：TaskList 条目间距 = 根 Gap。</summary>
    [Fact]
    public void ForEachBlock_ItemSpacing_EqualsRootGap()
    {
        var root = new StackPanel { Gap = 8 };
        var header = Text("H");
        root.Children.Add(header);
        var block = Items(root, 1, 2, 3);
        var footer = Text("F");
        root.Children.Add(footer);

        Layout(root);

        var items = block.Children.ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(H + 8, items[1].FinalRect.Y - items[0].FinalRect.Y, 5);
        Assert.Equal(H + 8, items[2].FinalRect.Y - items[1].FinalRect.Y, 5);

        // 块与兄弟之间也只有一个根 Gap（块自身高度 = 条目并集，不含额外间距）。
        Assert.Equal(H + 8, block.FinalRect.Y - header.FinalRect.Y, 5);
        Assert.Equal(8, footer.FinalRect.Y - (block.FinalRect.Y + block.FinalRect.Height), 5);
        Assert.Equal(H * 3 + 8 * 2, block.DesiredSize.Height, 5);
        Assert.Equal(H * 5 + 8 * 4, root.DesiredSize.Height, 5);
    }

    /// <summary>design/09 G3 出口标准：@if 多根元素按父容器布局。</summary>
    [Fact]
    public void ConditionalBlock_MultipleRoots_UseParentGap()
    {
        var root = new StackPanel { Gap = 6 };
        var block = new ConditionalBlock();
        root.Children.Add(block);
        block.SetCondition(() => true);
        block.SetContent(scope =>
        {
            scope.AddRoot(Text("A"));
            scope.AddRoot(Text("B"));
        });
        block.Refresh();
        var tail = Text("T");
        root.Children.Add(tail);

        Layout(root);

        var roots = block.Children.ToList();
        Assert.Equal(2, roots.Count);
        Assert.Equal(H + 6, roots[1].FinalRect.Y - roots[0].FinalRect.Y, 5);
        Assert.Equal(H * 2 + 6, block.DesiredSize.Height, 5);
        Assert.Equal(6, tail.FinalRect.Y - (block.FinalRect.Y + block.FinalRect.Height), 5);
    }

    [Fact]
    public void NestedBlocks_UseNearestLayoutAncestorGap()
    {
        var root = new StackPanel { Gap = 8 };
        var outer = new ForEachBlock<int>();
        root.Children.Add(outer);
        outer.SetItems(() => new[] { 1 });
        outer.SetTemplate((item, scope) =>
        {
            var inner = new ForEachBlock<int>();
            scope.AddRoot(inner);
            inner.SetItems(() => new[] { 10, 20 });
            inner.SetTemplate((n, innerScope) => innerScope.AddRoot(Text(n.ToString())));
            inner.Refresh();
            scope.AddRoot(Text("Z"));
        });
        outer.Refresh();

        Layout(root);

        var inner = (ForEachBlock<int>)outer.Children[0];
        var innerItems = inner.Children.ToList();
        var z = outer.Children[1];
        Assert.Equal(H + 8, innerItems[1].FinalRect.Y - innerItems[0].FinalRect.Y, 5);
        // Z 相对外层块：内层块高度（2H + 8）+ 一个 Gap。
        Assert.Equal(H * 2 + 8 + 8, z.FinalRect.Y, 5);
        Assert.Equal(H * 3 + 8 * 2, outer.DesiredSize.Height, 5);
    }

    [Fact]
    public void EmptyBlock_ConsumesNoGapSlot()
    {
        var root = new StackPanel { Gap = 8 };
        var a = Text("A");
        var empty = new ConditionalBlock();
        var b = Text("B");
        root.Children.Add(a);
        root.Children.Add(empty);
        root.Children.Add(b);
        empty.SetCondition(() => false);
        empty.Refresh();

        Layout(root);

        Assert.Empty(empty.Children);
        Assert.Equal(H + 8, b.FinalRect.Y - a.FinalRect.Y, 5);
        Assert.Equal(H * 2 + 8, root.DesiredSize.Height, 5);
    }

    [Fact]
    public void EmptyForEachBlock_ConsumesNoGapSlot()
    {
        var root = new StackPanel { Gap = 8 };
        var a = Text("A");
        root.Children.Add(a);
        var block = Items(root);
        var b = Text("B");
        root.Children.Add(b);

        Layout(root);

        Assert.Empty(block.Children);
        Assert.Equal(H + 8, b.FinalRect.Y - a.FinalRect.Y, 5);
    }

    /// <summary>块嵌套块且内层为空：外层整体不占间距槽。</summary>
    [Fact]
    public void BlockContainingOnlyEmptyBlock_ConsumesNoGapSlot()
    {
        var root = new StackPanel { Gap = 8 };
        var a = Text("A");
        var outer = new ConditionalBlock();
        var b = Text("B");
        root.Children.Add(a);
        root.Children.Add(outer);
        root.Children.Add(b);
        outer.SetCondition(() => true);
        outer.SetContent(scope =>
        {
            var inner = new ConditionalBlock();
            scope.AddRoot(inner);
            inner.SetCondition(() => false);
            inner.Refresh();
        });
        outer.Refresh();

        Layout(root);

        Assert.Equal(H + 8, b.FinalRect.Y - a.FinalRect.Y, 5);
    }

    /// <summary>块自己的 Padding/Gap 不是模板表面（[UiIgnore]），也不得引入额外间距/尺寸约束。</summary>
    [Fact]
    public void BlockOwnPaddingAndGap_DoNotAffectLayout()
    {
        var root = new StackPanel { Gap = 8 };
        var block = new ForEachBlock<int> { Padding = 20, Gap = 99 };
        root.Children.Add(block);
        block.SetItems(() => new[] { 1, 2 });
        block.SetTemplate((item, scope) => scope.AddRoot(Text(item.ToString())));
        block.Refresh();

        Layout(root);

        var items = block.Children.ToList();
        Assert.Equal(H + 8, items[1].FinalRect.Y - items[0].FinalRect.Y, 5);
        Assert.Equal(0, items[0].FinalRect.X, 5);
        Assert.Equal(0, items[0].FinalRect.Y, 5);
        Assert.Equal(H * 2 + 8, block.DesiredSize.Height, 5);
        Assert.Equal(items.Max(i => i.DesiredSize.Width), block.DesiredSize.Width, 5);
    }

    /// <summary>取的是最近「布局参与」祖先，不是最外层：ListBoxItem 的 Gap 胜出。</summary>
    [Fact]
    public void BlockInsideListBoxItem_UsesItemGapNotRootGap()
    {
        var root = new StackPanel { Gap = 8 };
        var item = new ListBoxItem { Gap = 6 };
        root.Children.Add(item);
        var block = new ForEachBlock<int>();
        item.Children.Add(block);
        block.SetItems(() => new[] { 1, 2 });
        block.SetTemplate((n, scope) => scope.AddRoot(Text(n.ToString())));
        block.Refresh();

        Layout(root);

        var entries = block.Children.ToList();
        Assert.Equal(H + 6, entries[1].FinalRect.Y - entries[0].FinalRect.Y, 5);
    }

    /// <summary>透明只改布局参数，不改坐标链：绘制/命中/脏区仍按父链累加 FinalRect。</summary>
    [Fact]
    public void TransparentBlock_KeepsCoordinateChain_HitTestStillFindsItem()
    {
        var root = new StackPanel { Gap = 8, Padding = 10 };
        var header = Text("H");
        root.Children.Add(header);
        var block = Items(root, 1, 2);

        var host = Layout(root);

        var second = block.Children[1];
        // 条目矩形相对块，块矩形相对根（含根 Padding）。
        Assert.Equal(10, block.FinalRect.X, 5);
        Assert.Equal(10 + H + 8, block.FinalRect.Y, 5);
        var absolute = new Point(
            block.FinalRect.X + second.FinalRect.X + 1,
            block.FinalRect.Y + second.FinalRect.Y + 1);
        var hit = host.HitTest(absolute);
        Assert.NotNull(hit);
        Assert.Same(second, hit!.Parent); // 最深命中是 TextBlock 的 run，坐标链穿过透明块仍然正确
    }

    /// <summary>条件翻转后间距随内容重算，不残留旧槽位。</summary>
    [Fact]
    public void ConditionalToggle_RecomputesSpacing()
    {
        var flag = false;
        var root = new StackPanel { Gap = 8 };
        var a = Text("A");
        var block = new ConditionalBlock();
        var b = Text("B");
        root.Children.Add(a);
        root.Children.Add(block);
        root.Children.Add(b);
        block.SetCondition(() => flag);
        block.SetContent(scope => scope.AddRoot(Text("X")));

        var host = new HeadlessRoot(root) { AvailableSize = new Size(1000, 1000) };
        block.Refresh();
        host.UpdateLayout();
        Assert.Equal(H + 8, b.FinalRect.Y - a.FinalRect.Y, 5);

        flag = true;
        block.Refresh();
        host.UpdateLayout();
        Assert.Equal(H * 3 + 8 * 2, root.DesiredSize.Height, 5);
        Assert.Equal(H + 8, block.FinalRect.Y - a.FinalRect.Y, 5);
        Assert.Equal(8, b.FinalRect.Y - (block.FinalRect.Y + block.FinalRect.Height), 5);

        flag = false;
        block.Refresh();
        host.UpdateLayout();
        Assert.Equal(H + 8, b.FinalRect.Y - a.FinalRect.Y, 5);
    }
}