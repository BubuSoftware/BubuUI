using System;
using System.Collections.Generic;

namespace BubuUI.Compiler;

/// <summary>Shared by LSP formatting and VS smart indent. Never rewrites literal/comment contents.</summary>
public static class TemplateIndentation
{
    public sealed class Line
    {
        public int Start { get; set; }
        public int ContentStart { get; set; }
        public int End { get; set; }
        public int Depth { get; set; }
        public bool Preserve { get; set; }
    }

    public static string Whitespace(int depth, int tabSize, bool insertSpaces) =>
        new string(insertSpaces ? ' ' : '\t', Math.Max(0, depth) * (insertSpaces ? Math.Max(1, Math.Min(16, tabSize)) : 1));

    public static IReadOnlyList<Line> Analyze(string text, bool stylesheet = false)
    {
        var events = new List<(int At, int Delta)>();
        var protectedRanges = new List<(int Start, int End)>();
        int i = 0;
        bool inTag = false, closingTag = false, expectBlock = false;
        void Protect(int start, int end) { protectedRanges.Add((start, end)); }
        void SkipBalanced(int open)
        {
            int close = CSharpSnippet.FindMatchingClose(text, open);
            int end = close < 0 ? text.Length : close + 1;
            Protect(open, end);
            i = end;
        }
        void ScanCode(int open, bool wholeFile = false)
        {
            int close = wholeFile ? text.Length : CSharpSnippet.FindMatchingClose(text, open);
            if (close < 0) close = text.Length;
            i = wholeFile ? 0 : open;
            while (i < text.Length && i <= close)
            {
                int after = CSharpSnippet.SkipLiteralOrComment(text, i);
                if (after > i) { Protect(i, after); i = after; continue; }
                char ch = text[i];
                if (ch == '{' || ch == '(' || ch == '[') events.Add((i + 1, 1));
                else if (ch == '}' || ch == ')' || ch == ']') events.Add((i, -1));
                i++;
            }
        }
        if (stylesheet) ScanCode(0, true);
        while (i < text.Length)
        {
            if (Starts(text, i, "<!--") || Starts(text, i, "@*"))
            {
                string endToken = text[i] == '<' ? "-->" : "*@";
                int close = text.IndexOf(endToken, i + 2, StringComparison.Ordinal);
                int end = close < 0 ? text.Length : close + endToken.Length;
                Protect(i, end); i = end; continue;
            }
            char c = text[i];
            if (inTag)
            {
                if (c == '"')
                {
                    int start = i++;
                    while (i < text.Length && text[i] != '"') { if (text[i] == '\\') i++; i++; }
                    if (i < text.Length) i++;
                    Protect(start, i); continue;
                }
                if (c == '{') { SkipBalanced(i); continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '>')
                {
                    if (!closingTag) events.Add((i, -1));
                    inTag = false; i += 2; continue;
                }
                if (c == '>') { inTag = false; i++; continue; }
                i++; continue;
            }
            if (c == '<' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '/'))
            {
                closingTag = text[i + 1] == '/';
                events.Add((closingTag ? i : i + 1, closingTag ? -1 : 1));
                inTag = true;
                i += closingTag ? 2 : 1;
                continue;
            }
            if (c == '@' && i + 1 < text.Length && char.IsLetter(text[i + 1]))
            {
                int start = ++i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                string word = text.Substring(start, i - start);
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if ((word == "code" || word == "style") && i < text.Length && text[i] == '{')
                { ScanCode(i); continue; }
                if (word == "if" || word == "foreach" || word == "for" || word == "while" || word == "switch" || word == "do")
                {
                    if (i < text.Length && text[i] == '(') SkipBalanced(i);
                    expectBlock = true;
                }
                continue;
            }
            if (IsWord(text, i, "else") || IsWord(text, i, "while"))
            {
                bool isElse = text[i] == 'e';
                i += isElse ? 4 : 5;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (isElse && IsWord(text, i, "if"))
                {
                    i += 2;
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                }
                if (i < text.Length && text[i] == '(') SkipBalanced(i);
                expectBlock = isElse;
                continue;
            }
            if (c == '{')
            {
                if (expectBlock) { events.Add((i + 1, 1)); expectBlock = false; i++; }
                else SkipBalanced(i);
                continue;
            }
            if (c == '}') events.Add((i, -1));
            i++;
        }

        var result = new List<Line>();
        int offset = 0, depth = 0, eventIndex = 0, rangeIndex = 0;
        while (offset <= text.Length)
        {
            int end = text.IndexOf('\n', offset);
            if (end < 0) end = text.Length;
            int first = offset;
            while (first < end && (text[first] == ' ' || text[first] == '\t')) first++;
            while (eventIndex < events.Count && events[eventIndex].At <= first)
            { depth = Math.Max(0, depth + events[eventIndex].Delta); eventIndex++; }
            while (rangeIndex < protectedRanges.Count && protectedRanges[rangeIndex].End <= first) rangeIndex++;
            bool preserve = rangeIndex < protectedRanges.Count && first > protectedRanges[rangeIndex].Start && first < protectedRanges[rangeIndex].End;
            // A multiline tag's closing '>' aligns with its opening '<'.
            int lineDepth = first < text.Length && text[first] == '>' ? Math.Max(0, depth - 1) : depth;
            result.Add(new Line { Start = offset, ContentStart = first, End = end, Depth = lineDepth, Preserve = preserve });
            if (end == text.Length) break;
            offset = end + 1;
        }
        return result;
    }

    private static bool Starts(string text, int offset, string value) =>
        offset + value.Length <= text.Length && string.CompareOrdinal(text, offset, value, 0, value.Length) == 0;

    private static bool IsWord(string text, int offset, string word) => Starts(text, offset, word) &&
        (offset == 0 || !char.IsLetterOrDigit(text[offset - 1]) && text[offset - 1] != '_') &&
        (offset + word.Length == text.Length || !char.IsLetterOrDigit(text[offset + word.Length]) && text[offset + word.Length] != '_');
}
