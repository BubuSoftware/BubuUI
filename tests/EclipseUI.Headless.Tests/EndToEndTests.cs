using System.Collections.Generic;
using System.Linq;
using EclipseUI;
using EclipseUI.Elements;
using Gallery;
using Xunit;

namespace EclipseUI.Headless.Tests;

public class CounterEndToEndTests
{
    private static (Counter Component, HeadlessRoot Root, TextBlock Text, CheckBox CheckBox) Mount()
    {
        var component = new Counter();
        var root = new HeadlessRoot(component);
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

        checkbox.Toggle(); // UiBinding 回写 showButton 并失效 → @if 挂载按钮
        root.UpdateLayout();
        var button = root.Content.Descendants<Button>().Single();

        button.Click();
        root.UpdateLayout();
        Assert.Equal("You pressed 1 times", text.FullText());
    }

    [Fact]
    public void Toggling_OffAndOn_PreservesCountState()
    {
        var (_, root, text, checkbox) = Mount();

        checkbox.Toggle();
        root.UpdateLayout();
        root.Content.Descendants<Button>().Single().Click();
        root.UpdateLayout();

        checkbox.Toggle(); // 隐藏按钮
        root.UpdateLayout();
        Assert.Empty(root.Content.Descendants<Button>());

        checkbox.Toggle(); // 重新显示（改 UI 不丢组件状态）
        root.UpdateLayout();
        root.Content.Descendants<Button>().Single().Click();
        root.UpdateLayout();
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
    public void ConditionalBind_RebuildsFromSourceState()
    {
        var component = new Prefs();
        var root = new HeadlessRoot(component);
        root.UpdateLayout();

        Assert.Empty(root.Content.Descendants<CheckBox>());

        component.showExtra = true;
        component.Invalidate();
        root.UpdateLayout();
        var checkbox = root.Content.Descendants<CheckBox>().Single();
        Assert.False(checkbox.Checked);

        checkbox.Toggle(); // 目标变化 → UiBinding 回写源
        Assert.True(component.extra);

        component.showExtra = false; // 卸载分支：绑定随作用域释放
        component.Invalidate();
        root.UpdateLayout();
        Assert.Empty(root.Content.Descendants<CheckBox>());

        component.showExtra = true; // 重建从字段恢复
        component.Invalidate();
        root.UpdateLayout();
        var rebuilt = root.Content.Descendants<CheckBox>().Single();
        Assert.NotSame(checkbox, rebuilt);
        Assert.True(rebuilt.Checked);

        rebuilt.Toggle();
        Assert.False(component.extra);
    }
}

public class TaskListEndToEndTests
{
    [Fact]
    public void Items_AreReconciledByKey_WithTargetedUpdates()
    {
        var component = new TaskList();
        var root = new HeadlessRoot(component);
        root.UpdateLayout();

        component.tasks = new List<TaskItem>
        {
            new TaskItem { Id = 1, Name = "alpha" },
            new TaskItem { Id = 2, Name = "beta" },
        };
        component.Invalidate();
        root.UpdateLayout();

        var items = root.Content.Descendants<ListBoxItem>().ToList();
        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal("alpha", first.Descendants<TextBlock>().Single().FullText());

        var alpha = component.tasks[0];
        var beta = component.tasks[1];
        alpha.Name = "alpha2";
        component.tasks = new List<TaskItem> { beta, alpha };
        component.Invalidate();
        root.UpdateLayout();

        items = root.Content.Descendants<ListBoxItem>().ToList();
        Assert.Equal(2, items.Count);
        Assert.Same(first, items[1]); // Key + item 身份相同：复用实例
        Assert.Equal("alpha2", items[1].Descendants<TextBlock>().Single().FullText()); // 渲染动作重跑
        Assert.Equal("beta", items[0].Descendants<TextBlock>().Single().FullText());
    }

    [Fact]
    public void SameKey_NewItemInstance_RebuildsEntry()
    {
        var component = new TaskList();
        var root = new HeadlessRoot(component);
        root.UpdateLayout();

        component.tasks = new List<TaskItem> { new TaskItem { Id = 1, Name = "alpha" } };
        component.Invalidate();
        root.UpdateLayout();
        var first = root.Content.Descendants<ListBoxItem>().Single();

        component.tasks = new List<TaskItem> { new TaskItem { Id = 1, Name = "beta" } };
        component.Invalidate();
        root.UpdateLayout();

        var rebuilt = root.Content.Descendants<ListBoxItem>().Single();
        Assert.NotSame(first, rebuilt);
        Assert.Equal("beta", rebuilt.Descendants<TextBlock>().Single().FullText());
    }

    [Fact]
    public void EmptyList_RendersHeaderOnly()
    {
        var component = new TaskList();
        var root = new HeadlessRoot(component);
        root.UpdateLayout();

        component.tasks = new List<TaskItem>();
        component.Invalidate();
        root.UpdateLayout();
        Assert.Empty(root.Content.Descendants<ListBoxItem>());
        Assert.Equal("Tasks", root.Content.Descendants<TextBlock>().First().FullText());
    }

