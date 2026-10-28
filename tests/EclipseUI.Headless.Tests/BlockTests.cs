using System;
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

    [Fact]
    public void ConditionalBlock_TogglesContent()
    {
        var flag = new Signal<bool>(false);
        var block = new ConditionalBlock();
        block.SetCondition(() => flag.Value, flag);
        block.SetContent(_ => new Element[] { new Button { Label = "X" } });

        Assert.Empty(block.Children);
        flag.Value = true;
        Assert.Single(block.Children);
        flag.Value = false;
        Assert.Empty(block.Children);
    }

    [Fact]
    public void ConditionalBlock_BuildsContent_WhenConditionInitiallyTrue()
    {
        // 修复回归：SetCondition 时条件已为真、SetContent 晚到，内容必须挂载
        var flag = new Signal<bool>(true);
        var block = new ConditionalBlock();
        block.SetCondition(() => flag.Value, flag);
        Assert.Empty(block.Children); // 尚无内容构建器

        block.SetContent(_ => new Element[] { new Button() });
        Assert.Single(block.Children); // 修复前此处为空
        flag.Value = false;
        Assert.Empty(block.Children);
        flag.Value = true;
        Assert.Single(block.Children);
    }

    [Fact]
    public void ConditionalBlock_DisposesContentSubscriptions()
    {
        var flag = new Signal<bool>(false);
        var count = new Signal<int>(0);
        var block = new ConditionalBlock();
        block.SetCondition(() => flag.Value, flag);
        int updates = 0;
        block.SetContent(subs =>
        {
            var run = new TextRun("");
            void Update()
            {
                updates++;
                run.Text = count.Value.ToString();
            }
            subs.Add(count.Subscribe(Update));
            Update();
            return new Element[] { run };
        });

        flag.Value = true;
        Assert.Equal(1, updates);
        Assert.Equal("0", block.Children.OfType<TextRun>().Single().Text);

        count.Value = 5;
        Assert.Equal(2, updates);
        Assert.Equal("5", block.Children.OfType<TextRun>().Single().Text); // 订阅生效

        flag.Value = false;
        count.Value = 7; // 内容已卸载：若订阅泄漏，此处会多触发一次
        flag.Value = true;
        Assert.Equal(3, updates);                                          // 泄漏时为 4
        Assert.Equal("7", block.Children.OfType<TextRun>().Single().Text); // 重建读取当前值
    }

    [Fact]
    public void ForEachBlock_CreatesScopesAndReusesByKey()
    {
        var items = new Signal<System.Collections.Generic.List<Item>>(new System.Collections.Generic.List<Item>());
        var block = new ForEachBlock<Item>();
        block.SetItems(() => items.Value, items);
        block.SetKeySelector(i => i.Id);
        block.SetTemplate(_ => new BlockScope(new Element[] { new ListBoxItem() }));

        items.Value = new System.Collections.Generic.List<Item> { new(1, "a"), new(2, "b") };
        Assert.Equal(2, block.Children.Count);
        var first = block.Children[0];

        items.Value = new System.Collections.Generic.List<Item> { new(2, "b"), new(1, "a2") };
        Assert.Equal(2, block.Children.Count);
        Assert.Same(first, block.Children[1]); // Key=1 复用同一实例
    }

    [Fact]
    public void ForEachBlock_RerunsUpdaters_WithNewItem()
    {
        var items = new Signal<System.Collections.Generic.List<Item>>(new System.Collections.Generic.List<Item>());
        var block = new ForEachBlock<Item>();
        block.SetItems(() => items.Value, items);
        block.SetKeySelector(i => i.Id);

        TextBlock? captured = null;
        block.SetTemplate(item =>
        {
            var tb = new TextBlock();
            var run = new TextRun(item.Name);
            tb.AddRun(run);
            captured = tb;
            return new BlockScope(new Element[] { tb },
                new Action<object>[] { o => run.Text = ((Item)o!).Name });
        });

        items.Value = new System.Collections.Generic.List<Item> { new(1, "a") };
        items.Value = new System.Collections.Generic.List<Item> { new(1, "b") };
        Assert.Equal("b", captured!.Runs[0].Text); // 复用条目按新 item 重跑更新
    }
}
