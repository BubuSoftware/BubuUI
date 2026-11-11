using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using UiSpan = EclipseUI.Compiler.TextSpan;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// design/18 阶段 2/3：C# 岛（<c>@code</c> 与 <c>{expr}</c>）的补全——直接调 Roslyn
/// <see cref="CompletionService"/>，跑在 <see cref="EditorDocumentWorkspace"/> 里的真实
/// 生成文档上（与 .cs 同一台机器）。局部变量/关键字/snippet/扩展方法/await 上下文这些
/// 能力全部来自 Roslyn，不手写枚举。
///
/// 每个候选的编辑区间（<c>item.Span</c> 或 GetChangeAsync 的 TextChange）经统一映射服务
/// 回映到 .ui；**映不回就丢弃该项**（§8 硬边界）。模板表面（标签/属性/指令）仍归目录，
/// 这里只服务 C# 岛。legacy（无项目快照/无生成文档）返回空——不另造合成投影补全；
/// <c>{expr}</c> 的 legacy 探针补全由 <see cref="CompletionProvider"/> 保留。
/// </summary>
public static class EmbeddedCSharpCompletionProvider
{
    public static async Task<UiCompletionList> CompleteAsync(
        DocumentAnalysis analysis, string uiPath, int offset,
        char? triggerCharacter, CancellationToken cancellationToken)
    {
        var generated = analysis.GeneratedDocument;
        var project = analysis.Project;
        if (generated is null || project is null) return UiCompletionList.Empty;
        if (!generated.Mappings.TryMapPositionToGenerated(offset, out var generatedOffset))
            return UiCompletionList.Empty;

        var document = EditorDocumentWorkspace.For(project).GeneratedDocumentFor(uiPath);
        if (document is null) return UiCompletionList.Empty;
        var service = CompletionService.GetService(document);
        if (service is null) return UiCompletionList.Empty;

        var trigger = triggerCharacter is { } character
            ? CompletionTrigger.CreateInsertionTrigger(character)
            : CompletionTrigger.Invoke;
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (!service.ShouldTriggerCompletion(text, generatedOffset, trigger))
            return UiCompletionList.Empty;

        var completions = await service.GetCompletionsAsync(
            document, generatedOffset, trigger, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (completions is null || completions.ItemsList.Count == 0) return UiCompletionList.Empty;

        var replacement = generated.Mappings.TryMapToOriginal(
            new UiSpan(completions.Span.Start, completions.Span.Length), out var mappedDefault)
            ? mappedDefault
            : new UiSpan(offset, 0);

        var items = new List<UiCompletionItem>();
        foreach (var item in completions.ItemsList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ConvertAsync(service, document, completions, item, generated.Mappings, cancellationToken)
                .ConfigureAwait(false) is { } converted)
                items.Add(converted);
        }
        // 建议模式（SuggestionModeItem）= 列表随输入细化，对应 LSP isIncomplete。
        return new UiCompletionList(replacement, items, completions.SuggestionModeItem is not null);
    }

    private static async Task<UiCompletionItem?> ConvertAsync(
        CompletionService service, Document document, CompletionList list, CompletionItem item,
        DocumentMappingService mappings, CancellationToken cancellationToken)
    {
        var insertText = item.DisplayText;
        var editSpan = item.Span;
        var isSnippet = item.Tags.Contains("Snippet");

        // 复杂编辑（snippet 展开、override 生成、自动补 using 等）的真实文本要问 GetChangeAsync。
        // 只接受「本文档单处编辑」：多处/跨文档编辑（如补 using 落在生成独有代码区）无法忠实
        // 回映到 .ui，整项丢弃——不猜。
        if (item.IsComplexTextEdit || isSnippet)
        {
            var change = await service.GetChangeAsync(document, item, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (change.TextChanges.Length != 1) return null;
            insertText = change.TextChanges[0].NewText ?? "";
            editSpan = change.TextChanges[0].Span;
        }

        if (!mappings.TryMapToOriginal(new UiSpan(editSpan.Start, editSpan.Length), out var uiSpan))
            return null;

        var description = await service.GetDescriptionAsync(document, item, cancellationToken)
            .ConfigureAwait(false);
        return new UiCompletionItem(
            item.DisplayText,
            KindFromTags(item.Tags),
            Detail: item.InlineDescription is { Length: > 0 } inline ? inline : null,
            Documentation: description?.Text is { Length: > 0 } doc ? doc : null,
            InsertText: insertText,
            IsSnippet: isSnippet,
            SortText: item.SortText,
            Replacement: uiSpan,
            FilterText: item.FilterText,
            CommitCharacters: CommitCharacters(list.Rules.DefaultCommitCharacters, item.Rules.CommitCharacterRules),
            Tags: item.Tags.IsDefaultOrEmpty ? null : item.Tags,
            Preselect: item.Rules.MatchPriority == MatchPriority.Preselect);
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