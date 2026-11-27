using BubuUI.Compiler;
using System.Text.RegularExpressions;
using Token = BubuUI.LanguageServer.Analysis.StyleDocument.Token;
using Node = BubuUI.LanguageServer.Analysis.StyleDocument.Node;

namespace BubuUI.LanguageServer.Analysis;

internal sealed partial class StyleLanguageProvider
{
    internal static IEnumerable<StyleDocument> StyleDocuments(string path, string text, bool includeInline = true)
    {
        if (path.EndsWith(".bss", StringComparison.OrdinalIgnoreCase))
        {
            yield return new(path, text); yield break;
        }
        if (!path.EndsWith(".bui", StringComparison.OrdinalIgnoreCase)) yield break;
        var ast = FrontEnd.Parse(text, path, new List<UiDiagnostic>());
        foreach (var block in ast.Nodes.OfType<UiStyleBlock>())
            yield return new(path, text, block.CodeSpan);
        // The compiler deliberately drops unterminated blocks from its AST. Use its
        // lexer to recognize that one recovery case without matching @style in C# or strings.
        var lexer = new Lexer(text);
        for (var token = lexer.Next(); token.Kind != TokenKind.EndOfFile; token = lexer.Next())
            if (token.Kind == TokenKind.Error && token.Text == "unterminated @style")
            {
                var start = text.IndexOf('{', token.Span.Start) + 1;
                if (start > 0) yield return new(path, text, new(start, text.Length - start));
            }
        if (!includeInline) yield break;
        foreach (var element in Elements(ast.Nodes))
            foreach (var attr in element.Attributes.Where(a => a.Name == "Style" && a.Kind == AttrValueKind.Literal))
            {
                // ValueSpan includes quotes. Literal text has no decoding/projection changes.
                var start = attr.ValueSpan.Start;
                if (start < text.Length && text[start] is '\'' or '"') start++;
                yield return new(path, text, new(start, attr.Value.Length), inline: true);
            }
    }

    private bool Legacy(Node? node)
    {
        if (_document.Embedded) return true;
        // Namespace/import/theme directives select the typed parser for the entire module, including
        // class-only rules. Keep completion and navigation on that same surface.
        if (_document.Nodes.Any(n => n.Header.FirstOrDefault()?.Value is "@theme" or "@using" or "@namespace")) return false;
        if (node is null) return false;
        while (node.Parent is not null) node = node.Parent;
        return node.Header.FirstOrDefault()?.Value is "." or "@states" || node.Header.FirstOrDefault()?.Value.StartsWith('$') == true;
    }

    private StylePropertyDef? LegacyProperty(Node? node) => Legacy(node) && node is { Block: false } &&
        node.Header.Count >= 2 && node.Header[1].Value == ":" ? _catalog.StyleProperties.Find(node.Header[0].Value) : null;

    private static IEnumerable<Node> Variables(StyleDocument doc) => doc.Nodes.Where(n => !n.Block &&
        n.Header.Count >= 3 && n.Header[0].Value.StartsWith('$') && n.Header[1].Value == ":");
    private Node? VariableAt(Token token) => Variables(_document).LastOrDefault(n => n.Header[0] == token ||
        n.End <= token.Span.Start && n.Header[0].Value == token.Value);

    private IEnumerable<(StyleDocument Doc, Token Token)> StateDeclarations() => _styles.SelectMany(d => d.Nodes
        .Where(n => n.Header.FirstOrDefault()?.Value == "@states")
        .SelectMany(n => n.Children.SelectMany(c => c.Header))
        .Where(t => Regex.IsMatch(t.Value, "^[A-Za-z_][A-Za-z0-9_-]*$"))
        .Select(t => (d, t)));
    private static bool StateToken(Node? node, Token token) => node?.Parent?.Header.FirstOrDefault()?.Value == "@states" ||
        node is not null && (node.Block || node.Header.FirstOrDefault()?.Value == "&" || node.Parent is null) &&
        node.Header.IndexOf(token) > 0 && node.Header[node.Header.IndexOf(token) - 1].Value == ":";
    private IEnumerable<string> StateNames() => States.Concat(StateDeclarations().Select(d => d.Token.Value)).Distinct();

