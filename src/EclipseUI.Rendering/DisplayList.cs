namespace EclipseUI.Rendering;

/// <summary>
/// 一帧的绘制指令序列。G1 冻结的是“语义顺序”，不是二进制格式。
/// </summary>
public sealed class DisplayList
{
    public DisplayList()
    {
    }

    public DisplayList(Size size)
    {
        Size = size;
    }

    /// <summary>目标逻辑尺寸（像素，DPI 缩放已由调用方折算）。</summary>
    public Size Size { get; set; }

    public List<DrawCommand> Commands { get; } = new();

    public DisplayList Add(DrawCommand command)
    {
        Commands.Add(command);
        return this;
    }

    public DisplayList AddRange(IEnumerable<DrawCommand> commands)
    {
        Commands.AddRange(commands);
        return this;
    }

    public void Clear() => Commands.Clear();

    /// <summary>校验栈类指令配对；后端在调试模式下可调用。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        int clip = 0, layer = 0, transform = 0;
        for (int i = 0; i < Commands.Count; i++)
        {
            switch (Commands[i])
            {
                case PushClip:
                case PushClipPath:
                    clip++;
                    break;
                case PopClip:
                    if (--clip < 0) errors.Add($"command {i}: PopClip without matching PushClip");
                    break;
                case PushLayer:
                    layer++;
                    break;
                case PopLayer:
                    if (--layer < 0) errors.Add($"command {i}: PopLayer without matching PushLayer");
                    break;
                case PushTransform:
                    transform++;
                    break;
                case PopTransform:
                    if (--transform < 0) errors.Add($"command {i}: PopTransform without matching PushTransform");
                    break;
            }
        }

        if (clip != 0) errors.Add($"{clip} unclosed clip scope(s): missing PopClip");
        if (layer != 0) errors.Add($"{layer} unclosed layer scope(s): missing PopLayer");
        if (transform != 0) errors.Add($"{transform} unclosed transform scope(s): missing PopTransform");
        return errors;
    }
}
