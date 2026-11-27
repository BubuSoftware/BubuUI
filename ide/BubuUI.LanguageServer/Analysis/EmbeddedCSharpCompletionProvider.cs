using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using UiSpan = BubuUI.Compiler.TextSpan;

namespace BubuUI.LanguageServer.Analysis;

/// <summary>
/// design/18 阶段 2/3：C# 岛（<c>@code</c> 与 <c>{expr}</c>）的补全——直接调 Roslyn
/// <see cref="CompletionService"/>，跑在 <see cref="EditorDocumentWorkspace"/> 里的真实
/// 生成文档上（与 .cs 同一台机器）。局部变量/关键字/snippet/扩展方法/await 上下文这些
/// 能力全部来自 Roslyn，不手写枚举。
///
/// 每个候选的编辑区间（<c>item.Span</c> 或 GetChangeAsync 的 TextChange）经统一映射服务
/// 回映到 .bui；**映不回就丢弃该项**（§8 硬边界）。模板表面（标签/属性/指令）仍归目录，
/// 这里只服务 C# 岛。项目模式通过 EditingCSharpDocument 保留错误模板的有效 @code；
/// legacy（无项目快照）返回空；
/// <c>{expr}</c> 的 legacy 探针补全由 <see cref="CompletionProvider"/> 保留。
/// </summary>
public static class EmbeddedCSharpCompletionProvider
{
    public static async Task<UiCompletionList> CompleteAsync(
        DocumentAnalysis analysis, string uiPath, int offset,
        char? triggerCharacter, CancellationToken cancellationToken)
    {
        if (await OverrideCompletionProvider.CompleteAsync(analysis, uiPath, offset, cancellationToken)
                .ConfigureAwait(false) is { } overrides) return overrides;
        var editing = EditingCSharpDocument.For(analysis, uiPath);
        if (editing is null) return UiCompletionList.Empty;
        if (!editing.Mappings.TryMapPositionToGenerated(offset, out var generatedOffset))
            return UiCompletionList.Empty;

        var document = editing.Document;
        var service = CompletionService.GetService(document);
        if (service is null) return UiCompletionList.Empty;

        // Roslyn's editor filters an existing list while a word is being typed, so
        // insertion triggers inside identifiers can return false. An LSP client may
        // request a fresh list there: honour it as an invocation, without requiring this.
        var trigger = triggerCharacter is { } character && !char.IsLetterOrDigit(character) && character != '_'
            ? CompletionTrigger.CreateInsertionTrigger(character)
            : CompletionTrigger.Invoke;
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (!service.ShouldTriggerCompletion(text, generatedOffset, trigger))
            return UiCompletionList.Empty;

        var completions = await service.GetCompletionsAsync(
            document, generatedOffset, trigger, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (completions is null || completions.ItemsList.Count == 0) return UiCompletionList.Empty;

        var replacement = editing.Mappings.TryMapToOriginal(
            new UiSpan(completions.Span.Start, completions.Span.Length), out var mappedDefault)
            ? mappedDefault
            : new UiSpan(offset, 0);

        var items = new List<UiCompletionItem>();
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var scopeRanks = model is null ? null : ScopeMemberRanks(model, generatedOffset, text, completions.Span.Start);
        var needsImport = model is null ? null : UnimportedTypeCompletionProvider.CreateImportFilter(model, generatedOffset);
        foreach (var item in completions.ItemsList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Roslyn can return import candidates whose commit only inserts the type's
            // name: its native editor adds the using separately. Deliver those through
            // our explicit template-import path so no selectable candidate misses it.
            if (needsImport?.Invoke(item) == true) continue;
            if (await ConvertAsync(service, document, completions, item, editing.Mappings, cancellationToken)
                .ConfigureAwait(false) is { } converted)
            {
                if (scopeRanks is not null)
                {
                    var rank = item.Tags.Any(tag => tag is "Field" or "Property" or "Method" or "Event"
                        or "Constant" or "Local" or "Parameter" or "RangeVariable")
                        && scopeRanks.TryGetValue(item.DisplayText, out var memberRank) ? memberRank : 2;
                    converted = converted with { SortText = rank + (converted.SortText ?? converted.Label) };
                }
                items.Add(converted);
            }
        }
        var incomplete = false;
        if (completions.ItemsList.Any(item => item.Tags.Any(tag => tag is "Class" or "Struct" or "Interface" or "Namespace")))
        {
            if (model is not null)
                items.AddRange(UnimportedTypeCompletionProvider.Complete(analysis, model, generatedOffset, offset, out incomplete));
        }
        // Suggestion mode only changes selection/commit behaviour. It does not mean
        // the list is truncated; marking it incomplete forces an LSP round trip on
        // every typed character instead of letting VS Code filter locally.
        return new UiCompletionList(replacement, items, incomplete);
    }

    private static Dictionary<string, int>? ScopeMemberRanks(
        SemanticModel model, int position, Microsoft.CodeAnalysis.Text.SourceText text, int replacementStart)
    {
        var previous = replacementStart - 1;
        while (previous >= 0 && char.IsWhiteSpace(text[previous])) previous--;
        if (previous >= 0 && text[previous] is '.' or ':') return null;
        var enclosing = model.GetEnclosingSymbol(position);
        var currentType = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;
        if (currentType is null) return null;
        var declaringTypes = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        for (var type = currentType; type is not null; type = type.BaseType)
            declaringTypes[type.OriginalDefinition] = SymbolEqualityComparer.Default.Equals(type, currentType) ? 0 : 1;
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var symbol in model.LookupSymbols(position))
        {
            int rank;
            if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol) rank = 0;
            else if (symbol is IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol
                && symbol.ContainingType is { } declaringType)
            {
                if (!declaringTypes.TryGetValue(declaringType.OriginalDefinition, out rank)) continue;
            }
            else continue;
            if (!ranks.TryGetValue(symbol.Name, out var existing) || rank < existing) ranks[symbol.Name] = rank;
        }
        return ranks;
    }

    private static async Task<UiCompletionItem?> ConvertAsync(
        CompletionService service, Document document, CompletionList list, CompletionItem item,
        DocumentMappingService mappings, CancellationToken cancellationToken)
    {
        var insertText = item.DisplayText;
        var editSpan = item.Span;
        int? caretOffset = null;
        var additional = new List<UiTextEdit>();

        // 复杂编辑（snippet 展开、override 生成、自动补 using 等）的真实文本要问 GetChangeAsync。
        // Every edit must map exactly. Namespace imports are explicit template edits;
        // override formatting that touches generated trivia is handled by its provider.
        if (item.IsComplexTextEdit || item.Tags.Contains("Snippet"))
        {
            var change = await service.GetChangeAsync(document, item, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (change.TextChanges.Length == 0) return null;
            var candidates = change.TextChanges.Where(edit => edit.Span.Contains(item.Span)
                || edit.Span.IntersectsWith(item.Span)).ToArray();
            if (candidates.Length != 1) return null;
            var primary = candidates[0];
            insertText = primary.NewText ?? "";
            editSpan = primary.Span;
            foreach (var extra in change.TextChanges.Where(edit => edit != primary))
            {
                if (!mappings.TryMapToOriginal(new UiSpan(extra.Span.Start, extra.Span.Length), out var span)) return null;
                additional.Add(new UiTextEdit(span, extra.NewText ?? ""));
            }
            if (change.NewPosition is { } position)
            {
                var deltaBefore = change.TextChanges.Where(edit => edit.Span.Start < primary.Span.Start)
                    .Sum(edit => (edit.NewText?.Length ?? 0) - edit.Span.Length);
                var relative = position - primary.Span.Start - deltaBefore;
                if (relative >= 0 && relative <= insertText.Length) caretOffset = relative;
            }
        }

        if (!mappings.TryMapToOriginal(new UiSpan(editSpan.Start, editSpan.Length), out var uiSpan))
            return null;

        return new UiCompletionItem(
            item.DisplayText,
            KindFromTags(item.Tags),
            Detail: item.InlineDescription is { Length: > 0 } inline ? inline : null,
            InsertText: insertText,
            SortText: item.SortText,
            Replacement: uiSpan,
            FilterText: item.FilterText,
            CommitCharacters: CommitCharacters(list.Rules.DefaultCommitCharacters, item.Rules.CommitCharacterRules),
            Tags: item.Tags.IsDefaultOrEmpty ? null : item.Tags,
            Preselect: item.Rules.MatchPriority == MatchPriority.Preselect,
            AdditionalEdits: additional.Count == 0 ? null : additional,
            CaretOffset: caretOffset)
        {
            DocumentationResolver = token => GetDescriptionAsync(service, document, item, token),
        };
    }

    internal static async Task<string?> GetDescriptionAsync(
        CompletionService service, Document document, CompletionItem item, CancellationToken cancellationToken)
    {
        var description = await service.GetDescriptionAsync(document, item, cancellationToken).ConfigureAwait(false);
        return description?.Text is { Length: > 0 } text ? text : null;
    }

    /// <summary>Roslyn 标签 → LSP 类别。按固定优先级取首个命中（符号项通常只有一个类别标签）。</summary>
    private static readonly (string Tag, UiCompletionKind Kind)[] TagKinds =
    {
        ("Class", UiCompletionKind.Class),
        ("Delegate", UiCompletionKind.Class),
        ("Interface", UiCompletionKind.Interface),
        ("Struct", UiCompletionKind.Struct),
        ("Enum", UiCompletionKind.Enum),
        ("EnumMember", UiCompletionKind.EnumMember),
        ("Method", UiCompletionKind.Method),
        ("ExtensionMethod", UiCompletionKind.Method),
        ("Property", UiCompletionKind.Property),
        ("Field", UiCompletionKind.Field),
        ("Constant", UiCompletionKind.Constant),
        ("Event", UiCompletionKind.Event),
        ("Local", UiCompletionKind.Variable),
        ("Parameter", UiCompletionKind.Variable),
        ("RangeVariable", UiCompletionKind.Variable),
        ("Namespace", UiCompletionKind.Namespace),
        ("Module", UiCompletionKind.Namespace),
        ("TypeParameter", UiCompletionKind.TypeParameter),
        ("Operator", UiCompletionKind.Operator),
        ("Keyword", UiCompletionKind.Keyword),
        ("Snippet", UiCompletionKind.Snippet),
    };

    private static UiCompletionKind KindFromTags(ImmutableArray<string> tags)
    {
        if (tags.IsDefaultOrEmpty) return UiCompletionKind.Text;
        foreach (var (tag, kind) in TagKinds)
            if (tags.Contains(tag)) return kind;
        return UiCompletionKind.Text;
    }

    /// <summary>
    /// 生效 commit 字符集 = 语言默认集 ± 项级修改规则。无修改规则返回 null（与默认一致，
    /// 宿主省略该字段，客户端按默认行为处理）。
    /// </summary>
    private static IReadOnlyList<string>? CommitCharacters(
        ImmutableArray<char> defaults, ImmutableArray<CharacterSetModificationRule> rules)
    {
        if (rules.IsDefaultOrEmpty) return null;
        var set = new HashSet<char>(defaults);
        foreach (var rule in rules)
            switch (rule.Kind)
            {
                case CharacterSetModificationKind.Add:
                    foreach (var c in rule.Characters) set.Add(c);
                    break;
                case CharacterSetModificationKind.Remove:
                    foreach (var c in rule.Characters) set.Remove(c);
                    break;
                case CharacterSetModificationKind.Replace:
                    set = new HashSet<char>(rule.Characters);
                    break;
            }
        return set.OrderBy(static c => c).Select(static c => c.ToString()).ToArray();
    }
}
