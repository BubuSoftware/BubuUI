using System.Runtime.CompilerServices;
using EclipseUI.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>Discover accessible namespace types from the same project symbols and explicitly import them.</summary>
internal static class UnimportedTypeCompletionProvider
{
    private static readonly ConditionalWeakTable<Compilation, TypeIndex> Indexes = new();

    internal static Func<CompletionItem, bool> CreateImportFilter(SemanticModel model, int position)
    {
        var namespaces = new Dictionary<string, INamespaceSymbol?>(StringComparer.Ordinal);
        HashSet<(string Name, string Namespace)>? visibleTypes = null;
        return NeedsImport;

        bool NeedsImport(CompletionItem item)
        {
            if (string.IsNullOrEmpty(item.InlineDescription)
                || !item.Tags.Any(tag => tag is "Class" or "Struct" or "Interface" or "Enum" or "Delegate")) return false;
            if (!namespaces.TryGetValue(item.InlineDescription, out var ns))
            {
                ns = model.Compilation.GlobalNamespace;
                foreach (var part in item.InlineDescription.Split('.'))
                {
                    ns = ns.GetNamespaceMembers().FirstOrDefault(candidate => candidate.Name == part);
                    if (ns is null) break;
                }
                namespaces[item.InlineDescription] = ns;
            }
            if (ns is null || ns.GetTypeMembers(item.DisplayText).Length == 0) return false;
            // Roslyn can return thousands of import candidates. Enumerate the visible
            // types once instead of repeating a semantic lookup for every candidate.
            // Keep the alias name, since an alias does not import its target's bare name.
            visibleTypes ??= model.LookupNamespacesAndTypes(position)
                .Select(symbol => (symbol.Name, Type: (symbol is IAliasSymbol alias ? alias.Target : symbol) as INamedTypeSymbol))
                .Where(entry => entry.Type is not null)
                .Select(entry => (entry.Name, entry.Type!.ContainingNamespace.ToDisplayString())).ToHashSet();
            return !visibleTypes.Contains((item.DisplayText, ns.ToDisplayString()));
        }
    }

