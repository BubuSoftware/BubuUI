using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI.Compiler;

public sealed record ThemeStyleNode(string Header, string? Value, IReadOnlyList<ThemeStyleNode> Children, TextSpan Span);
public sealed record ThemeStyleDocument(string LegacyText, IReadOnlyList<string> Imports, IReadOnlyList<ThemeStyleNode> Nodes);

/// <summary>Shared structural parser for theme modules. Values are bound through the UI type catalog.</summary>
public static class ControlThemeSyntax
{
    public static IEnumerable<string> Classes(ThemeStyleDocument document) => document.Nodes
        .Where(n => !n.Header.StartsWith("@", StringComparison.Ordinal))
        .SelectMany(n => System.Text.RegularExpressions.Regex.Matches(n.Header, @"\.([A-Za-z_][\w-]*)")
            .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value)).Distinct(StringComparer.Ordinal);
    public static ThemeStyleDocument Parse(string text, List<UiDiagnostic> diagnostics)
    {
        var errors = new List<UiDiagnostic>();
        var parser = new Reader(text, errors);
        var nodes = parser.Read(false);
        var advanced = nodes.Any(n => n.Header.StartsWith("@theme ", StringComparison.Ordinal) || n.Header.StartsWith("@using ", StringComparison.Ordinal));
        if (!advanced) return new(text, Array.Empty<string>(), Array.Empty<ThemeStyleNode>());
        diagnostics.AddRange(errors);
        var imports = new List<string>();
        var rules = new List<ThemeStyleNode>();
        var chars = text.ToCharArray();
        foreach (var node in nodes)
        {
            if (node.Header.StartsWith("@using ", StringComparison.Ordinal)) imports.Add(node.Header.Substring(7).Trim());
            else rules.Add(node);
            for (var i = node.Span.Start; i < node.Span.End; i++) if (chars[i] is not '\r' and not '\n') chars[i] = ' ';
        }
        return new(new string(chars), imports, rules);
    }

    private sealed class Reader(string text, List<UiDiagnostic> diagnostics)
    {
        private int _i;
        public List<ThemeStyleNode> Read(bool nested)
        {
            var result = new List<ThemeStyleNode>();
            while (_i < text.Length)
            {
                Trivia();
                if (_i == text.Length) break;
                if (text[_i] == '}')
                {
                    _i++;
                    if (!nested) Error("Unexpected '}'", _i - 1);
                    return result;
                }
                var start = _i;
                var parens = 0;
                char quote = '\0';
                while (_i < text.Length)
                {
                    var c = text[_i];
                    if (quote != '\0') { if (c == '\\') { _i = Math.Min(_i + 2, text.Length); continue; } if (c == quote) quote = '\0'; }
                    else if (c is '\'' or '"') quote = c;
                    else if (c == '(') parens++;
                    else if (c == ')') parens--;
                    else if (parens == 0 && c is '{' or ';' or '}') break;
                    _i++;
                }
                var header = text.Substring(start, _i - start).Trim();
                if (_i == text.Length || text[_i] == '}') { Error("Expected ';' or a block", start); if (_i < text.Length) _i++; break; }
                var block = text[_i++] == '{';
                if (block) result.Add(new(header, null, Read(true), new(start, _i - start)));
                else
                {
                    var colon = header.IndexOf(':');
                    result.Add(new(colon < 0 ? header : header.Substring(0, colon).Trim(), colon < 0 ? "" : header.Substring(colon + 1).Trim(), Array.Empty<ThemeStyleNode>(), new(start, _i - start)));
                }
            }
            if (nested) Error("Unterminated style block", Math.Max(0, _i - 1));
            return result;
        }
        private void Trivia()
        {
            while (_i < text.Length)
            {
                if (char.IsWhiteSpace(text[_i])) { _i++; continue; }
                if (_i + 1 < text.Length && text[_i] == '/' && text[_i + 1] == '/') { while (_i < text.Length && text[_i] != '\n') _i++; continue; }
                if (_i + 1 < text.Length && text[_i] == '/' && text[_i + 1] == '*')
                {
                    var end = text.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    if (end < 0) { Error("Unterminated comment", _i); _i = text.Length; return; }
                    _i = end + 2; continue;
                }
                break;
            }
        }
        private void Error(string message, int start) => diagnostics.Add(UiDiagnostics.Error("EUI050", message, new(start, Math.Min(1, text.Length - start))));
    }
}