    private UiCompletionList? LegacyComplete(int offset, int start, int end, Node? node)
    {
        var text = _document.Text;
        var dollar = start > 0 && text[start - 1] == '$';
        if (dollar) start--;
        var prefix = text[start..offset];
        var items = new List<UiCompletionItem>();
        void Add(string name, UiCompletionKind kind, string? detail = null, string? insertion = null) =>
            items.Add(new(name, kind, detail, InsertText: insertion));
        UiCompletionList Result() => new(new(start, end - start), items
            .Where(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).DistinctBy(i => i.Label).OrderBy(i => i.Label).ToArray());
        if (dollar)
        {
            if (!_document.Inline && Legacy(node))
                foreach (var variable in Variables(_document).Where(n => n.End <= offset).GroupBy(n => n.Header[0].Value).Select(g => g.Last()))
                    Add(variable.Header[0].Value, UiCompletionKind.Variable, text[variable.Header[2].Span.Start..variable.End].TrimEnd(';'));
            return Result();
        }
        if (!Legacy(node) && !(_document.Embedded && node is null)) return null;
        if (start > 0 && text[start - 1] == '@') { start--; prefix = text[start..offset]; }
        if (prefix.StartsWith('@'))
        {
            if (!_document.Inline)
            {
                Add("@states", UiCompletionKind.Directive, "Declare registered custom states", "@states { ");
                Add("@using", UiCompletionKind.Directive, "Import control and template namespaces", "@using ");
                Add("@namespace", UiCompletionKind.Directive, "Set the implicit namespace scope for controls, templates, and resources", "@namespace ");
            }
            return Result();
        }
        if (start > 0 && text[start - 1] == ':' && (node?.Block == true || node?.Parent is null || node?.Header.FirstOrDefault()?.Value == "&"))
        {
            foreach (var name in StateNames()) Add(name, UiCompletionKind.Keyword, "Control state");
            return Result();
        }
        if (node is { Block: false } && node.Header.Count >= 2 && node.Header[1].Value == ":" &&
            start >= node.Header[1].Span.End && node.Header[0].Value != "&")
        {
            if (LegacyProperty(node) is { } property)
            {
                foreach (var value in property.Candidates ?? []) Add(value, UiCompletionKind.EnumMember, property.EnumTypeName);
                if (property.AllowsTheme) Add("theme", UiCompletionKind.Method, "theme(dark, light)", "theme(");
                if (!_document.Inline)
                    foreach (var variable in Variables(_document).Where(n => n.End <= offset)) Add(variable.Header[0].Value, UiCompletionKind.Variable);
            }
            return Result();
        }
        if (node?.Header.FirstOrDefault()?.Value == "@states" || node?.Parent?.Header.FirstOrDefault()?.Value == "@states") return Result();
        var inBody = node?.Block == true && offset > node.Open || node is { Block: false, Parent: not null };
        var selector = node?.Header.Any(t => t.Value is ">" or "&") == true || node?.Block == true && offset <= node.Open;
        if (inBody && !selector)
        {
            foreach (var prop in _catalog.StyleProperties.Properties) Add(prop.Name, UiCompletionKind.StyleProperty, prop.Kind.ToString());
            if (!_document.Inline)
            {
                foreach (var state in StateNames()) Add("&:" + state, UiCompletionKind.Keyword);
                foreach (var type in _catalog.Elements) Add(type.Tag, UiCompletionKind.Class, type.ClrTypeDisplayName);
            }
        }
        else if (start > 0 && text[start - 1] == '.')
        {
            foreach (var doc in _styles.Where(d => !d.Embedded || d.Path == _document.Path))
                foreach (var rule in doc.Nodes.Where(n => n.Header.FirstOrDefault()?.Value == "." && n.Header.Count > 1))
                    Add(rule.Header[1].Value, UiCompletionKind.Variable, "Style class");
        }
        else if (selector)
            foreach (var type in _catalog.Elements) Add(type.Tag, UiCompletionKind.Class, type.ClrTypeDisplayName);
        else if (!_document.Inline)
        {
            Add("@states", UiCompletionKind.Directive);
            Add("@using", UiCompletionKind.Directive);
            Add("@namespace", UiCompletionKind.Directive);
        }
        return Result();
    }

