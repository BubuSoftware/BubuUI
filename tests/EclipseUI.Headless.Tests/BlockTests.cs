using System;
using System.Collections.Generic;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class BlockTests
{
    private sealed class Item
    {
        public Item(int id, string name) { Id = id; Name = name; }
        public int Id { get; }
        public string Name { get; }
    }

    private sealed class DisposeProbe : IDisposable
    {
        public int Count { get; private set; }
        public void Dispose() => Count++;
    }

    [Fact]
    public void ConditionalBlock_SettersOnlyStore_RefreshMountsAndClearsContent()
    {
        bool flag = false;
        var block = new ConditionalBlock();
        block.SetCondition(() => flag);
        block.SetContent(scope => scope.AddRoot(new Button { Label = "X" }));

        Assert.Empty(block.Children); // setter 不构建

        block.Refresh();
        Assert.Empty(block.Children);

        flag = true;
        block.Refresh();
        Assert.Single(block.Children);

        flag = false;
        block.Refresh();
        Assert.Empty(block.Children);
    }

    [Fact]
    public void ConditionalBlock_EmptyContent_IsLegal()
    {
        bool flag = true;
        var block = new ConditionalBlock();
        block.SetCondition(() => flag);
        block.Refresh();
        flag = false;
        block.Refresh();
        Assert.Empty(block.Children);
    }

    [Fact]
    public void ConditionalBlock_RerunsRenderActions_AndDisposesScopeResourcesOnToggle()
    {
        bool flag = false;
        int value = 0;
        int updates = 0;
        var probe = new DisposeProbe();
        var block = new ConditionalBlock();
        block.SetCondition(() => flag);
        block.SetContent(scope =>
        {
            var run = new TextRun("");
            void R()
            {
                updates++;
                run.Text = value.ToString();
            }
            R();
            scope.AddRenderAction(R);
            scope.AddDisposable(probe);
            scope.AddRoot(run);
        });

        block.Refresh(); // 条件为假：不构建内容
        Assert.Equal(0, updates);

        flag = true;
        block.Refresh();
        Assert.Equal(1, updates);
        Assert.Equal("0", ((TextRun)block.Children.Single()).Text);

        value = 5;
        block.Refresh(); // 条件不变：只重跑渲染动作
        Assert.Equal(2, updates);
        Assert.Equal("5", ((TextRun)block.Children.Single()).Text);

        flag = false;
        block.Refresh(); // 条件翻转：释放旧作用域
        Assert.Empty(block.Children);
        Assert.Equal(1, probe.Count);

        value = 7;
        block.Refresh();
        Assert.Equal(2, updates); // 已卸载：渲染动作不再执行

        flag = true;
        block.Refresh(); // 重建读取当前值
        Assert.Equal(3, updates);
        Assert.Equal("7", ((TextRun)block.Children.Single()).Text);
        Assert.Equal(1, probe.Count); // 旧作用域只释放一次
    }

    [Fact]
    public void ConditionalBlock_ContentFailure_DisposesPartialScope()
    {
        var probe = new DisposeProbe();
        var block = new ConditionalBlock();
        block.SetCondition(() => true);
        block.SetContent(scope =>
        {
            scope.AddDisposable(probe);
            throw new InvalidOperationException("template failed");
        });

        Assert.Throws<InvalidOperationException>(() => block.Refresh());
        Assert.Empty(block.Children);
        Assert.Equal(1, probe.Count);
        Assert.Throws<InvalidOperationException>(() => block.Refresh()); // 失败后可重试
        Assert.Equal(2, probe.Count);
    }

    [Fact]
    public void ConditionalBlock_Dispose_IsIdempotent_AndRefreshIsNoOp()
    {
        bool flag = true;
        var probe = new DisposeProbe();
        var block = new ConditionalBlock();
        block.SetCondition(() => flag);
        block.SetContent(scope =>
        {
            scope.AddDisposable(probe);
            scope.AddRoot(new Button());
        });
        block.Refresh();
        Assert.Single(block.Children);

        block.Dispose();
        block.Dispose();
        Assert.Empty(block.Children);
        Assert.Equal(1, probe.Count);

        flag = false;
        block.Refresh();
        Assert.Empty(block.Children);
        Assert.Equal(1, probe.Count);
    }

    [Fact]
    public void ConditionalBlock_NestedBlock_RefreshFollowsOuterRenderAction()
    {
        bool outer = true;
        bool inner = false;
        TextRun? innerRun = null;
        var outerBlock = new ConditionalBlock();
        outerBlock.SetCondition(() => outer);
        outerBlock.SetContent(outerScope =>
        {
            var innerBlock = new ConditionalBlock();
            innerBlock.SetCondition(() => inner);
            innerBlock.SetContent(innerScope =>
            {
                innerRun = new TextRun("inner");
                innerScope.AddRoot(innerRun);
            });
            outerScope.AddDisposable(innerBlock);
            void R() => innerBlock.Refresh();
            R();
            outerScope.AddRenderAction(R);
            outerScope.AddRoot(innerBlock);
        });

        outerBlock.Refresh();
        var innerBlock = Assert.IsType<ConditionalBlock>(outerBlock.Children.Single());
        Assert.Empty(innerBlock.Children); // 内层条件为假

        inner = true;
        outerBlock.Refresh(); // 外层条件不变：R 刷新内层
        Assert.Single(innerBlock.Children);

        outer = false;
        outerBlock.Refresh(); // 外层卸载：内层随作用域释放
        Assert.Empty(outerBlock.Children);
        Assert.Empty(innerBlock.Children);
        Assert.NotNull(innerRun);
    }

    [Fact]
    public void ForEachBlock_ReusesEntries_OnlyWhenKeyAndItemIdentityMatch()
    {
        var items = new List<Item>();
        int builds = 0;
        var block = new ForEachBlock<Item>();
        block.SetItems(() => items);
        block.SetKeySelector(i => i.Id);
        block.SetTemplate((item, scope) =>
        {
            builds++;
            var run = new TextRun("");
            void R() => run.Text = item.Name;
            R();
            scope.AddRenderAction(R);
            scope.AddRoot(run);
        });

        block.Refresh(); // 空集合
        Assert.Empty(block.Children);

        var a1 = new Item(1, "a");
        var b2 = new Item(2, "b");
        items = new List<Item> { a1, b2 };
        block.Refresh();
        Assert.Equal(2, builds);
        var first = block.Children[0];

        items = new List<Item> { b2, a1 }; // 同一引用换序：Key + item 都相同
        block.Refresh();
        Assert.Equal(2, builds);
        Assert.Same(first, block.Children[1]);

        items = new List<Item> { a1, b2, a1 }; // 重复 Key：队列里的条目只复用一次
        block.Refresh();
        Assert.Equal(3, builds);
        Assert.Same(first, block.Children[0]);
        Assert.NotSame(first, block.Children[2]); // 第二次出现同 Key 必须新建
    }

    [Fact]
    public void ForEachBlock_SameKeyDifferentItem_RebuildsAndDisposesOldScope()
    {
        var items = new List<Item> { new(1, "a") };
        var probes = new List<DisposeProbe>();
        var blocks = new ForEachBlock<Item>();
        blocks.SetItems(() => items);
        blocks.SetKeySelector(i => i.Id);
        blocks.SetTemplate((item, scope) =>
        {
            var probe = new DisposeProbe();
            probes.Add(probe);
            scope.AddDisposable(probe);
            var run = new TextRun("");
            void R() => run.Text = item.Name;
            R();
            scope.AddRenderAction(R);
            scope.AddRoot(run);
        });

        blocks.Refresh();
        var first = blocks.Children.Single();
        Assert.Equal("a", ((TextRun)first).Text);

        items = new List<Item> { new(1, "a2") }; // 同 Key 新 item：重建，不能复用旧闭包
        blocks.Refresh();
        Assert.Equal(2, probes.Count);
        Assert.Equal(1, probes[0].Count);
        Assert.NotSame(first, blocks.Children.Single());
        Assert.Equal("a2", ((TextRun)blocks.Children.Single()).Text);
    }

    [Fact]
    public void ForEachBlock_NullCollection_IsEmpty_AndDisposeReleasesEntries()
    {
        List<Item>? items = new() { new(1, "a"), new(2, "b") };
        var probes = new List<DisposeProbe>();
        var block = new ForEachBlock<Item>();
        block.SetItems(() => items);
        block.SetKeySelector(i => i.Id);
        block.SetTemplate((_, scope) =>
        {
            var probe = new DisposeProbe();
            probes.Add(probe);
            scope.AddDisposable(probe);
            scope.AddRoot(new TextRun("x"));
        });

        block.Refresh();
        Assert.Equal(2, block.Children.Count);

        items = null;
        block.Refresh();
        Assert.Empty(block.Children);
        Assert.Equal(1, probes[0].Count);
        Assert.Equal(1, probes[1].Count);

        items = new List<Item> { new(3, "c") };
        block.Refresh();
        Assert.Single(block.Children);

        block.Dispose();
        block.Dispose();
        Assert.Empty(block.Children);
        Assert.Equal(1, probes[2].Count);

        items = new List<Item> { new(4, "d") };
        block.Refresh(); // Dispose 后 no-op
        Assert.Empty(block.Children);
    }

    [Fact]
    public void ForEachBlock_TemplateFailure_DisposesPartialScope()
    {
        var items = new List<Item> { new(1, "a") };
        var probe = new DisposeProbe();
        var block = new ForEachBlock<Item>();
        block.SetItems(() => items);
        block.SetTemplate((_, scope) =>
        {
            scope.AddDisposable(probe);
            throw new InvalidOperationException("item failed");
        });

        Assert.Throws<InvalidOperationException>(() => block.Refresh());
        Assert.Empty(block.Children);
        Assert.Equal(1, probe.Count);
    }

    [Fact]
    public void ForEachBlock_EnumeratesArrayDictionaryValuesAndLinq()
    {
        IEnumerable<int>? source = new[] { 1, 2 };
        var block = new ForEachBlock<int>();
        block.SetItems(() => source);
        block.SetTemplate((item, scope) => scope.AddRoot(new TextRun(item.ToString())));

        block.Refresh();
        Assert.Equal(new[] { "1", "2" }, block.Children.Cast<TextRun>().Select(r => r.Text));

        source = new Dictionary<int, int> { [3] = 3, [4] = 4 }.Values;
        block.Refresh();
        Assert.Equal(new[] { "3", "4" }, block.Children.Cast<TextRun>().Select(r => r.Text));

        source = new[] { 1, 2, 3 }.Where(i => i % 2 == 1); // 延迟枚举的 LINQ 序列
        block.Refresh();
        Assert.Equal(new[] { "1", "3" }, block.Children.Cast<TextRun>().Select(r => r.Text));
    }

    [Fact]
    public void ForEachBlock_ValueTypeItems_UseEqualityComparer()
    {
        var items = new List<int> { 1, 2 };
        int builds = 0;
        var block = new ForEachBlock<int>();
        block.SetItems(() => items);
        block.SetTemplate((item, scope) =>
        {
            builds++;
            scope.AddRoot(new TextRun(item.ToString()));
        });

        block.Refresh();
        Assert.Equal(2, builds);
        var first = block.Children[0];

        items = new List<int> { 2, 1 }; // 值相等即复用
        block.Refresh();
        Assert.Equal(2, builds);
        Assert.Same(first, block.Children[1]);
    }

}
