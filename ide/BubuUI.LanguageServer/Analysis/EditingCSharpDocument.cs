using System.Runtime.CompilerServices;
using BubuUI.Compiler;
using BubuUI.Compiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UiSpan = BubuUI.Compiler.TextSpan;

namespace BubuUI.LanguageServer.Analysis;

/// <summary>
/// Completion's C# document and exact mappings. Production output is preferred. When an
/// unrelated template error prevents output, the shared catalog symbol view preserves the
/// root inheritance and verbatim @code members without constructing template structure.
/// This immutable editing branch never changes the project's compilation or diagnostics.
/// </summary>
public sealed record EditingCSharpDocument(Document Document, DocumentMappingService Mappings)
{
    private sealed record CacheEntry(string Path, EditingCSharpDocument? Value);
    private static readonly ConditionalWeakTable<DocumentAnalysis, CacheEntry> Cache = new();

    public static EditingCSharpDocument? For(DocumentAnalysis analysis, string uiPath)
    {
        if (analysis.Project is not { } project || string.IsNullOrEmpty(uiPath)) return null;
        if (analysis.GeneratedDocument is { } generated)
        {
            var document = EditorDocumentWorkspace.For(project).GeneratedDocumentFor(uiPath);
            return document is null ? null : new EditingCSharpDocument(document, generated.Mappings);
        }

        var cached = Cache.GetValue(analysis, _ => new CacheEntry(uiPath, CreateEditingView(analysis, uiPath)));
        return string.Equals(cached.Path, uiPath, StringComparison.OrdinalIgnoreCase)
            ? cached.Value
            : CreateEditingView(analysis, uiPath);
    }

    private static EditingCSharpDocument? CreateEditingView(DocumentAnalysis analysis, string uiPath)
    {
        if (analysis.Project is not { } project || analysis.Ast is null) return null;
        var declaration = UiTemplateScanner.FromFile(uiPath, analysis.Text);
        if (declaration is null || string.IsNullOrEmpty(declaration.RootTag)) return null;
        var namespaceName = declaration.Namespace ?? project.Evaluation.RootNamespace;

        // An absent namespace may be a transient edit. The project namespace is only an
        // editing context; the real generator continues to report the missing directive.
        declaration = declaration with
        {
            Namespace = namespaceName,
            CodeBlocks = analysis.Ast.Nodes.OfType<UiCodeBlock>().Select(block =>
            {
                var (line, character) = analysis.Positions.ToPosition(block.CodeSpan.Start);
                return new UiCodeBlockText(block.Code, block.CodeSpan.Start, block.CodeSpan.Length, line, character);
            }).ToArray(),
        };
        if (declaration.CodeBlocks.Count == 0) return null;

        var original = project.Compilation;
        var editing = UiCatalogFactory.WithTemplateTypes(original, new[] { declaration });
        var tree = editing.SyntaxTrees.FirstOrDefault(candidate =>
            string.Equals(candidate.FilePath, uiPath + ".catalog.cs", StringComparison.OrdinalIgnoreCase)
            && !original.SyntaxTrees.Contains(candidate));
        if (tree is null) return null;

        var viewName = declaration.Tag + (declaration.TemplateTarget is null ? "" : "__TemplateView");
        var typeName = namespaceName.Length == 0
            ? viewName : namespaceName + "." + viewName;
        // Do not invent a root for an unresolved/invalid root tag: completion must retain
        // the same CLR inheritance rules as the compiler, even while other markup is bad.
        var type = editing.GetTypeByMetadataName(typeName);
        if (type?.BaseType is not { TypeKind: not TypeKind.Error } root
            || root.IsSealed || root.IsStatic) return null;
        var uiObject = editing.GetTypeByMetadataName("BubuUI.UiObject");
        var validRoot = false;
        for (INamedTypeSymbol? current = root; current is not null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, uiObject))
            {
                validRoot = true;
                break;
            }
        if (!validRoot) return null;

        var generatedText = tree.GetText().ToString();
        var directives = tree.GetRoot().DescendantTrivia(descendIntoTrivia: true)
            .Select(trivia => trivia.GetStructure()).OfType<LineDirectiveTriviaSyntax>()
            .OrderBy(directive => directive.SpanStart).ToArray();
        var mappings = new List<SourceMapping>();
        var afterPreviousBlock = 0;
        foreach (var block in declaration.CodeBlocks)
        {
            // WithTemplateTypes emits a #line immediately before every verbatim block.
            // Locate that explicit boundary, then require an exact full-body match rather
            // than searching for a possibly repeated member text at an arbitrary offset.
            var marker = directives.FirstOrDefault(directive =>
                directive.SpanStart >= afterPreviousBlock
                && directive.Line.Value is int line && line == block.StartLine + 1
                && directive.File.ValueText == declaration.Path);
            if (marker is null) return null;
            var bodyStart = marker.FullSpan.End;
            if (bodyStart + block.Code.Length > generatedText.Length
                || !generatedText.AsSpan(bodyStart, block.Code.Length).SequenceEqual(block.Code.AsSpan()))
                return null;
            mappings.Add(new SourceMapping(
                new UiSpan(block.SpanStart, block.SpanLength),
                new UiSpan(bodyStart, block.Code.Length), Verbatim: true));
            afterPreviousBlock = bodyStart + block.Code.Length;
        }

        var editorProject = EditorDocumentWorkspace.For(project).Solution.Projects.Single();
        var document = editorProject.AddDocument(Path.GetFileName(tree.FilePath), tree.GetText(), filePath: tree.FilePath);
        return new EditingCSharpDocument(document, DocumentMappingService.Create(mappings));
    }
}
