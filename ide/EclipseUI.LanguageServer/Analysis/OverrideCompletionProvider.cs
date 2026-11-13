using EclipseUI.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UiSpan = EclipseUI.Compiler.TextSpan;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// Keeps Roslyn's override generation without copying its formatting edits across a template
/// boundary. Roslyn 4.9 includes the following #line directives and generated-member indentation
/// in an override TextChange. Apply that change to a temporary document, then take only the new
/// member syntax and replace the user's unfinished declaration. Candidate selection, accessibility,
/// signatures, base calls and abstract implementations remain Roslyn's responsibility.
/// </summary>
internal static class OverrideCompletionProvider
{
    public static async Task<UiCompletionList?> CompleteAsync(
        DocumentAnalysis analysis, string uiPath, int offset, CancellationToken cancellationToken)
    {
        if (CursorScanner.Scan(analysis.Text, offset).Kind != CursorKind.CodeBlock) return null;
        var editing = EditingCSharpDocument.For(analysis, uiPath);
        if (editing is null || !editing.Mappings.TryMapPositionToGenerated(offset, out var generatedOffset))
            return null;
        var document = editing.Document;
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || !TryDeclaration(analysis, editing.Mappings, root, generatedOffset, out var replacement))
            return null;
        var original = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        // The enhanced #line spans used by production output are not understood by the
        // 4.9 override commit provider. In this temporary editing branch they carry no
        // semantics: mask their characters while preserving all offsets and line breaks.
        var masked = original.WithChanges(root.DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.IsKind(SyntaxKind.LineDirectiveTrivia)
                || trivia.IsKind(SyntaxKind.LineSpanDirectiveTrivia))
            .Select(trivia => new TextChange(trivia.Span,
                new string(original.ToString(trivia.Span).Select(character =>
                    character is '\r' or '\n' ? character : ' ').ToArray()))));
        var block = analysis.Ast!.Nodes.OfType<UiCodeBlock>().Single(block =>
            replacement.Start >= block.CodeSpan.Start
            && replacement.Start + replacement.Length <= block.CodeSpan.Start + block.CodeSpan.Length);
        if (!editing.Mappings.TryMapPositionToGenerated(block.CodeSpan.Start + block.CodeSpan.Length,
                out var blockEnd)) return null;
        // End an unfinished declaration before the following generated member, so parser
        // recovery cannot absorb that member's modifiers or return type into the user's.
        masked = masked.WithChanges(new TextChange(new Microsoft.CodeAnalysis.Text.TextSpan(blockEnd, 0), ";\n"));
        document = document.WithText(masked);
        root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null) return null;
        var service = CompletionService.GetService(document);
        if (service is null) return null;
        var completions = await service.GetCompletionsAsync(document, generatedOffset,
            CompletionTrigger.Invoke, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (completions is null) return new UiCompletionList(replacement, Array.Empty<UiCompletionItem>());
        // A parameter/type/member-access completion inside an existing override remains an
        // ordinary C# completion. Do not replace its declaration with an override template.
        if (!completions.ItemsList.Any(IsOverrideItem)) return null;

        var originalText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<UiCompletionItem>();
        foreach (var item in completions.ItemsList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOverrideItem(item)) continue;
            var converted = await ConvertAsync(analysis, document, service, originalText,
                root, item, editing.Mappings, replacement, cancellationToken).ConfigureAwait(false);
            if (converted is not null) items.Add(converted);
        }
        return new UiCompletionList(replacement, items);
    }

    private static bool TryDeclaration(DocumentAnalysis analysis, DocumentMappingService mappings,
        SyntaxNode root, int offset, out UiSpan replacement)
    {
        replacement = default;
        if (offset <= 0 || offset > root.FullSpan.End) return false;
        var token = root.FindToken(offset - 1);
        var declaration = token.Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(IsOverrideMember);
        if (declaration is null
            || !declaration.ChildTokens().Any(t => t.IsKind(SyntaxKind.OverrideKeyword))
            || declaration.SpanStart > offset) return false;
        var bodyStart = declaration switch
        {
            BaseMethodDeclarationSyntax method => method.Body?.OpenBraceToken.SpanStart
                ?? method.ExpressionBody?.ArrowToken.SpanStart,
            PropertyDeclarationSyntax property => property.AccessorList?.OpenBraceToken.SpanStart
                ?? property.ExpressionBody?.ArrowToken.SpanStart,
            IndexerDeclarationSyntax indexer => indexer.AccessorList?.OpenBraceToken.SpanStart
                ?? indexer.ExpressionBody?.ArrowToken.SpanStart,
            EventDeclarationSyntax @event => @event.AccessorList?.OpenBraceToken.SpanStart,
            _ => null,
        };
        if (bodyStart is { } start && offset > start) return false;

        var end = offset;
        // Include a partially typed identifier to the right of the caret, but never the next
        // generated member which recovery may have merged into this incomplete declaration.
        var atCaret = root.FindToken(Math.Min(offset, root.FullSpan.End - 1));
        if (atCaret.SpanStart <= offset && atCaret.Span.End > offset
            && mappings.TryMapToOriginal(new UiSpan(atCaret.SpanStart, atCaret.Span.Length), out _))
            end = atCaret.Span.End;
        if (!mappings.TryMapToOriginal(new UiSpan(declaration.SpanStart,
                end - declaration.SpanStart), out replacement)) return false;
        var span = replacement;
        return analysis.Ast?.Nodes.OfType<UiCodeBlock>().Any(block =>
            span.Start >= block.CodeSpan.Start
            && span.Start + span.Length <= block.CodeSpan.Start + block.CodeSpan.Length) == true;
    }

    private static bool IsOverrideMember(MemberDeclarationSyntax declaration) => declaration is
        MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax
        or EventDeclarationSyntax or EventFieldDeclarationSyntax or FieldDeclarationSyntax or IncompleteMemberSyntax;

    // Public CompletionItem.Properties carries the locked 4.9 override provider's context.
    // IsComplexTextEdit alone also includes property snippets and imported types.
    private static bool IsOverrideItem(CompletionItem item) => item.IsComplexTextEdit
        && item.Properties.TryGetValue("Modifiers", out var modifiers)
        && modifiers.Split(',').Any(modifier => modifier.Trim() == "Override")
        && item.Properties.ContainsKey("Symbols");

    private static async Task<UiCompletionItem?> ConvertAsync(DocumentAnalysis analysis,
        Document document, CompletionService service, SourceText originalText, SyntaxNode originalRoot,
        CompletionItem item, DocumentMappingService mappings, UiSpan replacement, CancellationToken cancellationToken)
    {
        var change = await service.GetChangeAsync(document, item, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (change.NewPosition is not { } newPosition) return null;
        var changedText = originalText.WithChanges(change.TextChanges);
        if (newPosition < 0 || newPosition >= changedText.Length) return null;
        var changedDocument = document.WithText(changedText);
        var changedRoot = await changedDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var member = changedRoot?.FindToken(newPosition).Parent?.AncestorsAndSelf()
            .OfType<MemberDeclarationSyntax>().FirstOrDefault(IsOverrideMember);
        if (member is null || !member.ChildTokens().Any(t => t.IsKind(SyntaxKind.OverrideKeyword))) return null;
        var model = await changedDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var symbol = model?.GetDeclaredSymbol(member, cancellationToken);
        if (symbol is null || !symbol.IsOverride || symbol.IsStatic || symbol switch
            {
                IMethodSymbol method => method.OverriddenMethod is null,
                IPropertySymbol property => property.OverriddenProperty is null,
                IEventSymbol @event => @event.OverriddenEvent is null,
                _ => true,
            }) return null;
        // These are compiler-owned entry points, not author lifecycle hooks. The generator
        // rejects every author declaration with either name, including overloads.
        if (symbol.Name is "InitializeUi" or "Render") return null;

        var imports = ImportEdits(analysis, originalRoot, changedRoot!);
        if (imports is null) return null;
        // Recovery can attach our synthetic boundary semicolon to a completed method or
        // accessor block. It belongs to the editing branch, never to the inserted member.
        var end = member switch
        {
            BaseMethodDeclarationSyntax { Body: { } body } => body.Span.End,
            PropertyDeclarationSyntax { AccessorList: { } accessors, Initializer: null } => accessors.Span.End,
            IndexerDeclarationSyntax { AccessorList: { } accessors } => accessors.Span.End,
            EventDeclarationSyntax { AccessorList: { } accessors } => accessors.Span.End,
            _ => member.Span.End,
        };
        var text = changedText.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(member.SpanStart, end));
        var caret = newPosition - member.SpanStart;
        if (caret < 0 || caret > text.Length) return null;
        var newline = analysis.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var newIndent = changedText.ToString(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
            changedText.Lines.GetLineFromPosition(member.SpanStart).Start, member.SpanStart));
        var originalLine = analysis.Text.LastIndexOf('\n', Math.Max(0, replacement.Start - 1)) + 1;
        var originalIndent = analysis.Text.Substring(originalLine, replacement.Start - originalLine);
        string Normalize(string value)
        {
            value = value.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (newIndent.All(char.IsWhiteSpace) && originalIndent.All(char.IsWhiteSpace))
                value = value.Replace("\n" + newIndent, "\n" + originalIndent, StringComparison.Ordinal);
            return newline == "\n" ? value : value.Replace("\n", newline, StringComparison.Ordinal);
        }
        caret = Normalize(text[..caret]).Length;
        text = Normalize(text);
        var filterPrefix = "";
        if (mappings.TryMapToOriginal(new UiSpan(item.Span.Start, 0), out var filterStart)
            && filterStart.Start >= replacement.Start)
            filterPrefix = analysis.Text.Substring(replacement.Start, filterStart.Start - replacement.Start);
        return new UiCompletionItem(item.DisplayText,
            symbol is IPropertySymbol ? UiCompletionKind.Property
                : symbol is IEventSymbol ? UiCompletionKind.Event : UiCompletionKind.Method,
            Detail: symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            InsertText: text,
            Replacement: replacement,
            FilterText: filterPrefix + item.FilterText,
            SortText: item.SortText,
            AdditionalEdits: imports.Count == 0 ? null : imports,
            CaretOffset: caret)
        {
            DocumentationResolver = token => EmbeddedCSharpCompletionProvider.GetDescriptionAsync(
                service, document, item, token),
        };
    }

    private static IReadOnlyList<UiTextEdit>? ImportEdits(
        DocumentAnalysis analysis, SyntaxNode before, SyntaxNode after)
    {
        if (before is not CompilationUnitSyntax oldRoot || after is not CompilationUnitSyntax newRoot)
            return null;
        var existing = oldRoot.Usings.Select(u => u.WithoutTrivia().ToString()).ToHashSet(StringComparer.Ordinal);
        var edits = new List<UiTextEdit>();
        foreach (var directive in newRoot.Usings)
        {
            if (existing.Contains(directive.WithoutTrivia().ToString())) continue;
            if (directive.Alias is not null || directive.StaticKeyword != default
                || directive.GlobalKeyword != default || directive.Name is null) return null;
            if (UiImportService.CreateImportEdit(analysis, directive.Name.ToString()) is { } edit) edits.Add(edit);
        }
        return edits.GroupBy(edit => edit.Span)
            .Select(group => new UiTextEdit(group.Key, string.Concat(group.Select(edit => edit.NewText))))
            .ToArray();
    }
}
