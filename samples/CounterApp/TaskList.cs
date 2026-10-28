using System.Collections.Generic;
using EclipseUI;

namespace Samples;

public sealed class TaskItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public partial class TaskList
{
    public readonly Signal<List<TaskItem>> tasks = new(new List<TaskItem>());
}
