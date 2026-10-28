using System.Collections.Generic;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;
using Samples;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class CounterEndToEndTests
{
    private static (Counter Component, HeadlessRoot Root, TextBlock Text, CheckBox CheckBox) Mount()
    {
        var component = new Counter();
        component.Mount();
        var root = new HeadlessRoot(component.Root);
        root.UpdateLayout();
        return (component, root,
            component.Root.Descendants<TextBlock>().Single(),
            component.Root.Descendants<CheckBox>().Single());
    }

    [Fact]
    public void InitialRender_ShowsZero()
    {
        var (_, _, text, _) = Mount();
        Assert.Equal("You pressed 0 times", text.FullText());
    }

    [Fact]
    public void ToggleCheckBox_ShowsButton_ClickIncrements()
    {
        var (_, root, text, checkbox) = Mount();

        checkbox.Toggle(); // 双向绑定 → showButton → @if 挂载按钮
        root.UpdateLayout();
        var button = root.Content.Descendants<Button>().Single();

        button.Click();
        Assert.Equal("You pressed 1 times", text.FullText());
    }

    [Fact]
    public void Toggling_OffAndOn_PreservesCountState()
    {
        var (_, root, text, checkbox) = Mount();

        checkbox.Toggle();
        root.Content.Descendants<Button>().Single().Click();

        checkbox.Toggle(); // 隐藏按钮
        root.UpdateLayout();
        Assert.Empty(root.Content.Descendants<Button>());

        checkbox.Toggle(); // 重新显示（改 UI 不丢组件状态）
        root.UpdateLayout();
        root.Content.Descendants<Button>().Single().Click();
        Assert.Equal("You pressed 2 times", text.FullText());
    }

    [Fact]
    public void LayoutReflectsConditionalContent()
    {
        var (_, root, _, checkbox) = Mount();
        double heightBefore = root.Content.DesiredSize.Height;
        checkbox.Toggle();
        root.UpdateLayout();
        Assert.True(root.Content.DesiredSize.Height > heightBefore);
    }
}

public class PrefsEndToEndTests
{
    [Fact]
    public void ConditionalBind_DoesNotLeak_AndRestoresState()
    {
        // 修复回归：条件内容里的 :bind 曾被发成 Track（组件级），
        // toggle 一次泄漏一个订阅；现在挂在内容订阅表上，卸载即释放。
        var component = new Prefs();
        component.Mount();
        var root = new HeadlessRoot(component.Root);
        root.UpdateLayout();

        Assert.Equal(0, component.extra.SubscriberCount);

        component.showExtra.Value = true;
        Assert.Equal(1, component.extra.SubscriberCount); // 双向绑定建立

        var checkbox = root.Content.Descendants<CheckBox>().Single();
        checkbox.Toggle();
        Assert.True(component.extra.Value); // 属性变更写回 signal

        component.showExtra.Value = false;
        Assert.Equal(0, component.extra.SubscriberCount); // 卸载释放（泄漏时为 1）

        component.showExtra.Value = true;
        var rebuilt = root.Content.Descendants<CheckBox>().Single();
        Assert.NotSame(checkbox, rebuilt);
        Assert.True(rebuilt.Checked);                     // 重建从 signal 恢复（改 UI 不丢状态）
        Assert.Equal(1, component.extra.SubscriberCount); // 反复开关无累积

        component.showExtra.Value = false;
        Assert.Equal(0, component.extra.SubscriberCount);
    }
}

public class TaskListEndToEndTests
{
    [Fact]
    public void Items_AreReconciledByKey_WithTargetedUpdates()
    {
        var component = new TaskList();
        component.Mount();
        var root = new HeadlessRoot(component.Root);
        root.UpdateLayout();

        component.tasks.Value = new List<TaskItem>
        {
            new TaskItem { Id = 1, Name = "alpha" },
            new TaskItem { Id = 2, Name = "beta" },
        };
        root.UpdateLayout();

        var items = root.Content.Descendants<ListBoxItem>().ToList();
        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal("alpha", first.Descendants<TextBlock>().Single().FullText());

        component.tasks.Value = new List<TaskItem>
        {
            new TaskItem { Id = 2, Name = "beta" },
            new TaskItem { Id = 1, Name = "alpha2" },
        };
        root.UpdateLayout();

        items = root.Content.Descendants<ListBoxItem>().ToList();
        Assert.Equal(2, items.Count);
        Assert.Same(first, items[1]); // Key 对齐复用实例，O(变动数)
        Assert.Equal("alpha2", items[1].Descendants<TextBlock>().Single().FullText()); // 定向更新重跑
        Assert.Equal("beta", items[0].Descendants<TextBlock>().Single().FullText());
    }

    [Fact]
    public void EmptyList_RendersHeaderOnly()
    {
        var component = new TaskList();
        component.Mount();
        var root = new HeadlessRoot(component.Root);
        root.UpdateLayout();

        component.tasks.Value = new List<TaskItem>();
        root.UpdateLayout();
        Assert.Empty(root.Content.Descendants<ListBoxItem>());
        Assert.Equal("Tasks", root.Content.Descendants<TextBlock>().First().FullText());
    }
}
