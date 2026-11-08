using System;
using System.Collections.Generic;
using System.Globalization;

namespace EclipseUI.Elements;

/// <summary>轨道尺寸单位（解析 <see cref="Grid.RowDefinitions"/> 紧凑串用）。</summary>
internal enum GridUnitType : byte
{
    Auto,
    Pixel,
    Star,
}

/// <summary>单个轨道定义：Auto=按内容取最大期望、Pixel=固定像素、Star=按权重分剩余空间。</summary>
internal readonly record struct TrackDef(double Value, GridUnitType Type);

/// <summary>
/// 行列网格布局（G4 布局切片）。轨道定义用紧凑字符串：
/// <c>RowDefinitions="Auto,*,2*,120"</c>——逗号分隔，空格容忍；未设置（空串/null）时
/// 退化为单星轨道（单一单元格铺满）。子元素落格用附加属性
/// <c>Grid.Row</c>/<c>Grid.Column</c>（默认 0；越界钳到最后一轨，负值钳到 0）；
/// 同格多子元素按声明顺序叠放（后画者在上）。布局透明的空块（D77）不占格。
/// 测量时可用空间无限大的星号轨道退化为按内容（与 StackPanel 语义一致）。
/// </summary>
public class Grid : Panel
{
    public static readonly StyledProperty<string> RowDefinitionsProperty =
        StyledProperty<string>.Register<Grid>("RowDefinitions", string.Empty,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    public static readonly StyledProperty<string> ColumnDefinitionsProperty =
        StyledProperty<string>.Register<Grid>("ColumnDefinitions", string.Empty,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    /// <summary>轨道间距（行间与列间共用一个值；不计入首尾轨道与 Padding 之间）。</summary>
    public static readonly StyledProperty<double> GapProperty =
        StyledProperty<double>.Register<Grid>("Gap", 0d,
            new PropertyOptions { Invalidate = InvalidateKind.Measure });

    // ---- 附加属性（G4 首批切片的静态载体保留；模板 Grid.Row="1" 生成直调）----

    public static readonly AttachedProperty<int> RowProperty = new("Row", typeof(Grid));

    public static readonly AttachedProperty<int> ColumnProperty = new("Column", typeof(Grid));

    public static int GetRow(Element? element) => RowProperty.GetValue(element);

    public static void SetRow(Element element, int value) => RowProperty.SetValue(element, value);

    public static int GetColumn(Element? element) => ColumnProperty.GetValue(element);

    public static void SetColumn(Element element, int value) => ColumnProperty.SetValue(element, value);

    public string RowDefinitions
    {
        get => GetValue(RowDefinitionsProperty);
        set => SetValue(RowDefinitionsProperty, value);
    }

    public string ColumnDefinitions
    {
        get => GetValue(ColumnDefinitionsProperty);
        set => SetValue(ColumnDefinitionsProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    private readonly struct Cell
    {
        public Cell(Element child, int row, int col)
        {
            Child = child;
            Row = row;
            Col = col;
        }

        public Element Child { get; }
        public int Row { get; }
        public int Col { get; }
    }

    protected internal override Size MeasureCore(Size available)
    {
        var padding = Padding;
        var gap = Gap;
        var rows = ParseDefinitions(GetValue(RowDefinitionsProperty));
        var cols = ParseDefinitions(GetValue(ColumnDefinitionsProperty));
        var innerW = Math.Max(0, available.Width - padding * 2);
        var innerH = Math.Max(0, available.Height - padding * 2);

        // 第一遍：全部测量——固定轨道给精确约束，其余给无穷让它量出期望。
        var cells = new List<Cell>(Children.Count);
        foreach (var child in Children)
        {
            var row = ClampIndex(Grid.GetRow(child), rows.Length);
            var col = ClampIndex(Grid.GetColumn(child), cols.Length);
            cells.Add(new Cell(child, row, col));
            child.Measure(new Size(
                cols[col].Type == GridUnitType.Pixel ? cols[col].Value : double.PositiveInfinity,
                rows[row].Type == GridUnitType.Pixel ? rows[row].Value : double.PositiveInfinity));
        }

        // 空透明块（D77）不占格：测量后期望尺寸已确定，此时剔除。
        cells.RemoveAll(c => IsSkipped(c.Child));

        var colSizes = ResolveTrackSizes(cols, TrackMaxDesired(cols, cells, horizontal: true), innerW, gap);
        var rowSizes = ResolveTrackSizes(rows, TrackMaxDesired(rows, cells, horizontal: false), innerH, gap);

        return new Size(
            Sum(colSizes) + GapTotal(cols.Length, gap) + padding * 2,
            Sum(rowSizes) + GapTotal(rows.Length, gap) + padding * 2);
    }

    protected internal override void ArrangeCore(Rect final)
    {
        var padding = Padding;
        var gap = Gap;
        var rows = ParseDefinitions(GetValue(RowDefinitionsProperty));
        var cols = ParseDefinitions(GetValue(ColumnDefinitionsProperty));
        var innerW = Math.Max(0, final.Width - padding * 2);
        var innerH = Math.Max(0, final.Height - padding * 2);

        // 子元素本轮已测量，期望尺寸有效；收集规则与测量期一致（空透明块不占格），
        // 但仍要 arrange 到零矩形：否则 FinalRect 残留上一轮位置，命中测试与脏区会读到过期值。
        var cells = new List<Cell>(Children.Count);
        foreach (var child in Children)
        {
            if (IsSkipped(child))
            {
                child.Arrange(new Rect(padding, padding, 0, 0));
                continue;
            }
            cells.Add(new Cell(
                child,
                ClampIndex(Grid.GetRow(child), rows.Length),
                ClampIndex(Grid.GetColumn(child), cols.Length)));
        }

        var colSizes = ResolveTrackSizes(cols, TrackMaxDesired(cols, cells, horizontal: true), innerW, gap);
        var rowSizes = ResolveTrackSizes(rows, TrackMaxDesired(rows, cells, horizontal: false), innerH, gap);

        foreach (var cell in cells)
        {
            cell.Child.Arrange(new Rect(
                padding + TrackOffset(colSizes, cell.Col, gap),
                padding + TrackOffset(rowSizes, cell.Row, gap),
                colSizes[cell.Col],
                rowSizes[cell.Row]));
        }
    }

    /// <summary>布局透明的空块整体退出网格：不占格（D77 口径，与 StackPanelBase 一致）。</summary>
    private static bool IsSkipped(Element child) =>
        child.IsLayoutTransparent && child.DesiredSize.Width == 0 && child.DesiredSize.Height == 0;

    private static int ClampIndex(int index, int count) => Math.Clamp(index, 0, count - 1);

    private static double[] TrackMaxDesired(TrackDef[] tracks, List<Cell> cells, bool horizontal)
    {
        var desired = new double[tracks.Length];
        foreach (var cell in cells)
        {
            var size = cell.Child.DesiredSize;
            var value = horizontal ? size.Width : size.Height;
            var track = horizontal ? cell.Col : cell.Row;
            if (value > desired[track]) desired[track] = value;
        }
        return desired;
    }

    /// <summary>
    /// 轨道尺寸解析：固定轨取定义值；Auto（以及空间无限时的星号轨）取轨道内最大期望；
    /// 星号轨在剩余空间里按权重分配（剩余 = 可用 - 固定 - 内容 - 间隙，钳非负——超约束时星号收缩到 0）。
    /// </summary>
    private static double[] ResolveTrackSizes(TrackDef[] tracks, double[] maxDesired, double inner, double gap)
    {
        var sizes = new double[tracks.Length];
        double fixedSum = 0, contentSum = 0, weightSum = 0;
        for (int i = 0; i < tracks.Length; i++)
        {
            switch (tracks[i].Type)
            {
                case GridUnitType.Pixel:
                    sizes[i] = tracks[i].Value;
                    fixedSum += sizes[i];
                    break;
                case GridUnitType.Auto:
                    sizes[i] = maxDesired[i];
                    contentSum += sizes[i];
                    break;
                case GridUnitType.Star:
                    if (double.IsPositiveInfinity(inner))
                    {
                        sizes[i] = maxDesired[i];
                        contentSum += sizes[i];
                    }
                    else
                    {
                        weightSum += Math.Max(0, tracks[i].Value);
                    }
                    break;
            }
        }

        if (weightSum > 0)
        {
            var remaining = inner - fixedSum - contentSum - GapTotal(tracks.Length, gap);
            var unit = remaining > 0 ? remaining / weightSum : 0;
            for (int i = 0; i < tracks.Length; i++)
            {
                if (tracks[i].Type == GridUnitType.Star)
                    sizes[i] = Math.Max(0, tracks[i].Value) * unit;
            }
        }

        return sizes;
    }

    private static double TrackOffset(double[] sizes, int track, double gap)
    {
        double offset = 0;
        for (int i = 0; i < track; i++) offset += sizes[i] + gap;
        return offset;
    }

    private static double GapTotal(int trackCount, double gap) => trackCount > 1 ? gap * (trackCount - 1) : 0;

    private static double Sum(double[] sizes)
    {
        double total = 0;
        foreach (var size in sizes) total += size;
        return total;
    }

    internal static TrackDef[] ParseDefinitions(string? definitions)
    {
        if (string.IsNullOrWhiteSpace(definitions))
            return new[] { new TrackDef(1d, GridUnitType.Star) };   // 未设置 → 单星：单一单元格铺满

        var tokens = definitions.Split(',');
        var tracks = new TrackDef[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            tracks[i] = ParseTrack(tokens[i].AsSpan().Trim(), definitions);
        return tracks;
    }

    private static TrackDef ParseTrack(ReadOnlySpan<char> token, string raw)
    {
        if (token.Length == 0)
            throw new FormatException($"Invalid Grid definition '{raw}': empty track (use Auto, N* or a non-negative number)");

        if (token.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return new TrackDef(0, GridUnitType.Auto);

        if (token[token.Length - 1] == '*')
        {
            var weight = token.Length == 1
                ? 1d
                : ParseNonNegative(token[..^1], raw);
            return new TrackDef(weight, GridUnitType.Star);
        }

        return new TrackDef(ParseNonNegative(token, raw), GridUnitType.Pixel);
    }

    private static double ParseNonNegative(ReadOnlySpan<char> text, string raw)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || value < 0)
            throw new FormatException($"Invalid Grid definition '{raw}': unrecognized track '{text.ToString()}' (use Auto, N* or a non-negative number)");
        return value;
    }
}
