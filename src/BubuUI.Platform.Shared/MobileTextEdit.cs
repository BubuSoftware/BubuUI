using System.Globalization;

namespace BubuUI.Platform;

internal static class MobileTextEdit
{
    // Native editors may replace an entire buffer. Keep the TextBox edit (and undo)
    // confined to the graphemes that changed.
    internal static (int Start, int Length, string Replacement) GetReplacement(
        string old, string next, (int Start, int Length)? selection = null)
    {
        // Prefer a replacement at the known selection. Adjacent identical characters and
        // new combining marks can make a diff alone point to a different edit.
        if (selection is { } range)
        {
            var inserted = next.Length - (old.Length - range.Length);
            if (inserted >= 0 &&
                old.AsSpan(0, range.Start).SequenceEqual(next.AsSpan(0, range.Start)) &&
                old.AsSpan(range.Start + range.Length).SequenceEqual(next.AsSpan(range.Start + inserted)))
                return (range.Start, range.Length, next.Substring(range.Start, inserted));
        }

        var oldOffsets = StringInfo.ParseCombiningCharacters(old).Append(old.Length).ToArray();
        var newOffsets = StringInfo.ParseCombiningCharacters(next).Append(next.Length).ToArray();
        int first = 0, oldEnd = oldOffsets.Length - 1, newEnd = newOffsets.Length - 1;

        bool SameGrapheme(int oldIndex, int newIndex) =>
            old.AsSpan(oldOffsets[oldIndex]..oldOffsets[oldIndex + 1])
                .SequenceEqual(next.AsSpan(newOffsets[newIndex]..newOffsets[newIndex + 1]));

        while (first < oldEnd && first < newEnd && SameGrapheme(first, first)) first++;
        while (oldEnd > first && newEnd > first && SameGrapheme(oldEnd - 1, newEnd - 1))
        {
            oldEnd--;
            newEnd--;
        }
        return (oldOffsets[first], oldOffsets[oldEnd] - oldOffsets[first], next[newOffsets[first]..newOffsets[newEnd]]);
    }
}
