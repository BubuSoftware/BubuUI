using EclipseUI.Compiler;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>Translates a namespace import into an explicit template directive edit.</summary>
public static class UiImportService
{
    public static UiTextEdit? CreateImportEdit(DocumentAnalysis analysis, string namespaceName)
    {
        namespaceName = namespaceName.Replace("global::", "", StringComparison.Ordinal).Trim();
        if (namespaceName.Length == 0 || analysis.IsNamespaceVisible(namespaceName)) return null;
        var text = analysis.Text;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insertion = text.StartsWith('\uFEFF') ? 1 : 0;
        // Directives may occur anywhere in a template. Insert only in its leading directive
        // group, never at a position inferred from generated C# or inside an @code block.
        var at = insertion;
        while (at < text.Length)
        {
            var end = text.IndexOf('\n', at);
            if (end < 0) end = text.Length;
            var line = text.AsSpan(at, end - at).Trim();
            if (line.Length == 0) { at = end < text.Length ? end + 1 : end; if (at == end) break; continue; }
            if (!line.StartsWith("@using ", StringComparison.Ordinal)
                && !line.StartsWith("@using\t", StringComparison.Ordinal)
                && !line.StartsWith("@namespace ", StringComparison.Ordinal)
                && !line.StartsWith("@namespace\t", StringComparison.Ordinal)) break;
            insertion = end < text.Length ? end + 1 : end;
            if (end == text.Length) break;
            at = end + 1;
        }
        var prefix = insertion > 0 && text[insertion - 1] != '\n' && text[insertion - 1] != '\uFEFF'
            ? newline : "";
        return new UiTextEdit(new TextSpan(insertion, 0), prefix + "@using " + namespaceName + newline);
    }
}
