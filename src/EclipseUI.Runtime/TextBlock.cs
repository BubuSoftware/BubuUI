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

/// <summary>文本换行（design/05 §布局契约；Avalonia 同名枚举对齐）：
/// <see cref="NoWrap"/> 单行（现状）；<see cref="Wrap"/> 贪心断行——空格为换行机会，
/// 超行宽的长词（含 CJK 无空格串）字符级硬切。WrapWithOverflow 暂缓。</summary>
public enum TextWrapping
{
    NoWrap,
    Wrap,
}

/// <summary>Wrap 布局的绘制段：来源 <see cref="TextRun"/>、绘制文本（可为 run 文本的切片——
/// 跨行的 run 按行拆成多段）、字号、行内 X 与段宽。</summary>
public sealed record TextLineSegment(TextRun Run, string Text, double FontSize, double X, double Width);

/// <summary>Wrap 布局的一行：块内纵向偏移 Y、行高与段序列（行尾空白折叠不进段）。</summary>
public sealed record TextLayoutLine(double Y, double Height, IReadOnlyList<TextLineSegment> Segments);

[UiContent(UiContentKind.TextRuns)]
public class TextBlock : Panel, ITextContentHost
{
    public override Rect? GetContentClip() => new Rect(Point.Zero, FinalRect.Size);
    public static readonly StyledProperty<double> FontSizeProperty =
        StyledProperty<double>.Register<TextBlock>("FontSize", 14d,
            new PropertyOptions { Inherits = true, Invalidate = InvalidateKind.Measure });

