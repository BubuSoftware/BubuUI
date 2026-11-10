namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// 偏移 ↔ LSP <c>Position</c> 换算。LSP 的 <c>character</c> 是**UTF-16 码元**、0 基，
/// 与 .NET <see cref="string"/> 索引一致，所以换算是纯粹的「行首表 + 相减」——
/// 不做 UTF-8 字节换算，也不做字形簇修正（VS Code 侧同样按 UTF-16 计数）。
/// </summary>
public sealed class PositionMap
{
    private readonly int[] _lineStarts;

    public PositionMap(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                starts.Add(i + 1);
        _lineStarts = starts.ToArray();
        TextLength = text.Length;
    }

    public int TextLength { get; }

    public (int Line, int Character) ToPosition(int offset)
    {
        offset = Math.Clamp(offset, 0, TextLength);
        int lo = 0, hi = _lineStarts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_lineStarts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return (lo, offset - _lineStarts[lo]);
    }

    /// <summary>越界行列一律钳制到最近合法位置（编辑器在文件末尾/换行后会发出这种位置）。</summary>
    public int ToOffset(int line, int character)
    {
        if (line < 0) return 0;
        if (line >= _lineStarts.Length) return TextLength;
        int start = _lineStarts[line];
        int end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] - 1 : TextLength;
        // 行尾的 '\r' 不单独算字符宽度，但仍允许落在它之后（换行前）。
        return Math.Clamp(start + Math.Max(0, character), start, Math.Max(start, end));
    }
}