    internal static IReadOnlyList<UiCompletionItem> Complete(DocumentAnalysis analysis, SemanticModel model,
        int generatedOffset, int offset, out bool incomplete)
    {
        incomplete = false;
        var source = analysis.Text;
        var start = offset;
        while (start > 0 && IsIdentifierPart(source[start - 1])) start--;
        var end = offset;
        while (end < source.Length && IsIdentifierPart(source[end])) end++;
        var prefix = source[start..offset].TrimStart('@');
        // Empty input should retain Roslyn's context-specific list, not list every type in
        // every reference. A non-empty prefix makes import discovery bounded and useful.
        if (prefix.Length == 0) return Array.Empty<UiCompletionItem>();
        var preceding = start - 1;
        while (preceding >= 0 && char.IsWhiteSpace(source[preceding])) preceding--;
        if (preceding >= 0 && source[preceding] is '.' or ':') return Array.Empty<UiCompletionItem>();

        var syntax = model.SyntaxTree.GetRoot();
        var token = syntax.FindToken(Math.Clamp(generatedOffset, 0, syntax.FullSpan.End));
        var trivia = syntax.FindTrivia(Math.Max(0, Math.Min(generatedOffset - 1, syntax.FullSpan.End - 1)));
        if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.DisabledTextTrivia)
            || token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.CharacterLiteralToken)
            || token.IsKind(SyntaxKind.InterpolatedStringTextToken)) return Array.Empty<UiCompletionItem>();
        var enclosing = model.GetEnclosingSymbol(generatedOffset);
        ISymbol within = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType ?? (ISymbol)model.Compilation.Assembly;
        var results = new List<UiCompletionItem>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var conflicts = new Dictionary<string, bool>(StringComparer.Ordinal);
        INamedTypeSymbol[]? boundTypes = null;
        foreach (var type in Indexes.GetValue(model.Compilation, static compilation => new TypeIndex(compilation)).Types)
        {
            if (!type.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!model.Compilation.IsSymbolAccessibleWithin(type, within)) continue;
            var ns = type.ContainingNamespace.ToDisplayString();
            if (analysis.IsNamespaceVisible(ns)) continue;
            var visible = model.LookupNamespacesAndTypes(generatedOffset, name: type.Name);
            if (visible.Any(symbol => SymbolEqualityComparer.Default.Equals(
                    (symbol is IAliasSymbol alias ? alias.Target : symbol)?.OriginalDefinition, type.OriginalDefinition))) continue;
            var qualified = visible.Length > 0 || ImportConflicts(type.ContainingNamespace);
            var fullName = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            if (!names.Add(fullName)) continue;
            var import = UiImportService.CreateImportEdit(analysis, ns);
            if (import is null) continue;
            results.Add(new UiCompletionItem(type.Name + (type.Arity > 0 ? "<>" : ""), Kind(type),
                Detail: ns + (qualified ? " · qualified name (import conflicts)" : " · add @using"), Documentation: "`" + fullName + "`",
                InsertText: qualified ? "global::" + ns + "." + EscapeIdentifier(type.Name) : EscapeIdentifier(type.Name),
                SortText: "2" + type.Name + "." + ns,
                Replacement: new TextSpan(start, end - start), FilterText: type.Name,
                AdditionalEdits: qualified ? null : new[] { import }));
            if (results.Count == 200) { incomplete = true; break; }
        }
        return results;

        bool ImportConflicts(INamespaceSymbol imported)
        {
            var ns = imported.ToDisplayString();
            if (conflicts.TryGetValue(ns, out var cached)) return cached;
            boundTypes ??= syntax.DescendantNodes().OfType<SimpleNameSyntax>()
                .Where(name => name.Parent is not QualifiedNameSyntax and not AliasQualifiedNameSyntax
                    && !(name.Parent is MemberAccessExpressionSyntax access && ReferenceEquals(access.Name, name))
                    && model.GetAliasInfo(name) is null)
                .Select(name => model.GetSymbolInfo(name).Symbol).OfType<INamedTypeSymbol>()
                .Where(bound => bound.ContainingType is null
                    && !analysis.EnclosingNamespaces.Contains(bound.ContainingNamespace.ToDisplayString(), StringComparer.Ordinal))
                .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToArray();
            var conflict = boundTypes.Any(bound => imported.GetTypeMembers(bound.Name, bound.Arity)
                .Any(other => model.Compilation.IsSymbolAccessibleWithin(other, within)
                    && !SymbolEqualityComparer.Default.Equals(other.OriginalDefinition, bound.OriginalDefinition)));
            conflicts[ns] = conflict;
            return conflict;
        }
    }

    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value is '_' or '@';
    private static string EscapeIdentifier(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
    private static UiCompletionKind Kind(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => UiCompletionKind.Interface,
        TypeKind.Struct => UiCompletionKind.Struct,
        TypeKind.Enum => UiCompletionKind.Enum,
        _ => UiCompletionKind.Class,
    };

    private sealed class TypeIndex
    {
        public IReadOnlyList<INamedTypeSymbol> Types { get; }
        public TypeIndex(Compilation compilation)
        {
            var types = new List<INamedTypeSymbol>();
            Visit(compilation.GlobalNamespace);
            Types = types.OrderBy(static type => type.Name, StringComparer.Ordinal)
                .ThenBy(static type => type.ContainingNamespace.ToDisplayString(), StringComparer.Ordinal).ToArray();
            void Visit(INamespaceSymbol ns)
            {
                foreach (var type in ns.GetTypeMembers())
                    if (!ns.IsGlobalNamespace && type.CanBeReferencedByName && !type.IsImplicitlyDeclared)
                        types.Add(type);
                foreach (var child in ns.GetNamespaceMembers()) Visit(child);
            }
        }
    }
}
