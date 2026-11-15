using EclipseUI.Compiler;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>Lossless token ranges and a tolerant block tree for editing incomplete stylesheets.</summary>
internal sealed class StyleDocument
{
    internal sealed record Token(string Value, TextSpan Span, bool Trivia = false, bool Quoted = false);
    internal sealed class Node
    {
        public required int Start { get; init; }
        public int End { get; set; }
        public int Open { get; set; } = -1;
        public Node? Parent { get; init; }
        public List<Token> Header { get; } = new();
        public List<Node> Children { get; } = new();
        public bool Block => Open >= 0;
    }
    public string Path { get; }
    public string Text { get; }
    public List<Token> Tokens { get; } = new();
    public List<Node> Nodes { get; } = new();
    public string[] Imports => Nodes.Where(n => n.Header.FirstOrDefault()?.Value == "@using")
        .Select(n => string.Concat(n.Header.Skip(1).Select(t => t.Value))).ToArray();
    public bool Advanced => Tokens.Any(t => !t.Trivia && t.Value is "@theme" or "@using");

    public StyleDocument(string path, string text)
    {
        Path = path; Text = text;
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            var start = i;
            var trivia = false; var quoted = false;
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                trivia = true;
                while (i < text.Length && text[i] is not '\r' and not '\n') i++;
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                trivia = true;
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
            }
            else if (text[i] is '\'' or '"')
            {
                quoted = true; var quote = text[i++];
                while (i < text.Length)
                {
                    var c = text[i++];
                    if (c == '\\' && i < text.Length) i++;
                    else if (c == quote) break;
                }
            }
            else if (text.AsSpan(i).StartsWith("/template/", StringComparison.Ordinal)) i += 10;
            else if (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@' or '$' or '#')
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-')) i++;
            }
            else i++;
            Tokens.Add(new(text[start..i], new(start, i - start), trivia, quoted));
        }
        var active = Tokens.Where(t => !t.Trivia).ToArray();
        var index = 0;
        Read(Nodes, null);
        void Read(List<Node> nodes, Node? parent)
        {
            while (index < active.Length)
            {
                if (active[index].Value == "}") { if (parent is not null) parent.End = active[index].Span.End; index++; return; }
                var node = new Node { Start = active[index].Span.Start, End = text.Length, Parent = parent };
                nodes.Add(node);
                var parens = 0;
                while (index < active.Length)
                {
                    var token = active[index];
                    if (parens == 0 && token.Value is "{" or ";" or "}") break;
                    if (token.Value == "(") parens++;
                    if (token.Value == ")") parens = Math.Max(0, parens - 1);
                    node.Header.Add(token); index++;
                }
                if (index == active.Length) break;
                var delimiter = active[index];
                if (delimiter.Value == "}") { node.End = delimiter.Span.Start; continue; }
                index++;
                if (delimiter.Value == "{") { node.Open = delimiter.Span.Start; Read(node.Children, node); }
                else node.End = delimiter.Span.End;
            }
        }
    }
    public IEnumerable<Node> AllNodes => Descend(Nodes);
    private static IEnumerable<Node> Descend(IEnumerable<Node> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Descend(n.Children)));
    public Node? At(int offset) => AllNodes.Where(n => n.Start <= offset && offset <= n.End).LastOrDefault();
    public Token? TokenAt(int offset) => Tokens.FirstOrDefault(t => t.Span.Start <= offset && offset < t.Span.End)
        ?? Tokens.LastOrDefault(t => t.Span.End == offset);
    public bool InCommentOrString(int offset) => Tokens.Any(t => (t.Trivia || t.Quoted) && offset > t.Span.Start &&
        (offset < t.Span.End || offset == Text.Length && t.Span.End == offset &&
            (t.Value.StartsWith("//", StringComparison.Ordinal) || t.Trivia && !t.Value.EndsWith("*/", StringComparison.Ordinal) ||
             t.Quoted && (t.Value.Length == 1 || t.Value[^1] != t.Value[0]))));
}
