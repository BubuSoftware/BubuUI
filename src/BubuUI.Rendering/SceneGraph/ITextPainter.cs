namespace BubuUI.Rendering;

/// <summary>一行文本的行盒度量（像素）：基线以上/以下高度 + 整行宽度。</summary>
public readonly record struct TextLineMetrics(double Ascent, double Descent, double Width = 0)
{
    public double Height => Ascent + Descent;
}

/// <summary>
/// 文本 → display list 的适配器：布局测量与绘制共用同一 shaping 结果。
/// 契约层只依赖 <see cref="ShapedText"/> 之外的东西，不依赖具体 shaping 库；
/// HarfBuzz 实现见 <c>BubuUI.Text.Shaping.HarfBuzzTextPainter</c>。
/// </summary>
public interface ITextPainter
{
    /// <summary>行盒度量。无字体实现可按字号估算，保证布局仍有确定值。</summary>
    TextLineMetrics Measure(string text, double fontSize);

    /// <summary>绘制一行文本到 <paramref name="topLeft"/>（行盒左上角，不含额外留白）。</summary>
    void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color);
}

/// <summary>
/// 无字体环境的降级实现：只按字号估算度量，不产生绘制指令。
/// 用于没有字体文件/无法 shaping 的机器，保证布局与命中测试仍可用。
/// </summary>
public sealed class MeasuringTextPainter : ITextPainter
{
    public TextLineMetrics Measure(string text, double fontSize)
    {
        double width = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) width += fontSize * 0.3;
            else if (IsWide(ch)) width += fontSize;
            else width += fontSize * 0.6;
        }

        return new TextLineMetrics(fontSize, fontSize * 0.4, width);
    }

    public void Paint(DisplayList list, string text, double fontSize, Point topLeft, Color color)
    {
        // 无字体：不画字形。调用方可据此判断是否需要提示“字体缺失”。
    }

    private static bool IsWide(char c) =>
        (c >= 0x2E80 && c <= 0x9FFF) ||
        (c >= 0xAC00 && c <= 0xD7AF) ||
        (c >= 0xF900 && c <= 0xFAFF) ||
        (c >= 0xFF00 && c <= 0xFF60);
}