    /// <summary>design/09 G3 出口标准：TaskList 条目间距 = 根 Gap（TaskList.ui 的 Gap="4"）。</summary>
    [Fact]
    public void ItemSpacing_EqualsRootGap()
    {
        var component = new TaskList();
        var root = new HeadlessRoot(component);
        component.tasks = new List<TaskItem>
        {
            new TaskItem { Id = 1, Name = "alpha" },
            new TaskItem { Id = 2, Name = "beta" },
            new TaskItem { Id = 3, Name = "gamma" },
        };
        component.Invalidate();
        root.UpdateLayout();

        var panel = Assert.IsType<StackPanel>(root.Content);
        Assert.Equal(4, panel.Gap, 5);
        var items = root.Content.Descendants<ListBoxItem>().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(items[0].DesiredSize.Height + 4, items[1].FinalRect.Y - items[0].FinalRect.Y, 5);
        Assert.Equal(items[1].DesiredSize.Height + 4, items[2].FinalRect.Y - items[1].FinalRect.Y, 5);

        // 整根高等于把条目摊进父容器的结果：标题 + 三条目 + 3 个 Gap。
        var header = panel.Children.OfType<TextBlock>().Single();
        Assert.Equal(header.DesiredSize.Height + items.Sum(i => i.DesiredSize.Height) + 3 * 4,
            panel.DesiredSize.Height, 5);
    }

    /// <summary>空列表的 @foreach 块不占间距槽：根高只剩标题。</summary>
    [Fact]
    public void EmptyList_ConsumesNoGapSlot()
    {
        var component = new TaskList();
        var root = new HeadlessRoot(component);
        component.tasks = new List<TaskItem>();
        component.Invalidate();
        root.UpdateLayout();

        var panel = Assert.IsType<StackPanel>(root.Content);
        var header = panel.Children.OfType<TextBlock>().Single();
        Assert.Equal(header.DesiredSize.Height, panel.DesiredSize.Height, 5);
    }

    [Fact]
    public void ItemBinding_WritesBackToItem_RebuildsFromItem_AndIsReleasedWithTheEntry()
    {
        var component = new TaskList();
        var root = new HeadlessRoot(component);
        root.UpdateLayout();

        var alpha = new TaskItem { Id = 1, Name = "alpha" };
        var beta = new TaskItem { Id = 2, Name = "beta" };
        component.tasks = new List<TaskItem> { alpha, beta };
        component.Invalidate();
        root.UpdateLayout();

        var firstCheck = root.Content.Descendants<CheckBox>().First();
        Assert.False(alpha.Done);

        firstCheck.Toggle(); // 条目内 @bind-Checked：目标变化 → 回写 item 并失效组件
        Assert.True(alpha.Done);
        root.UpdateLayout();
        Assert.True(root.Content.Descendants<CheckBox>().First().Checked); // 重渲染不丢回写

        // 同 Key 换实例：条目重建，新绑定从 item 恢复
        var replaced = new TaskItem { Id = 1, Name = "alpha", Done = true };
        component.tasks = new List<TaskItem> { replaced, beta };
        component.Invalidate();
        root.UpdateLayout();
        var rebuiltCheck = root.Content.Descendants<CheckBox>().First();
        Assert.NotSame(firstCheck, rebuiltCheck);
        Assert.True(rebuiltCheck.Checked);

        // 条目移除：绑定随作用域释放，旧控件不再回写
        component.tasks = new List<TaskItem> { beta };
        component.Invalidate();
        root.UpdateLayout();
        Assert.Single(root.Content.Descendants<CheckBox>());

        firstCheck.Toggle();
        Assert.True(alpha.Done);
    }
}

/// <summary>
/// spec/02 §2.7「第二组件来源」：Gallery 的四个组件都没有同名 <c>.cs</c>。
/// <c>@code</c> 里声明的成员就是生成 partial 类的成员——模板里的 <c>Ref={…}</c> 与外部
/// 消费者（GalleryScene）看到的是同一个字段，不需要额外一层 code-behind 转接。
/// </summary>
public class TemplateOnlyComponentTests
{
    [Fact]
    public void CodeDeclaredField_IsTheRefTarget_AndIsExternallyReachable()
    {
        var component = new Showcase();
        var root = new HeadlessRoot(component); // 构造即 Mount，Ref 已在 Build 里赋值

        Assert.NotNull(component.virtualList);

        // 外部消费者（GalleryScene 同款接线）：字段对外可见、可直接注入工厂，无需 code-behind。
        component.virtualList.ItemFactory = i => new Button { Label = $"item {i}" };
        root.UpdateLayout();

        Assert.Same(component.virtualList, root.Content.Descendants<VirtualizingStackPanel>().Single());
        Assert.NotEmpty(component.virtualList.Children);
    }
}