    private UiHoverResult? LegacyHover(int offset)
    {
        var (token, node, _, _) = Context(offset);
        if (token is null) return null;
        if (token.Value == "@states") return new(token.Span, "**@states { name; ... }** — register custom state selectors across this project.");
        if (token.Value.StartsWith('$') && VariableAt(token) is { } variable)
            return new(token.Span, $"**{token.Value}** — stylesheet variable\n\n`{_document.Text[variable.Header[2].Span.Start..variable.End].TrimEnd(';')}`");
        if (node?.Header.FirstOrDefault() == token && LegacyProperty(node) is { } property)
            return new(token.Span, $"**{property.Name}** : `{property.Kind}`\n\nDeclared by `{TypeNames.Short(property.DeclaringTypeName)}`." +
                (property.AllowsTheme ? " Supports `theme(dark, light)`." : "") +
                (property.Candidates is { Count: > 0 } values ? "\n\nValues: " + string.Join(", ", values) : ""));
        if (StateToken(node, token) && StateNames().Contains(token.Value)) return new(token.Span, $"**:{token.Value}** — control state selector");
        if (Legacy(node) && Unique(_catalog.Elements.Where(e => e.Tag == token.Value)) is { } type)
            return new(token.Span, $"**{type.Tag}**\n\n`{TypeNames.Short(type.ClrTypeDisplayName)}`");
        return null;
    }

    private UiDefinition? LegacyDefinition(int offset)
    {
        var (token, node, _, _) = Context(offset);
        if (token is null) return null;
        if (token.Value.StartsWith('$') && VariableAt(token) is { } variable)
            return new(_document.Path, variable.Header[0].Span, token.Value, _document.Text);
        if (StateToken(node, token))
        {
            var declarations = StateDeclarations().Where(d => d.Token.Value == token.Value).ToArray();
            if (declarations.Length == 1) return new(declarations[0].Doc.Path, declarations[0].Token.Span, token.Value, declarations[0].Doc.Text);
        }
        if (node?.Header.FirstOrDefault() == token && LegacyProperty(node) is { } property)
            return ClrDefinition(property.DeclaringTypeName, property.PropertyField.Split('.').Last());
        if (Legacy(node) && Unique(_catalog.Elements.Where(e => e.Tag == token.Value)) is { } type) return ClrDefinition(type.ClrTypeDisplayName);
        return null;
    }

    private UiPrepareRename? LegacyPrepareRename(int offset)
    {
        var (token, node, _, _) = Context(offset);
        if (token is null) return null;
        if (token.Value.StartsWith('$') && VariableAt(token) is not null) return new(new(token.Span.Start + 1, token.Span.Length - 1), token.Value[1..]);
        if (StateToken(node, token) && !States.Contains(token.Value) && StateDeclarations().Any(d => d.Token.Value == token.Value)) return new(token.Span, token.Value);
        return null;
    }

    private UiRenameResult? LegacyRename(int offset, string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_-]*$") || LegacyPrepareRename(offset) is null) return null;
        var (token, _, _, _) = Context(offset);
        if (token!.Value.StartsWith('$'))
        {
            var variable = VariableAt(token)!;
            if (Variables(_document).Any(n => n != variable && n.Header[0].Value == "$" + name)) return null;
            var edits = _document.Tokens.Where(t => t.Value == token.Value && VariableAt(t) == variable)
                .Select(t => new UiTextEdit(new(t.Span.Start + 1, t.Span.Length - 1), name)).ToArray();
            return new([new(_document.Path, _document.Text, edits)]);
        }
        if (States.Contains(name) || StateDeclarations().Any(d => d.Token.Value == name && name != token.Value)) return null;
        var files = _styles.Select(d => new UiRenameFile(d.Path, d.Text, d.AllNodes
            .SelectMany(n => n.Header.Where(t => !t.Quoted && t.Value == token.Value && StateToken(n, t)).Select(t => new UiTextEdit(t.Span, name))).ToArray()))
            .Where(f => f.Edits.Count > 0).GroupBy(f => f.Path).Select(g => new UiRenameFile(g.Key, g.First().Text, g.SelectMany(f => f.Edits).DistinctBy(e => e.Span).ToArray())).ToArray();
        return new(files);
    }
}
