namespace Gallery;

/// <summary>
/// 待办项模型（纯数据类型，不是组件）。<c>TaskList.ui</c> 的 <c>@code</c> 用它声明列表——
/// 组件本身没有 .cs（spec/02 §2.7），模型留在 .cs 是因为它是被多处消费的普通类型。
/// </summary>
public sealed class TaskItem
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public bool Done { get; set; }
}
