using System;
using System.Collections.Generic;

namespace EclipseUI.Elements;

public readonly record struct TextMetrics(double Width, double Height);

public interface ITextMeasurer
{
    TextMetrics Measure(string text, double fontSize);
}

/// <summary>
/// 文本测量服务。当前是确定性的 naive 实现（支撑无头测试与布局）；
/// M1 后续接入 HarfBuzz shaping（先 shaping 再测量）时替换。
/// </summary>
public static class TextMeasurer
{
    public static ITextMeasurer Current { get; set; } = new NaiveTextMeasurer();
}

public sealed class NaiveTextMeasurer : ITextMeasurer
{
    public TextMetrics Measure(string text, double fontSize)
    {
        double width = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) width += fontSize * 0.3;
            else if (IsWide(ch)) width += fontSize;
            else width += fontSize * 0.6;
        }
        return new TextMetrics(width, fontSize * 1.4);
    }

    private static bool IsWide(char c) =>
        (c >= 0x2E80 && c <= 0x9FFF) ||
        (c >= 0xAC00 && c <= 0xD7AF) ||
        (c >= 0xF900 && c <= 0xFAFF) ||
        (c >= 0xFF00 && c <= 0xFF60);
}

/// <summary>文本段：TextBlock 的子元素，一等参与布局与定向更新。</summary>
[UiIgnore]
public sealed class TextRun : Element
{
    public static readonly StyledProperty<string> TextProperty =
        StyledProperty<string>.Register<TextRun>("Text", "",
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public TextRun() { }

    public TextRun(string text) => Text = text;

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected internal override Size MeasureCore(Size available)
    {
        var metrics = TextMeasurer.Current.Measure(Text, GetValue(TextBlock.FontSizeProperty));
        return new Size(metrics.Width, metrics.Height);
    }

    protected internal override void ArrangeCore(Rect final) { }
}

[UiContent(UiContentKind.TextRuns)]
public sealed class TextBlock : Panel, ITextContentHost
{
    public static readonly StyledProperty<double> FontSizeProperty =
        StyledProperty<double>.Register<TextBlock>("FontSize", 14d,
            new PropertyOptions { Inherits = true, Invalidate = InvalidateKind.Measure });

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    private readonly List<TextRun> _runs = new();

    public IReadOnlyList<TextRun> Runs => _runs;

    public void AddRun(TextRun run)
    {
        Children.Add(run);
        _runs.Add(run);
        // run 的文字画在 TextBlock 的 retained 段里（段键是 block.RenderVersion）：
        // run 属性变化必须连带失效 block 渲染，否则复用段保留旧文字。
        run.PropertyChanged += (_, _) => InvalidateRender();
    }

    public TextRun AddRun(string text)
    {
        var run = new TextRun(text);
        AddRun(run);
        return run;
    }

    protected internal override Size MeasureCore(Size available)
    {
        double width = 0, height = 0;
        foreach (var run in _runs)
        {
            run.Measure(new Size(available.Width, double.PositiveInfinity));
            width += run.DesiredSize.Width;
            height = Math.Max(height, run.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected internal override void ArrangeCore(Rect final)
    {
        double x = 0;
        foreach (var run in _runs)
        {
            run.Arrange(new Rect(x, 0, run.DesiredSize.Width, run.DesiredSize.Height));
            x += run.DesiredSize.Width;
        }
    }
}