    /// <summary>换行模式（默认 NoWrap = 现状单行；Wrap 见 <see cref="TextWrapping"/>）。</summary>
    public static readonly StyledProperty<TextWrapping> TextWrappingProperty =
        StyledProperty<TextWrapping>.Register<TextBlock>("TextWrapping", TextWrapping.NoWrap,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    private readonly List<TextRun> _runs = new();
    private readonly List<TextLayoutLine> _textLines = new();

    public IReadOnlyList<TextRun> Runs => _runs;

    /// <summary>
    /// Wrap 布局产物（NoWrap 时为空）——绘制侧按段定位（run 跨行时 FinalRect 只落在首段，
    /// 完整行布局以这里为准）。仅测量期重算。
    /// </summary>
    public IReadOnlyList<TextLayoutLine> TextLines => _textLines;

    public void AddRun(TextRun run)
    {
        Children.Add(run);
        _runs.Add(run);
        // run 的文字画在 TextBlock 的 retained 段里（段键是 block.RenderVersion）：
        // run 属性变化必须连带失效 block 渲染，否则复用段保留旧文字。
        run.PropertyChanged += OnRunPropertyChanged;
    }

    private void OnRunPropertyChanged(Element sender, StyledProperty property) => InvalidateRender();

    protected internal override void OnChildrenChanged()
    {
        // Rebuilding a generated TextBlock disposes its old child runs. Keep
        // the text layout cache in step with the actual content tree.
        for (var i = _runs.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(_runs[i].Parent, this))
            {
                _runs[i].PropertyChanged -= OnRunPropertyChanged;
                _runs.RemoveAt(i);
            }
        _textLines.Clear();
        base.OnChildrenChanged();
    }

    public TextRun AddRun(string text)
    {
        var run = new TextRun(text);
        AddRun(run);
        return run;
    }

    protected internal override Size MeasureCore(Size available)
    {
        // 无穷宽约束下换行无意义（ Wrap 退化为单行自然宽）——走 NoWrap 通道。
        if (TextWrapping == TextWrapping.Wrap && !double.IsPositiveInfinity(available.Width))
            return MeasureWrapped(available.Width);

        _textLines.Clear();
        double width = 0, height = 0;
        foreach (var run in _runs)
        {
            // 单行 run 保留自然宽度，供后续 run 的绘制位置使用；TextBlock 自身对外受约束。
            run.Measure(Size.Infinity);
            width += run.DesiredSize.Width;
            height = Math.Max(height, run.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected internal override void ArrangeCore(Rect final)
    {
        if (_textLines.Count > 0)
        {
            // Wrap：run 排到首段矩形（跨行 run 的完整矩形无意义——绘制走 TextLines 段）。
            foreach (var run in _runs)
            {
                var placed = false;
                foreach (var line in _textLines)
                {
                    foreach (var segment in line.Segments)
                    {
                        if (!ReferenceEquals(segment.Run, run)) continue;
                        run.Arrange(new Rect(segment.X, line.Y, segment.Width, line.Height));
                        placed = true;
                        break;
                    }
                    if (placed) break;
                }
                if (!placed) run.Arrange(new Rect(0, 0, 0, 0));
            }
            return;
        }

        double x = 0;
        foreach (var run in _runs)
        {
            run.Arrange(new Rect(x, 0, run.DesiredSize.Width, run.DesiredSize.Height));
            x += run.DesiredSize.Width;
        }
    }

    // ---- Wrap 贪心断行（design/05 §布局契约）----

    /// <summary>
    /// 贪心断行：空格是换行机会（行首空白折叠、行尾空白不进段）；词放不下换行，
    /// 单词自身超行宽（含 CJK 无空格长串）字符级硬切。段宽度按子串测量——naive 测量器
    /// 按字符累加、与整串严格一致；HarfBuzz 接入后 kerning 差异在容差内（M1 断行管线接管）。
    /// </summary>
    private Size MeasureWrapped(double width)
    {
        _textLines.Clear();

        var line = new List<TextLineSegment>();
        var pendingSpaces = new List<TextLineSegment>();
        double lineWidth = 0, lineHeight = 0, totalHeight = 0, maxLineWidth = 0;

        void FlushLine()
        {
            // 行尾悬挂空格不进段（绘制不可见，且避免空行占位）。
            if (line.Count > 0)
            {
                _textLines.Add(new TextLayoutLine(totalHeight, lineHeight, line.ToArray()));
                totalHeight += lineHeight;
                maxLineWidth = Math.Max(maxLineWidth, lineWidth);
            }
            line.Clear();
            pendingSpaces.Clear();
            lineWidth = 0;
            lineHeight = 0;
        }

        foreach (var run in _runs)
        {
            run.Measure(new Size(width, double.PositiveInfinity));
            var text = run.Text;
            if (text.Length == 0) continue;
            var fontSize = run.GetValue(FontSizeProperty);
            var runHeight = TextMeasurer.Current.Measure(text, fontSize).Height;

            var index = 0;
            while (index < text.Length)
            {
                if (char.IsWhiteSpace(text[index]))
                {
                    var start = index;
                    while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                    pendingSpaces.Add(new TextLineSegment(
                        run, text[start..index], fontSize, lineWidth,
                        TextMeasurer.Current.Measure(text[start..index], fontSize).Width));
                    continue;
                }

                var wordStart = index;
                while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
                var word = text[wordStart..index];
                PlaceWord(run, word, fontSize, runHeight, width,
                    line, pendingSpaces, ref lineWidth, ref lineHeight, FlushLine);
            }
        }

        FlushLine();
        return new Size(Math.Min(width, maxLineWidth), totalHeight);
    }

    private void PlaceWord(
        TextRun run, string word, double fontSize, double runHeight, double width,
        List<TextLineSegment> line, List<TextLineSegment> pendingSpaces,
        ref double lineWidth, ref double lineHeight, Action flushLine)
    {
        // 换行判据含悬挂空格宽：词随其前导空格一起量（aa___bb 中 bb 放不下则空格与 bb 同换）。
        double pendingWidth = 0;
        foreach (var space in pendingSpaces) pendingWidth += space.Width;
        var wordWidth = TextMeasurer.Current.Measure(word, fontSize).Width;
        if (lineWidth > 0 && lineWidth + pendingWidth + wordWidth > width)
            flushLine();

        if (lineWidth == 0 && wordWidth > width)
        {
            // 空行也放不下：按完整字素切分，避免拆开 emoji 和组合序列。
            while (word.Length > 0 && wordWidth > width)
            {
                var take = MaxCharsFitting(word, fontSize, width);
                var chunk = word[..take];
                PlaceChunk(run, chunk, fontSize, runHeight, line, pendingSpaces, ref lineWidth, ref lineHeight);
                flushLine();
                word = word[take..];
                wordWidth = TextMeasurer.Current.Measure(word, fontSize).Width;
            }
            if (word.Length > 0)
                PlaceChunk(run, word, fontSize, runHeight, line, pendingSpaces, ref lineWidth, ref lineHeight);
            return;
        }

        PlaceChunk(run, word, fontSize, runHeight, line, pendingSpaces, ref lineWidth, ref lineHeight);
    }

    private void PlaceChunk(
        TextRun run, string chunk, double fontSize, double runHeight,
        List<TextLineSegment> line, List<TextLineSegment> pendingSpaces,
        ref double lineWidth, ref double lineHeight)
    {
        // 词落位前先把行中悬挂空格并入（行首/行尾的由折叠与 Flush 丢弃）。
        foreach (var space in pendingSpaces)
        {
            line.Add(new TextLineSegment(space.Run, space.Text, space.FontSize, lineWidth, space.Width));
            lineWidth += space.Width;
        }
        pendingSpaces.Clear();

        var chunkWidth = TextMeasurer.Current.Measure(chunk, fontSize).Width;
        line.Add(new TextLineSegment(run, chunk, fontSize, lineWidth, chunkWidth));
        lineWidth += chunkWidth;
        lineHeight = Math.Max(lineHeight, runHeight);
    }

    /// <summary>按字素边界测量完整前缀，保留 shaping 的连字与字距。
    /// 即使一个字素超宽，也将它完整放到一行以保证向前推进。</summary>
    private static int MaxCharsFitting(string word, double fontSize, double width)
    {
        var boundaries = System.Globalization.StringInfo.ParseCombiningCharacters(word);
        var count = 0;
        for (var i = 0; i < boundaries.Length; i++)
        {
            var end = i + 1 < boundaries.Length ? boundaries[i + 1] : word.Length;
            if (TextMeasurer.Current.Measure(word[..end], fontSize).Width > width) break;
            count = end;
        }
        return count > 0 ? count : boundaries.Length > 1 ? boundaries[1] : word.Length;
    }
}
