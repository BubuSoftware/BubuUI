using System.Text.Json.Nodes;

namespace BubuUI.LanguageServer.Server;

/// <summary>Expand relative template indentation for clients that insert completion edits verbatim.</summary>
internal static class CompletionText
{
    public static string IndentTemplate(string document, int start, string insertion, JsonNode? options)
    {
        if (!insertion.Contains('\n')) return insertion;
        start = Math.Clamp(start, 0, document.Length);
        int lineStart = start > 0 ? document.LastIndexOf('\n', start - 1) + 1 : 0;
        int indentEnd = lineStart;
        while (indentEnd < document.Length && document[indentEnd] is ' ' or '\t') indentEnd++;
        string prefix = document[lineStart..indentEnd];
        bool spaces = options?["insertSpaces"]?.GetValue<bool>() ?? !prefix.Contains('\t');
        int size = Math.Clamp(options?["indentSize"]?.GetValue<int>()
            ?? options?["tabSize"]?.GetValue<int>() ?? InferIndentSize(document), 1, 16);
        int tabSize = Math.Clamp(options?["tabSize"]?.GetValue<int>() ?? size, 1, 16);
        string newline = document.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = insertion.ReplaceLineEndings("\n").Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            int tabs = 0;
            while (tabs < lines[i].Length && lines[i][tabs] == '\t') tabs++;
            int columns = tabs * size;
            string relative = spaces ? new string(' ', columns)
                : new string('\t', columns / tabSize) + new string(' ', columns % tabSize);
            lines[i] = prefix + relative + lines[i][tabs..];
        }
        return string.Join(newline, lines);
    }

    private static int InferIndentSize(string document)
    {
        // Clients without per-request options still inherit the surrounding document style.
        int size = 0;
        foreach (var line in document.Split('\n'))
        {
            int count = 0;
            while (count < line.Length && line[count] == ' ') count++;
            if (count == 0 || count == line.TrimEnd('\r').Length) continue;
            int a = size, b = count;
            while (b != 0) { int remainder = a % b; a = b; b = remainder; }
            size = a;
        }
        return size is >= 1 and <= 8 ? size : 4;
    }
}
