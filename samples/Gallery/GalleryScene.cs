using System.Collections.Generic;
using EclipseUI;
using EclipseUI.Elements;

namespace Gallery;

/// <summary>
/// 画廊主场景（平台无关）：标题 + Counter 展示页 + 任务列表演示。
/// 手工元素树 + <see cref="GalleryRoot"/> 组件接管所有权与失效队列（G2 口径）。
/// </summary>
public static class GalleryScene
{
    /// <summary>构建场景。返回各可交互部件，供宿主/验证工具驱动。</summary>
    public static (StackPanel Root, Counter Counter, TaskList Tasks, Button AddButton) Build()
    {
        var counter = new Counter();
        counter.Mount();
        SetPadding(counter.Root, 16);

        var taskList = new TaskList();
        // 先填数据再 Mount：Build 末尾的 Refresh 会读当前 tasks（spec/03 §2.3），省一次渲染；
        // 接管前入队的失效如今会由 Adopt 迁移（D78），此处写 Invalidate 也正确。
        taskList.tasks = new List<TaskItem>
        {
            new TaskItem { Id = 1, Name = "Design review" },
            new TaskItem { Id = 2, Name = "Damage tracking v0" },
            new TaskItem { Id = 3, Name = "Golden image test" },
        };
        taskList.Mount();
        SetPadding(taskList.Root, 16);

        var addTaskButton = new Button { Label = "+ Add task" };
        var nextId = 4;
        addTaskButton.AddHandler(Button.ClickEvent, () =>
        {
            var tasks = new List<TaskItem>(taskList.tasks)
            {
                new TaskItem { Id = nextId, Name = $"Task {nextId}" },
            };
            nextId++;
            taskList.tasks = tasks;
            taskList.Invalidate();
        });

        var root = new StackPanel { Gap = 14, Padding = 20 };
        root.Children.Add(Text("EclipseUI G2 Gallery", 26));
        root.Children.Add(Text("Click the checkbox to reveal +1 · Tab / Space to drive focus · Esc to close", 12));
        root.Children.Add(counter.Root);
        root.Children.Add(addTaskButton);
        root.Children.Add(taskList.Root);
        return (root, counter, taskList, addTaskButton);
    }

    private static TextBlock Text(string text, double fontSize)
    {
        var block = new TextBlock { FontSize = fontSize };
        block.AddRun(new TextRun(text));
        return block;
    }

    private static void SetPadding(Element element, double padding)
    {
        if (element is Panel panel) panel.Padding = padding;
    }
}

/// <summary>
/// G2 场景是手工元素树；根组件只承担所有权与失效队列，供 WindowHost 每帧处理。
/// </summary>
public sealed class GalleryRoot : Component
{
    private readonly Counter _counter;
    private readonly TaskList _tasks;
    private readonly Element _tree;

    public GalleryRoot(Counter counter, TaskList tasks, Element tree)
    {
        _counter = counter;
        _tasks = tasks;
        _tree = tree;
    }

    protected override Element Build()
    {
        Track(_counter);
        Track(_tasks);
        return _tree;
    }
}
