using System;
using System.Collections.Generic;
using System.Linq;

namespace BubuUI.Compiler;

public sealed record ThemeStyleNode(string Header, string? Value, IReadOnlyList<ThemeStyleNode> Children, TextSpan Span);
public sealed record ThemeStyleDocument(string LegacyText, IReadOnlyList<string> Imports, IReadOnlyList<ThemeStyleNode> Nodes)
{
    /// <summary>Optional implicit namespace scope for controls, templates, and typed resources.</summary>
    public string? Namespace { get; init; }
}

/// <summary>Shared structural parser for theme modules. Values are bound through the UI type catalog.</summary>
public static class ControlThemeSyntax
{
    public static IEnumerable<string> Classes(ThemeStyleDocument document) => document.Nodes
        .Where(n => !n.Header.StartsWith("@", StringComparison.Ordinal))
        .SelectMany(n => System.Text.RegularExpressions.Regex.Matches(n.Header, @"\.([A-Za-z_][\w-]*)")
            .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value)).Distinct(StringComparer.Ordinal);
    public static ThemeStyleDocument Parse(string text, List<UiDiagnostic> diagnostics, int spanOffset = 0)
    {
        var errors = new List<UiDiagnostic>();
        var parser = new Reader(text, errors, spanOffset);
        var nodes = parser.Read(false);
        var advanced = nodes.Any(n => n.Header.StartsWith("@theme ", StringComparison.Ordinal) ||
            n.Header.StartsWith("@using ", StringComparison.Ordinal) ||
            n.Header.StartsWith("@namespace ", StringComparison.Ordinal));
        if (!advanced) return new(text, Array.Empty<string>(), Array.Empty<ThemeStyleNode>());
        diagnostics.AddRange(errors);
        var imports = new List<string>();
        var rules = new List<ThemeStyleNode>();
        string? ns = null;
        var chars = text.ToCharArray();
        foreach (var node in nodes)
        {
            if (node.Header.StartsWith("@using ", StringComparison.Ordinal))
            {
                var import = node.Header.Substring(7).Trim();
                if (import.Length > 0) imports.Add(import);
            }
            else if (node.Header.StartsWith("@namespace ", StringComparison.Ordinal))
            {
                var declared = node.Header.Substring(11).Trim();
                if (ns is not null)
                    diagnostics.Add(UiDiagnostics.Error(UiDiagnostics.StyleSyntax,
                        "duplicate '@namespace' declaration; a style module may declare it only once",
                        new(node.Span.Start + spanOffset, node.Span.Length)));
                else if (declared.Length > 0) ns = declared;
            }
            else rules.Add(node);
            for (var i = node.Span.Start; i < node.Span.End; i++) if (chars[i] is not '\r' and not '\n') chars[i] = ' ';
        }
        return new(new string(chars), imports, rules) { Namespace = ns };
    }

    private sealed class Reader(string text, List<UiDiagnostic> diagnostics, int spanOffset)
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
                if (!nested && StartsDirective("namespace"))
                {
                    result.Add(ReadNamespace());
                    continue;
                }
                // Imports are line directives, just like in .bui; they must not consume the next rule.
                if (!nested && StartsDirective("using"))
                {
                    result.Add(ReadUsing());
                    continue;
                }
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

        private bool StartsDirective(string name)
        {
            var directive = "@" + name;
            if (text.Length - _i < directive.Length ||
                string.CompareOrdinal(text, _i, directive, 0, directive.Length) != 0) return false;
            var end = _i + directive.Length;
            return end == text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] is '_' or '-');
        }

        private ThemeStyleNode ReadNamespace()
        {
            var start = _i;
            while (_i < text.Length && text[_i] is not '\r' and not '\n') _i++;
            var line = text.Substring(start, _i - start);
            var token = new Lexer(line).Next();
            var span = new TextSpan(start, line.Length);
            if (token.Kind == TokenKind.Error)
            {
                diagnostics.Add(UiDiagnostics.Error(UiDiagnostics.StyleSyntax,
                    token.Aux ?? "invalid '@namespace' directive", new(start + spanOffset, line.Length)));
                return new("@namespace ", "", Array.Empty<ThemeStyleNode>(), span);
            }
            if (token.Kind != TokenKind.Directive || token.Text != "namespace" || token.Aux is null)
            {
                diagnostics.Add(UiDiagnostics.Error(UiDiagnostics.StyleSyntax,
                    "expected '@namespace Dotted.Name'", new(start + spanOffset, line.Length)));
                return new("@namespace ", "", Array.Empty<ThemeStyleNode>(), span);
            }
            return new("@namespace " + token.Aux, "", Array.Empty<ThemeStyleNode>(), span);
        }

        private ThemeStyleNode ReadUsing()
        {
            var start = _i;
            while (_i < text.Length && text[_i] is not '\r' and not '\n') _i++;
            var span = new TextSpan(start, _i - start);
            var token = new Lexer(text.Substring(start, span.Length)).Next();
            string? import = null;
            if (token.Kind == TokenKind.Error)
                diagnostics.Add(UiDiagnostics.Error(UiDiagnostics.StyleSyntax, token.Aux!, new(span.Start + spanOffset, span.Length)));
            else if (token.Aux2 is not null)
                diagnostics.Add(UiDiagnostics.Error(UiDiagnostics.StyleSyntax, "Style modules support namespace imports: @using Namespace", new(span.Start + spanOffset, span.Length)));
            else
                import = token.Aux;
            return new("@using " + import, "", Array.Empty<ThemeStyleNode>(), span);
        }
        private void Trivia()
        {
            while (_i < text.Length)
            {
                if (char.IsWhiteSpace(text[_i])) { _i++; continue; }
                if (_i + 1 < text.Length && text[_i] == '/' && text[_i + 1] == '/') { while (_i < text.Length && text[_i] is not '\r' and not '\n') _i++; continue; }
                if (_i + 1 < text.Length && text[_i] == '/' && text[_i + 1] == '*')
                {
                    var end = text.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    if (end < 0) { Error("Unterminated comment", _i); _i = text.Length; return; }
                    _i = end + 2; continue;
                }
                break;
            }
        }
        private void Error(string message, int start) => diagnostics.Add(UiDiagnostics.Error(
            UiDiagnostics.StyleSyntax, message, new(start + spanOffset, Math.Min(1, text.Length - start))));
    }
}
