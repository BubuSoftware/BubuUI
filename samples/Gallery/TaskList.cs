using System.Collections.Generic;

namespace Gallery;

public sealed class TaskItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Done { get; set; }
}

public partial class TaskList
{
    public List<TaskItem> tasks = new();
}
