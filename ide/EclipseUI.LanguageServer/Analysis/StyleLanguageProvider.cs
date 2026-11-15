using EclipseUI.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EclipseUI.LanguageServer.Workspace;
using System.Text.RegularExpressions;
using Token = EclipseUI.LanguageServer.Analysis.StyleDocument.Token;
using Node = EclipseUI.LanguageServer.Analysis.StyleDocument.Node;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>Project-scoped stylesheet editing. Semantic types come from the compiler catalog.</summary>
internal sealed partial class StyleLanguageProvider
{
    private readonly UiCatalog _catalog;
    private readonly Compilation? _compilation;
    private readonly ProjectSnapshot? _project;
    private readonly ProjectWorkspace? _workspace;
    private readonly Dictionary<string, string> _files;
    private readonly StyleDocument _document;
    private readonly StyleDocument[] _styles;
    private readonly Template[] _templates;
    private readonly Theme[] _themes;
    private sealed record Template(string Name, string Namespace, string? Target, string? Path, string? Text, UiDocument? Ast);
    private sealed record Theme(string Name, string Target, StyleDocument Document, Node Node, Token Token);
    private static readonly string[] States = ["hover", "pressed", "active", "focus", "focus-visible", "focus-within", "disabled"];

    public StyleLanguageProvider(string path, string text, UiCatalog catalog, Compilation? compilation,
        Dictionary<string, string> files, ProjectSnapshot? project = null, StyleDocument? document = null, ProjectWorkspace? workspace = null)
    {
        _catalog = catalog; _compilation = compilation;
        _project = project; _files = files; _workspace = workspace;
        _document = document ?? new(path, text);
        _styles = files.SelectMany(f => StyleDocuments(f.Key, f.Value, includeInline: false))
            .Select(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase) && d.Region == _document.Region ? _document : d).ToArray();
        _themes = _styles.SelectMany(d => d.Nodes.Where(n => n.Header.Count >= 4 && n.Header[0].Value == "@theme" && n.Header[2].Value == "for")
            .Select(n => new Theme(n.Header[1].Value, n.Header[3].Value, d, n, n.Header[1]))).ToArray();
        var templates = new List<Template>();
        foreach (var file in files.Where(f => f.Key.EndsWith(".ui", StringComparison.OrdinalIgnoreCase)))
        {
            var declaration = UiTemplateScanner.FromFile(file.Key, file.Value);
            if (declaration?.TemplateTarget is not { } target) continue;
            UiDocument? ast = null;
            try { ast = FrontEnd.Parse(file.Value, file.Key, new List<UiDiagnostic>()); } catch (Exception) { }
            var imports = declaration.Usings.Where(u => !u.Contains('=')).Select(u => u.Replace("using ", "").Trim().TrimEnd(';')).ToArray();
            var alias = ast?.Nodes.OfType<UiUsingDirective>().FirstOrDefault(u => u.Alias == target)?.AliasTarget;
            var resolved = (alias is null ? catalog.Resolve(target, imports, TemplateAnalyzer.EnclosingNamespaces(declaration.Namespace)).Element :
                catalog.FindByTypeDisplayName(alias.StartsWith("global::", StringComparison.Ordinal) ? alias : "global::" + alias))?.ClrTypeDisplayName;
            templates.Add(new(declaration.Tag, declaration.Namespace ?? "", resolved, file.Key, file.Value, ast));
        }
        // Imported template factories can also come from referenced theme libraries.
        foreach (var ns in _styles.SelectMany(s => s.Imports).Distinct(StringComparer.Ordinal))
        {
            INamespaceSymbol? symbol = compilation?.GlobalNamespace;
            foreach (var segment in ns.Split('.')) symbol = symbol?.GetNamespaceMembers().FirstOrDefault(n => n.Name == segment);
            foreach (var type in symbol?.GetTypeMembers() ?? Enumerable.Empty<INamedTypeSymbol>())
            {
                if (type.IsAbstract || type.DeclaredAccessibility != Accessibility.Public) continue;
                var parent = type.BaseType;
                if (parent?.OriginalDefinition.ToDisplayString() != "EclipseUI.ControlTemplate<T>" || templates.Any(t => t.Name == type.Name && t.Namespace == ns)) continue;
                templates.Add(new(type.Name, ns, parent.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), null, null, null));
            }
        }
        _templates = templates.ToArray();
    }

    private UiElementDef? Resolve(string name, StyleDocument? document = null) =>
        _catalog.Resolve(name, (document ?? _document).Imports, null).Element;
    private bool Compatible(UiElementDef? actual, string? expected)
    {
        if (actual is null || expected is null) return false;
        if (actual.ClrTypeDisplayName == expected) return true;
        var symbol = _compilation?.GetTypeByMetadataName(actual.ClrTypeDisplayName.Replace("global::", ""));
        for (; symbol is not null; symbol = symbol.BaseType)
            if (symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == expected) return true;
        return false;
    }
    private UiElementDef? Target(Node? node, StyleDocument? document = null, bool owner = false)
    {
        if (node is null) return null;
        var header = node.Header;
        if (!node.Block && node.Parent is not null && header.FirstOrDefault()?.Value != "&" && !header.Any(t => t.Value == "/template/"))
            return Target(node.Parent, document, owner);
        if (header.Count >= 4 && header[0].Value == "@theme") return Resolve(header[3].Value, document);
        var boundary = header.FindIndex(t => t.Value == "/template/");
        if (!owner && boundary >= 0 && header.Count > boundary + 1) return Resolve(header[boundary + 1].Value, document);
        if (header.FirstOrDefault()?.Value == "&") return Target(node.Parent, document, owner);
        return header.Count > 0 && header[0].Value != "." ? Resolve(header[0].Value, document) : Resolve("TemplatedControl", document);
    }
    private UiPropDef? Property(Node? node) => node is not null && !node.Block && node.Header.Count >= 2 && node.Header[1].Value == ":"
        && !Legacy(node) ? Target(node)?.Properties.GetValueOrDefault(node.Header[0].Value) : null;
    private static string Qualified(Template t) => t.Namespace.Length == 0 ? t.Name : t.Namespace + "." + t.Name;
    private Template? FindTemplate(string name, StyleDocument? document = null) => Unique(_templates.Where(t => Qualified(t) == name || t.Name == name && (document ?? _document).Imports.Contains(t.Namespace)));
    private Theme? FindTheme(string name) => Unique(_themes.Where(t => t.Name == name));
    private static T? Unique<T>(IEnumerable<T> items) where T : class { var list = items.Take(2).ToArray(); return list.Length == 1 ? list[0] : null; }
    private UiTextEdit? Import(string ns) => ns.Length == 0 || _document.Imports.Contains(ns) ? null :
        new(new(_document.Text.StartsWith('\uFEFF') ? 1 : 0, 0), "@using " + ns + ";" + (_document.Text.Contains("\r\n") ? "\r\n" : "\n"));

    public UiCompletionList Complete(int offset)
    {
        offset = Math.Clamp(offset, 0, _document.Text.Length);
        if (_document.InCommentOrString(offset)) return UiCompletionList.Empty;
        var text = _document.Text;
        var start = offset; var end = offset;
        while (start > 0 && Word(text[start - 1])) start--;
        while (end < text.Length && Word(text[end])) end++;
        if (start > 0 && text[start - 1] == '/' && (start < 2 || text[start - 2] != '/'))
        {
            start--;
            if (end < text.Length && text[end] == '/') end++;
        }
        var prefix = text[start..offset];
        var node = _document.At(offset);
        var header = node?.Header.Where(t => t.Span.Start < start).ToArray() ?? [];
        var items = new List<UiCompletionItem>();
        var replacement = new TextSpan(start, end - start);
        if (LegacyComplete(offset, start, end, node) is { } legacy) return legacy;
        void Add(string name, UiCompletionKind kind, string? detail = null, UiTextEdit? import = null, string? insertion = null) =>
            items.Add(new(name, kind, detail, InsertText: insertion, AdditionalEdits: import is null ? null : [import]));
        void Types(bool templated)
        {
            foreach (var type in _catalog.Elements.Where(e => !templated || e.Properties.ContainsKey("Template")))
                Add(type.Tag, UiCompletionKind.Class, type.ClrTypeDisplayName, Import(TypeNames.NamespaceOf(type.ClrTypeDisplayName) ?? ""));
        }
        var function = header.LastOrDefault(t => t.Value is "resource" or "template");
        if (function is not null && header.Any(t => t.Value == "(") && header.LastOrDefault()?.Value is "(" or ".")
        {
            // Qualified template names replace their full argument, including a suffix after the cursor.
            var open = header.Last(t => t.Value == "(").Span.End;
            start = open; while (start < offset && char.IsWhiteSpace(text[start])) start++;
            end = offset; while (end < text.Length && (Word(text[end]) || text[end] == '.')) end++;
            prefix = text[start..offset]; replacement = new(start, end - start);
            if (function.Value == "resource")
            {
                if (node?.Header.FirstOrDefault()?.Value == "Theme")
                    foreach (var theme in _themes.Where(t => FindTheme(t.Name) is not null && Compatible(Target(node), Resolve(t.Target, t.Document)?.ClrTypeDisplayName)))
                        Add(theme.Name, UiCompletionKind.Variable, "ControlTheme for " + theme.Target);
            }
            else if (node?.Header.FirstOrDefault()?.Value == "Template")
                foreach (var template in _templates.Where(t => Compatible(Target(node), t.Target)))
                {
                    var qualified = prefix.Contains('.') || _templates.Count(t => t.Name == template.Name) > 1;
                    Add(qualified ? Qualified(template) : template.Name, UiCompletionKind.Class,
                        "ControlTemplate for " + template.Target, qualified ? null : Import(template.Namespace));
                }
        }
        else if (node?.Header.FirstOrDefault()?.Value == "@using" && start >= node.Header[0].Span.End)
        {
            var argStart = node.Header[0].Span.End;
            while (argStart < offset && char.IsWhiteSpace(text[argStart])) argStart++;
            end = offset; while (end < text.Length && (Word(text[end]) || text[end] == '.')) end++;
            prefix = text[argStart..offset]; replacement = new(argStart, end - argStart);
            var namespaces = _compilation is null ? [] : NamespaceNames(_compilation.GlobalNamespace);
            foreach (var ns in namespaces.Concat(_catalog.Elements.Select(e => TypeNames.NamespaceOf(e.ClrTypeDisplayName)).OfType<string>()).Concat(_templates.Select(t => t.Namespace)).Distinct())
                Add(ns, UiCompletionKind.Namespace);
        }
        else if (node?.Header.FirstOrDefault()?.Value == "@theme" && (!node.Block || offset <= node.Open) && (header.LastOrDefault()?.Value == "for" || node.Header.Count >= 4 && start >= node.Header[3].Span.Start)) Types(true);
        else if (node?.Header.FirstOrDefault()?.Value == "@theme" && (!node.Block || offset <= node.Open) && header.Length == 2) Add("for", UiCompletionKind.Keyword);
        else if (start > 0 && text[start - 1] == '@' || prefix.StartsWith('@'))
        {
            if (start > 0 && text[start - 1] == '@') { start--; replacement = new(start, end - start); prefix = text[start..offset]; }
            Add("@using", UiCompletionKind.Directive, "Import control and template namespaces", insertion: "@using ");
            Add("@theme", UiCompletionKind.Directive, "@theme Name for ControlType { ... }", insertion: "@theme ");
            Add("@states", UiCompletionKind.Directive, "Declare custom states", insertion: "@states { ");
        }
        else if (node is { Block: false, Parent: not null } && node.Header.Count > 1 && node.Header[1].Value == ":" && start >= node.Header[1].Span.End && node.Header[0].Value != "&")
        {
            var property = Property(node);
            if (property is not null)
            {
                foreach (var value in property.TypeShape.EnumMembers) Add(value, UiCompletionKind.EnumMember, property.ClrTypeDisplayName);
                foreach (var value in _catalog.FindLiteralParser(property.TypeShape.CanonicalKey)?.Candidates ?? []) Add(value, UiCompletionKind.LiteralValue);
                if (property.TypeShape.CanonicalKey == "bool") { Add("true", UiCompletionKind.Keyword); Add("false", UiCompletionKind.Keyword); }
                if (property.Name == "Theme") Add("resource", UiCompletionKind.Method, "resource(ControlThemeName)", insertion: "resource(");
                else if (property.Name == "Template") Add("template", UiCompletionKind.Method, "template(TemplateType)", insertion: "template(");
                else Add("theme", UiCompletionKind.Method, "theme(dark, light)", insertion: "theme(");
            }
        }
        else if (start > 0 && text[start - 1] == ':' && (node?.Block == true || node?.Parent is null || node?.Header.FirstOrDefault()?.Value == "&"))
            foreach (var state in StateNames()) Add(state, UiCompletionKind.Keyword, "Control state");
        else if (start > 0 && text[start - 1] == '.')
        {
            var partType = PartSelectorType(node);
            foreach (var part in Parts(Target(node, owner: true)).Where(p => partType is null || p.Type == partType))
                Add(part.Name, UiCompletionKind.Variable, part.Type + " template class");
        }
        else
        {
            var selector = node is not null && (!node.Block || offset <= node.Open) && (prefix.StartsWith('/') || node.Header.Any(t => t.Value is "/template/" or "&"));
            var inBody = !selector && (node?.Block == true && offset > node.Open || node?.Parent is not null && !node.Block);
            if (inBody)
            {
                var target = node?.Block == true ? Target(node) : Target(node?.Parent);
                foreach (var property in target?.Properties.Values ?? Enumerable.Empty<UiPropDef>())
                    Add(property.Name, UiCompletionKind.StyleProperty, TypeNames.Short(property.ClrTypeDisplayName), insertion: property.Name);
                Add("&:hover", UiCompletionKind.Keyword, "Nested state rule");
            }
            else
            {
                Types(false);
                Add("/template/", UiCompletionKind.Keyword, "Select inside this control's template");
                Add("@theme", UiCompletionKind.Directive); Add("@using", UiCompletionKind.Directive);
                Add("@states", UiCompletionKind.Directive);
            }
        }
        return new(replacement, items.Where(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(i => (i.Label, i.Detail)).OrderBy(i => i.Label, StringComparer.Ordinal).ToArray());
    }
    private static bool Word(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';
    private static IEnumerable<string> NamespaceNames(INamespaceSymbol root)
    {
        foreach (var child in root.GetNamespaceMembers())
        {
            yield return child.ToDisplayString();
            foreach (var name in NamespaceNames(child)) yield return name;
        }
    }

    private sealed record Part(string Name, string Type, string Path, string Text, TextSpan Span);
    private IEnumerable<Part> Parts(UiElementDef? owner)
    {
        foreach (var template in _templates.Where(t => t.Ast is not null && Compatible(owner, t.Target)))
            foreach (var element in Elements(template.Ast!.Nodes, _catalog))
                foreach (var attr in element.Attributes.Where(a => a.Name == "Class" && a.Kind == AttrValueKind.Literal))
                    foreach (Match match in Regex.Matches(attr.Value, @"[A-Za-z_][\w-]*"))
                    {
                        var valueStart = template.Text!.IndexOf(attr.Value, attr.ValueSpan.Start, StringComparison.Ordinal);
                        if (valueStart >= 0) yield return new(match.Value, element.Tag, template.Path!, template.Text!, new(valueStart + match.Index, match.Length));
                    }
    }
    private static IEnumerable<UiElement> Elements(IEnumerable<UiNode> nodes, UiCatalog? boundaryCatalog = null)
    {
        foreach (var node in nodes)
        {
            if (node is UiElement element)
            {
                yield return element;
                if (boundaryCatalog is not null && (boundaryCatalog.Elements.Any(t => t.Tag == element.Tag && t.Properties.ContainsKey("Template")) || element.Tag == "ContentPresenter")) continue;
                foreach (var child in Elements(element.Children, boundaryCatalog)) yield return child;
            }
            else if (node is UiIf condition)
            {
                foreach (var child in Elements(condition.Children, boundaryCatalog)) yield return child;
                foreach (var branch in condition.ElseIfs) foreach (var child in Elements(branch.Children, boundaryCatalog)) yield return child;
                foreach (var child in Elements(condition.ElseChildren ?? [], boundaryCatalog)) yield return child;
            }
            else if (node is UiForeach loop) foreach (var child in Elements(loop.Children, boundaryCatalog)) yield return child;
            else if (node is UiSwitch choice) foreach (var section in choice.Sections) foreach (var child in Elements(section.Children, boundaryCatalog)) yield return child;
        }
    }

    private static string? PartSelectorType(Node? node)
    {
        var boundary = node?.Header.FindIndex(t => t.Value == "/template/") ?? -1;
        return boundary >= 0 ? node!.Header.ElementAtOrDefault(boundary + 1)?.Value : null;
    }

    public UiDefinition? ClassDefinition(string name, string target)
    {
        var matches = new List<UiDefinition>();
        foreach (var document in _styles.Where(s => s.Advanced))
            foreach (var node in document.Nodes.Where(n => n.Block && n.Header.FirstOrDefault()?.Value == target))
            {
                var header = node.Header.TakeWhile(t => t.Value != "/template/").ToArray();
                for (var i = 1; i < header.Length; i++)
                    if (header[i - 1].Value == "." && header[i].Value == name)
                        matches.Add(new(document.Path, header[i].Span, name, document.Text));
            }
        return Unique(matches);
    }

    public UiDefinition? ThemeDefinition(string name) => FindTheme(name) is { } theme
        ? new(theme.Document.Path, theme.Token.Span, theme.Name, theme.Document.Text) : null;

    public (string Name, TextSpan Span)? CSharpThemeAt(int offset)
    {
        if (_project is null || !_project.GeneratedDocuments.TryGetValue(_document.Path, out var generated) ||
            !generated.Mappings.TryMapPositionToGenerated(offset, out var position)) return null;
        var token = generated.Tree.GetRoot().FindToken(position);
        var identifier = token.Parent?.AncestorsAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault();
        if (identifier is null) return null;
        var symbol = _project.Compilation.GetSemanticModel(generated.Tree).GetSymbolInfo(identifier).Symbol;
        if (!IsThemeSymbol(symbol) || FindTheme(symbol!.Name) is null) return null;
        return generated.Mappings.TryMapToOriginal(new(identifier.Span.Start, identifier.Span.Length), out var span)
            ? (symbol.Name, span) : null;
    }

    private bool IsThemeSymbol(ISymbol? symbol) => symbol is IPropertySymbol { IsStatic: true } &&
        symbol.ContainingType.Name == "StyleBootstrap" &&
        symbol.ContainingType.ContainingNamespace.ToDisplayString() == _project?.Evaluation.RootNamespace &&
        SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, _project?.Compilation.Assembly) &&
        symbol.Locations.Any(l => l.SourceTree?.FilePath.EndsWith("ControlThemes.g.cs", StringComparison.Ordinal) == true);

    public UiRenameResult? RenameTheme(string name, string newName)
    {
        if (FindTheme(name) is not { } theme) return null;
        return RenameCore(theme, newName);
    }

    private (Token? Token, Node? Node, string? Function, string Argument) Context(int offset)
    {
        var token = _document.TokenAt(offset);
        var node = _document.At(offset);
        if (token is null || token.Trivia || token.Quoted || node is null) return (null, null, null, "");
        var index = node.Header.IndexOf(token);
        for (var i = index - 1; i > 0; i--)
        {
            if (node.Header[i].Value == ")") break;
            if (node.Header[i].Value != "(") continue;
            var argument = string.Concat(node.Header.Skip(i + 1).TakeWhile(t => t.Value != ")").Select(t => t.Value));
            return (token, node, node.Header[i - 1].Value, argument);
        }
        return (token, node, null, "");
    }
    public UiHoverResult? Hover(int offset)
    {
        if (LegacyHover(offset) is { } legacy) return legacy;
        var (token, node, function, argument) = Context(offset);
        if (token is null) return null;
        string? description = token.Value switch
        {
            "@theme" => "**@theme Name for ControlType**\n\nReusable control theme with one template and typed state setters.",
            "@using" => "Import a namespace containing controls or template factories.",
            "/template/" => "Explicit template boundary. Matches owned visual parts; does not cross nested templates or borrowed content.",
            "template" => "**template(TemplateType)** — construct a reusable control template factory.",
            "resource" => "**resource(Name)** — control theme in this compilation module.",
            "theme" => "**theme(dark, light)** — select a typed literal using the effective theme variant.",
            _ => null,
        };
        if (function == "resource" && FindTheme(argument) is { } theme) description = $"**{theme.Name}** — ControlTheme for `{theme.Target}`";
        else if (function == "template" && FindTemplate(argument) is { } template) description = $"**{Qualified(template)}**\n\nControlTemplate for `{template.Target}`; each application creates a fresh visual tree.";
        else if (node is not null && node.Header.FirstOrDefault() == token && Property(node) is { } prop)
            description = $"**{prop.Name}** : `{TypeNames.Short(prop.ClrTypeDisplayName)}`\n\nStyled property declared by `{TypeNames.Short(prop.DeclaringTypeName)}`.";
        else if (node?.Header.FirstOrDefault()?.Value == "@theme" && node.Header.ElementAtOrDefault(1) == token)
            description = $"**{token.Value}** — control theme";
        else if (Resolve(token.Value) is { } type) description = $"**{type.Tag}**\n\n`{TypeNames.Short(type.ClrTypeDisplayName)}`";
        else if (States.Contains(token.Value)) description = $"**:{token.Value}** — control state selector";
        return description is null ? null : new(token.Span, description);
    }

    public UiDefinition? Definition(int offset)
    {
        if (ClassDefinitionAt(offset) is { } styleClass) return styleClass;
        if (LegacyDefinition(offset) is { } legacy) return legacy;
        var (token, node, function, argument) = Context(offset);
        if (token is null) return null;
        if (function == "resource" && FindTheme(argument) is { } theme)
            return new(theme.Document.Path, theme.Token.Span, theme.Name, theme.Document.Text);
        if (function == "template" && FindTemplate(argument) is { } template)
        {
            if (template.Path is not null && template.Text is not null)
            {
                var at = template.Text.IndexOf("@template", StringComparison.Ordinal);
                return new(template.Path, new(Math.Max(0, at), 9), template.Name, template.Text);
            }
            return ClrDefinition(Qualified(template));
        }
        if (node?.Header.FirstOrDefault() == token && Property(node) is { } property)
            return ClrDefinition(property.DeclaringTypeName, property.Name);
        if (node is not null && node.Header.IndexOf(token) > 0 && node.Header[node.Header.IndexOf(token) - 1].Value == "." && node.Header.Any(t => t.Value == "/template/"))
        {
            var part = Unique(Parts(Target(node, owner: true)).Where(p => p.Name == token.Value && p.Type == PartSelectorType(node)));
            if (part is not null) return new(part.Path, part.Span, part.Name, part.Text);
        }
        if (Resolve(token.Value) is { } type) return ClrDefinition(type.ClrTypeDisplayName);
        return null;
    }
    private UiDefinition? ClrDefinition(string type, string? member = null)
    {
        var symbol = _compilation?.GetTypeByMetadataName(type.Replace("global::", ""));
        if (symbol is null) return null;
        ISymbol? target = member is null ? symbol : symbol.GetMembers(member).FirstOrDefault();
        if (target is null) return null;
        var analysis = DocumentAnalysis.Create("@namespace __StyleProbe\n@using EclipseUI.Elements\n<Border />", "StyleProbe.ui", _catalog, _compilation,
            project: _project, workspace: _workspace, documentPath: _document.Path);
        return DefinitionProvider.SymbolDefinition(analysis, target);
    }

    private (Theme Theme, Token Token)? ThemeAt(int offset)
    {
        var (token, node, function, argument) = Context(offset);
        if (token is null) return null;
        var theme = function == "resource" ? FindTheme(argument) :
            node?.Header.FirstOrDefault()?.Value == "@theme" && node.Header.ElementAtOrDefault(1) == token ? FindTheme(token.Value) : null;
        return theme is null ? null : (theme, token);
    }
    public UiPrepareRename? PrepareRename(int offset) => ClassPrepareRename(offset) ?? LegacyPrepareRename(offset) ?? (ThemeAt(offset) is { } hit ? new(hit.Token.Span, hit.Theme.Name) : null);
    public UiRenameResult? Rename(int offset, string newName)
    {
        if (ClassPrepareRename(offset) is not null) return RenameClassAt(offset, newName);
        return LegacyPrepareRename(offset) is not null ? LegacyRename(offset, newName) : ThemeAt(offset) is { } hit ? RenameCore(hit.Theme, newName) : null;
    }
    private UiRenameResult? RenameCore(Theme theme, string newName)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName) || SyntaxFacts.GetKeywordKind(newName) != SyntaxKind.None ||
            newName is "Apply" or "ApplyControlThemes" or "StyleBootstrap" || _themes.Any(t => t.Name == newName && t != theme)) return null;
        var files = new List<UiRenameFile>();
        foreach (var doc in _styles)
        {
            var edits = new List<UiTextEdit>();
            if (doc == theme.Document) edits.Add(new(theme.Token.Span, newName));
            foreach (var node in doc.AllNodes.Where(n => !n.Block))
                for (var i = 0; i + 3 < node.Header.Count; i++)
                    if (node.Header[i].Value == "resource" && node.Header[i + 1].Value == "(" && node.Header[i + 2].Value == theme.Name && node.Header[i + 3].Value == ")")
                        edits.Add(new(node.Header[i + 2].Span, newName));
            if (edits.Count > 0) files.Add(new(doc.Path, doc.Text, edits.OrderBy(e => e.Span.Start).ToArray()));
        }
        if (_project is not null)
        {
            foreach (var tree in _project.Compilation.SyntaxTrees)
            {
                if (tree.FilePath.EndsWith("ControlThemes.g.cs", StringComparison.Ordinal)) continue;
                var generated = _project.GeneratedDocuments.FirstOrDefault(pair => ReferenceEquals(pair.Value.Tree, tree));
                var source = _project.Evaluation.CompileFiles.Contains(tree.FilePath, StringComparer.OrdinalIgnoreCase);
                if (!source && generated.Value is null) continue;
                var model = _project.Compilation.GetSemanticModel(tree);
                var edits = new List<UiTextEdit>();
                foreach (var identifier in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == theme.Name))
                {
                    if (!IsThemeSymbol(model.GetSymbolInfo(identifier).Symbol)) continue;
                    var span = new TextSpan(identifier.Span.Start, identifier.Span.Length);
                    if (generated.Value is not null && !generated.Value.Mappings.TryMapToOriginal(span, out span)) return null;
                    edits.Add(new(span, newName));
                }
                if (edits.Count == 0) continue;
                var path = generated.Value is null ? tree.FilePath : generated.Key;
                var text = generated.Value is null ? tree.ToString() : _files.GetValueOrDefault(path);
                if (text is null) return null;
                files.Add(new(path, text, edits.DistinctBy(e => e.Span).ToArray()));
            }
        }
        return new(files);
    }
    public IReadOnlyList<UiSymbol> Symbols() => _document.Inline ? [] : _document.Nodes
        .Where(n => n.Block || n.Header.FirstOrDefault()?.Value.StartsWith('$') == true).Select(Symbol).ToArray();

    public UiSignatureHelp? SignatureHelp(int offset)
    {
        if (_document.InCommentOrString(offset)) return null;
        var tokens = _document.Tokens.Where(t => !t.Trivia && t.Span.Start < offset).ToArray();
        var depth = 0; var parameter = 0;
        for (var i = tokens.Length - 1; i > 0; i--)
        {
            if (tokens[i].Value == ")") depth++;
            else if (tokens[i].Value == "(")
            {
                if (depth > 0) { depth--; continue; }
                var name = tokens[i - 1].Value;
                UiSignature? signature = name switch
                {
                    "theme" => new("theme(dark, light)", "Both literals use the property's type; the effective theme selects one.", [new("dark", "Value in a dark theme"), new("light", "Value in a light theme")]),
                    "resource" => new("resource(name)", "Control theme declared in this compilation module; target types must be compatible.", [new("name", "Control theme name")]),
                    "template" => new("template(type)", "Imported ControlTemplate factory compatible with the owning control.", [new("type", "Template factory type")]),
                    _ => null,
                };
                return signature is null ? null : new([signature], 0, Math.Min(parameter, signature.Parameters.Count - 1));
            }
            else if (tokens[i].Value == "," && depth == 0) parameter++;
            else if (tokens[i].Value is ";" or "{" or "}") break;
        }
        return null;
    }

    public IReadOnlyList<UiCodeAction> CodeActions()
    {
        var result = new List<UiCodeAction>();
        foreach (var node in _document.AllNodes)
        {
            if (!node.Block && node.Parent is not null && node.Header.Count >= 2 && node.Header[1].Value == ":")
            {
                if (Legacy(node))
                {
                    var token = node.Header[0];
                    if (_catalog.StyleProperties.Find(token.Value) is null)
                        foreach (var name in _catalog.StyleProperties.SortedNames().Where(n => Distance(n, token.Value) <= 2).Take(3))
                            result.Add(new($"Replace with '{name}'", "quickfix", null, [new(token.Span, name)], "EUI051"));
                    continue;
                }
                if (Target(node) is not { } target || target.Properties.ContainsKey(node.Header[0].Value)) continue;
                var misspelled = node.Header[0];
                foreach (var name in target.Properties.Keys.Where(n => Distance(n, misspelled.Value) <= 2).OrderBy(n => Distance(n, misspelled.Value)).Take(3))
                    result.Add(new($"Replace with '{name}'", "quickfix", null, [new(misspelled.Span, name)], "EUI050"));
                continue;
            }
            if (!node.Block) continue;
            if (Legacy(node)) continue;
            var names = node.Header.FirstOrDefault()?.Value == "@theme" ? node.Header.Skip(3).Take(1) : node.Header.Take(1);
            var boundary = node.Header.FindIndex(t => t.Value == "/template/");
            if (boundary >= 0) names = names.Concat(node.Header.Skip(boundary + 1).Take(1));
            foreach (var token in names)
            {
                if (Resolve(token.Value) is not null) continue;
                foreach (var type in _catalog.Elements.Where(e => e.Tag == token.Value))
                    if (TypeNames.NamespaceOf(type.ClrTypeDisplayName) is { } ns && Import(ns) is { } edit)
                        result.Add(new("Add @using " + ns, "quickfix", null, [edit], "EUI050"));
            }
        }
        return result.DistinctBy(a => (a.Title, a.Edits[0].Span)).ToArray();
    }
    private static int Distance(string left, string right)
    {
        var row = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var previous = row[0]; row[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var next = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (left[i - 1] == right[j - 1] ? 0 : 1));
                previous = next;
            }
        }
        return row[^1];
    }

    private UiSymbol Symbol(Node node)
    {
        var header = _document.Text[node.Start..(node.Block ? node.Open : node.End)].Trim();
        var selected = node.Header.FirstOrDefault(t => t.Value != "@theme")?.Span ?? new(node.Start, 0);
        return new(header, node.Header.FirstOrDefault()?.Value == "@theme" ? "Control theme" : "Style rule", UiSymbolKind.Block,
            new(node.Start, node.End - node.Start), selected, node.Children.Where(n => n.Block).Select(Symbol).ToArray());
    }
    public IReadOnlyList<UiSemanticToken> SemanticTokens()
    {
        var map = new PositionMap(_document.Text);
        var result = new List<UiSemanticToken>();
        foreach (var token in _document.Tokens.Where(t => !t.Trivia && !t.Quoted))
        {
            var (t, node, function, argument) = Context(token.Span.Start);
            var kind = token.Value.StartsWith('@') || token.Value is "for" or "/template/" || States.Contains(token.Value) ? "keyword" :
                function == "resource" && FindTheme(argument) is not null ? "variable" :
                function == "template" && FindTemplate(argument) is not null ? "class" :
                node?.Header.FirstOrDefault()?.Value == "@theme" && node.Header.ElementAtOrDefault(1) == token ? "variable" :
                token.Value.StartsWith('$') || StateToken(node, token) && StateNames().Contains(token.Value) ? "variable" :
                node?.Header.FirstOrDefault() == token && (Property(node) is not null || LegacyProperty(node) is not null) ? "property" :
                Resolve(token.Value) is not null ? "class" : null;
            if (kind is null) continue;
            var (line, character) = map.ToPosition(token.Span.Start);
            result.Add(new(line, character, token.Span.Length, SemanticTokenTypes.IndexOf(kind), 0));
        }
        return result;
    }
    public IReadOnlyList<UiTextEdit> Format()
    {
        if (_document.Inline) return [];
        // Indentation only: preserve literals, comments, line endings and incomplete input.
        var edits = new List<UiTextEdit>();
        var offset = 0;
        foreach (var line in _document.Text.Split('\n'))
        {
            var indent = line.TakeWhile(c => c is ' ' or '\t').Count();
            var first = offset + indent;
            if (first >= _document.Region.Start && first < _document.Region.End && offset >= _document.Region.Start &&
                indent < line.TrimEnd('\r').Length && !_document.InCommentOrString(first))
            {
                var depth = _document.AllNodes.Count(n => n.Block && n.Open < first && n.End > first);
                if (line[indent] == '}') depth = Math.Max(0, depth - 1);
                var desired = new string(' ', depth * 4 + (_document.Embedded ? 4 : 0));
                if (line[..indent] != desired) edits.Add(new(new(offset, indent), desired));
            }
            offset += line.Length + 1;
        }
        return edits;
    }
}
