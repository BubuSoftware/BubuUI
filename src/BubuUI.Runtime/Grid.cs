using System;
using System.Collections.Generic;
using System.Globalization;

namespace BubuUI.Elements;

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

    public static readonly AttachedProperty<int> RowSpanProperty = new("RowSpan", typeof(Grid), 1);
    public static readonly AttachedProperty<int> ColumnSpanProperty = new("ColumnSpan", typeof(Grid), 1);
    public static int GetRowSpan(Element element) => RowSpanProperty.GetValue(element);
    public static int GetColumnSpan(Element element) => ColumnSpanProperty.GetValue(element);
    public static void SetRowSpan(Element element, int value) => RowSpanProperty.SetValue(element, Math.Max(1, value));
    public static void SetColumnSpan(Element element, int value) => ColumnSpanProperty.SetValue(element, Math.Max(1, value));

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

    // ---- S5 布局热路径缓冲（每次 Measure/Arrange 都跑，分配必须常数级）----
    // 轨道定义解析缓存：属性值字符串实例稳定（存储引用），按 ReferenceEquals 命中；
    // 解析产物 TrackDef[] 在解算中只读（星号解算写独立 double[]），缓存安全。
    private string? _rowsCacheKey;
    private TrackDef[]? _rowsCache;
    private string? _colsCacheKey;
    private TrackDef[]? _colsCache;

    private readonly List<Cell> _measureCells = new();
    private readonly List<Cell> _arrangeCells = new();
    private double[] _colDesired = Array.Empty<double>();
    private double[] _rowDesired = Array.Empty<double>();
    private double[] _colSizes = Array.Empty<double>();
    private double[] _rowSizes = Array.Empty<double>();

    private TrackDef[] ParseRows() => ParseCached(GetValue(RowDefinitionsProperty), ref _rowsCacheKey, ref _rowsCache);

    private TrackDef[] ParseCols() => ParseCached(GetValue(ColumnDefinitionsProperty), ref _colsCacheKey, ref _colsCache);

    private static TrackDef[] ParseCached(string? definitions, ref string? cacheKey, ref TrackDef[]? cache)
    {
        if (cache is not null && ReferenceEquals(cacheKey, definitions)) return cache;
        var parsed = ParseDefinitions(definitions);
        cacheKey = definitions;
        cache = parsed;
        return parsed;
    }

    private static double[] EnsureBuffer(double[] buffer, int length)
    {
        if (buffer.Length >= length) return buffer;
        var grown = new double[Math.Max(length, buffer.Length * 2)];
        return grown;
    }

    protected internal override Size MeasureCore(Size available)
    {
        var padding = Padding;
        var gap = Gap;
        var rows = ParseRows();
        var cols = ParseCols();
        var innerW = Math.Max(0, available.Width - padding * 2);
        var innerH = Math.Max(0, available.Height - padding * 2);

        // 第一遍：全部测量——固定轨道给精确约束，其余给无穷让它量出期望。
        var cells = _measureCells;
        cells.Clear();
        foreach (var child in Children)
        {
            var row = ClampIndex(Grid.GetRow(child), rows.Length);
            var col = ClampIndex(Grid.GetColumn(child), cols.Length);
            cells.Add(new Cell(child, row, col));
            child.Measure(new Size(
                FixedConstraint(cols, col, GetColumnSpan(child), gap),
                FixedConstraint(rows, row, GetRowSpan(child), gap)));
        }

        // 空透明块（D77）不占格：测量后期望尺寸已确定，此时剔除。
        cells.RemoveAll(c => IsSkipped(c.Child));

        _colSizes = ResolveTrackSizes(cols, TrackMaxDesired(cols, cells, horizontal: true, ref _colDesired), innerW, gap, ref _colSizes);
        // 列宽先定，再按单元格宽度重测；Auto 行高必须来自这次真实断行结果。
        MeasureCellsAtColumnWidths(cells, rows);
        _rowSizes = ResolveTrackSizes(rows, TrackMaxDesired(rows, cells, horizontal: false, ref _rowDesired), innerH, gap, ref _rowSizes);
        var colSizes = _colSizes;
        var rowSizes = _rowSizes;

        return new Size(
            Sum(colSizes, cols.Length) + GapTotal(cols.Length, gap) + padding * 2,
            Sum(rowSizes, rows.Length) + GapTotal(rows.Length, gap) + padding * 2);
    }

    protected internal override void ArrangeCore(Rect final)
    {
        var padding = Padding;
        var gap = Gap;
        var rows = ParseRows();
        var cols = ParseCols();
        var innerW = Math.Max(0, final.Width - padding * 2);
        var innerH = Math.Max(0, final.Height - padding * 2);

        // 子元素本轮已测量，期望尺寸有效；收集规则与测量期一致（空透明块不占格），
        // 但仍要 arrange 到零矩形：否则 FinalRect 残留上一轮位置，命中测试与脏区会读到过期值。
        var cells = _arrangeCells;
        cells.Clear();
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

        // Auto 列的自然需求保留测量产物，避免被星号列的受约束期望反向污染。
        _colSizes = ResolveTrackSizes(cols, _colDesired, innerW, gap, ref _colSizes);
        MeasureCellsAtColumnWidths(cells, rows);
        _rowSizes = ResolveTrackSizes(rows, TrackMaxDesired(rows, cells, horizontal: false, ref _rowDesired), innerH, gap, ref _rowSizes);
        var colSizes = _colSizes;
        var rowSizes = _rowSizes;

        foreach (var cell in cells)
        {
            cell.Child.Arrange(new Rect(
                padding + TrackOffset(colSizes, cell.Col, gap),
                padding + TrackOffset(rowSizes, cell.Row, gap),
                SpanSize(colSizes, cell.Col, GetColumnSpan(cell.Child), gap),
                SpanSize(rowSizes, cell.Row, GetRowSpan(cell.Child), gap)));
        }
    }

    private void MeasureCellsAtColumnWidths(List<Cell> cells, TrackDef[] rows)
    {
        foreach (var cell in cells)
            cell.Child.Measure(new Size(SpanSize(_colSizes, cell.Col, GetColumnSpan(cell.Child), Gap),
                FixedConstraint(rows, cell.Row, GetRowSpan(cell.Child), Gap)));
    }
    private static double FixedConstraint(TrackDef[] tracks, int start, int span, double gap)
    {
        span = Math.Clamp(span, 1, tracks.Length - start);
        double size = (span - 1) * gap;
        for (var i = start; i < start + span; i++)
        {
            if (tracks[i].Type != GridUnitType.Pixel) return double.PositiveInfinity;
            size += tracks[i].Value;
        }
        return size;
    }

    /// <summary>布局透明的空块整体退出网格：不占格（D77 口径，与 StackPanelBase 一致）。</summary>
    private static bool IsSkipped(Element child) =>
        !child.IsVisible || child is Popup || child.IsLayoutTransparent && child.DesiredSize.Width == 0 && child.DesiredSize.Height == 0;

    private static int ClampIndex(int index, int count) => Math.Clamp(index, 0, count - 1);

    private double[] TrackMaxDesired(TrackDef[] tracks, List<Cell> cells, bool horizontal, ref double[] buffer)
    {
        buffer = EnsureBuffer(buffer, tracks.Length);
        var desired = buffer;
        for (int i = 0; i < tracks.Length; i++) desired[i] = 0;
        foreach (var cell in cells)
        {
            var size = cell.Child.DesiredSize;
            var value = horizontal ? size.Width : size.Height;
            var track = horizontal ? cell.Col : cell.Row;
            var span = Math.Clamp(horizontal ? GetColumnSpan(cell.Child) : GetRowSpan(cell.Child), 1, tracks.Length - track);
            if (span == 1) { if (value > desired[track]) desired[track] = value; continue; }
            double allocated = (span - 1) * Gap; int flexible = 0;
            for (var i = track; i < track + span; i++)
            {
                if (tracks[i].Type == GridUnitType.Pixel) allocated += tracks[i].Value;
                else { flexible++; allocated += desired[i]; }
            }
            if (flexible == 0) continue;
            var deficit = Math.Max(0, value - allocated) / flexible;
            for (var i = track; i < track + span; i++) if (tracks[i].Type != GridUnitType.Pixel) desired[i] += deficit;
        }
        return desired;
    }

    /// <summary>
    /// 轨道尺寸解析：固定轨取定义值；Auto（以及空间无限时的星号轨）取轨道内最大期望；
    /// 星号轨在剩余空间里按权重分配（剩余 = 可用 - 固定 - 内容 - 间隙，钳非负——超约束时星号收缩到 0）。
    /// </summary>
    private double[] ResolveTrackSizes(TrackDef[] tracks, double[] maxDesired, double inner, double gap, ref double[] buffer)
    {
        buffer = EnsureBuffer(buffer, tracks.Length);
        var sizes = buffer;
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
                    sizes[i] = 0;
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

    private static double SpanSize(double[] sizes, int start, int span, double gap)
    {
        span = Math.Clamp(span, 1, sizes.Length - start);
        double result = (span - 1) * gap;
        for (var i = start; i < start + span; i++) result += sizes[i];
        return result;
    }
    private static double TrackOffset(double[] sizes, int track, double gap)
    {
        double offset = 0;
        for (int i = 0; i < track; i++) offset += sizes[i] + gap;
        return offset;
    }

    private static double GapTotal(int trackCount, double gap) => trackCount > 1 ? gap * (trackCount - 1) : 0;

    private static double Sum(double[] sizes, int count)
    {
        double total = 0;
        for (var i = 0; i < count; i++) total += sizes[i];
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
            || !double.IsFinite(value) || value < 0)
            throw new FormatException($"Invalid Grid definition '{raw}': unrecognized track '{text.ToString()}' (use Auto, N* or a non-negative number)");
        return value;
    }
}
