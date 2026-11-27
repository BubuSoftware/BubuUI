using BubuUI.Compiler;
using System.Text.RegularExpressions;
using Token = BubuUI.LanguageServer.Analysis.StyleDocument.Token;

namespace BubuUI.LanguageServer.Analysis;

internal sealed partial class StyleLanguageProvider
{
    private sealed record StyleClass(StyleDocument Doc, Token Token, string? Target);
    private IEnumerable<StyleClass> Classes() => _styles.SelectMany(d => d.Nodes.Where(n => n.Block &&
        n.Header.Count >= 2 && !n.Header.Any(t => t.Value is "/template/" or "@theme" or "@states"))
        .SelectMany(n => n.Header.Skip(1).Where(t => n.Header[n.Header.IndexOf(t) - 1].Value == ".")
            .Select(t => new StyleClass(d, t, n.Header[0].Value == "." ? null : n.Header[0].Value))));

    private StyleClass? BindClass(string name, string path, string? target)
    {
        var candidates = Classes().Where(c => c.Token.Value == name && (c.Target is null || c.Target == target)).ToArray();
        var local = candidates.Where(c => c.Doc.Embedded && string.Equals(c.Doc.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        return Unique(local.Length > 0 ? local : candidates.Where(c => !c.Doc.Embedded));
    }

    private (StyleClass Class, TextSpan Span)? ClassAt(int offset)
    {
        if (_document.Path.EndsWith(".bui", StringComparison.OrdinalIgnoreCase) && !_document.Embedded)
        {
            var context = CursorScanner.Scan(_document.Text, offset);
            if (context.Kind != CursorKind.AttributeValue || context.AttributeName != "Class") return null;
            var start = offset; var end = offset;
            while (start > 0 && Word(_document.Text[start - 1])) start--;
            while (end < _document.Text.Length && Word(_document.Text[end])) end++;
            var bound = BindClass(_document.Text[start..end], _document.Path, context.TagName);
            return bound is null ? null : (bound, new(start, end - start));
        }
        var (token, node, _, _) = Context(offset);
        if (token is null || node is null || node.Header.Any(t => t.Value == "/template/")) return null;
        var index = node.Header.IndexOf(token);
        if (index < 1 || node.Header[index - 1].Value != ".") return null;
        var type = node.Header[0].Value == "." ? null : node.Header.FirstOrDefault(t => t.Value != ">")?.Value;
        var match = BindClass(token.Value, _document.Path, type);
        return match is null ? null : (match, token.Span);
    }

    public UiCompletionList? ClassCompletion(int offset)
    {
        if (_document.Embedded || !_document.Path.EndsWith(".bui", StringComparison.OrdinalIgnoreCase)) return null;
        var context = CursorScanner.Scan(_document.Text, offset);
        if (context.Kind != CursorKind.AttributeValue || context.AttributeName != "Class") return null;
        var start = offset; var end = offset;
        while (start > 0 && Word(_document.Text[start - 1])) start--;
        while (end < _document.Text.Length && Word(_document.Text[end])) end++;
        var prefix = _document.Text[start..offset];
        return new(new(start, end - start), Classes().Where(c =>
            (!c.Doc.Embedded || string.Equals(c.Doc.Path, _document.Path, StringComparison.OrdinalIgnoreCase)) &&
            (c.Target is null || c.Target == context.TagName) && c.Token.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(c => new UiCompletionItem(c.Token.Value, UiCompletionKind.Variable, c.Doc.Embedded ? "Component style class" : "Style class"))
            .DistinctBy(c => c.Label).OrderBy(c => c.Label).ToArray());
    }

    public UiDefinition? ClassDefinitionAt(int offset) => ClassAt(offset) is { } hit
        ? new(string.Equals(hit.Class.Doc.Path, _document.Path, StringComparison.OrdinalIgnoreCase) ? null : hit.Class.Doc.Path,
            hit.Class.Target is null ? new(hit.Class.Token.Span.Start - 1, hit.Class.Token.Span.Length + 1) : hit.Class.Token.Span,
            hit.Class.Token.Value, hit.Class.Doc.Text) : null;
    public UiPrepareRename? ClassPrepareRename(int offset) => ClassAt(offset) is { } hit ? new(hit.Span, hit.Class.Token.Value) : null;
    public UiRenameResult? RenameClassAt(int offset, string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_-]*$") || ClassAt(offset) is not { } hit) return null;
        var symbol = hit.Class;
        // Conservatively reject collisions in every affected lookup scope.
        if (Classes().Any(c => c.Token.Value == name && c != symbol)) return null;
        var edits = new List<(string Path, string Text, UiTextEdit Edit)>();
        foreach (var doc in _styles)
            foreach (var node in doc.AllNodes.Where(n => n.Block && !n.Header.Any(t => t.Value is "/template/" or "@theme")))
                for (var i = 1; i < node.Header.Count; i++)
                {
                    var token = node.Header[i];
                    var target = node.Header[0].Value == "." ? null : node.Header.FirstOrDefault(t => t.Value != ">")?.Value;
                    if (node.Header[i - 1].Value == "." && token.Value == symbol.Token.Value && BindClass(token.Value, doc.Path, target) == symbol)
                        edits.Add((doc.Path, doc.Text, new(token.Span, name)));
                }
        foreach (var file in _files.Where(f => f.Key.EndsWith(".bui", StringComparison.OrdinalIgnoreCase)))
        {
            var ast = FrontEnd.Parse(file.Value, file.Key, new List<UiDiagnostic>());
            foreach (var element in Elements(ast.Nodes))
                foreach (var attr in element.Attributes.Where(a => a.Name == "Class" && a.Kind == AttrValueKind.Literal))
                    foreach (Match match in Regex.Matches(attr.Value, @"[A-Za-z_][\w-]*"))
                        if (match.Value == symbol.Token.Value && BindClass(match.Value, file.Key, element.Tag) == symbol)
                        {
                            var start = file.Value.IndexOf(attr.Value, attr.ValueSpan.Start, StringComparison.Ordinal);
                            if (start >= 0) edits.Add((file.Key, file.Value, new(new(start + match.Index, match.Length), name)));
                        }
        }
        return new(edits.GroupBy(e => e.Path).Select(g => new UiRenameFile(g.Key, g.First().Text,
            g.Select(e => e.Edit).DistinctBy(e => e.Span).OrderBy(e => e.Span.Start).ToArray())).ToArray());
    }
}
