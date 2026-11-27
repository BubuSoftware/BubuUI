using System.Collections.Generic;

namespace BubuUI.Compiler;

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end) => new TextSpan(start, end - start);

    public static readonly TextSpan Zero = default;
}

/// <summary>偏移 → 行/列 换算（诊断与 #line 映射共用）。</summary>
public sealed class LineMap
{
    private readonly int[] _lineStarts;

    public LineMap(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                starts.Add(i + 1);
        _lineStarts = starts.ToArray();
    }

    /// <summary>1-based 行号。</summary>
    public int LineOf(int offset)
    {
        int lo = 0, hi = _lineStarts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_lineStarts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return lo + 1;
    }

    public int ColumnOf(int offset)
    {
        int line = LineOf(offset);
        return offset - _lineStarts[line - 1] + 1;
    }
}
