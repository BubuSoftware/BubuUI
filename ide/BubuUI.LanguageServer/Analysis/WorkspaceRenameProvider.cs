using BubuUI.Compiler;
using BubuUI.LanguageServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using UiSpan = BubuUI.Compiler.TextSpan;

namespace BubuUI.LanguageServer.Analysis;

/// <summary>
/// design/18 阶段 3（§5.6）：项目模式的重命名走 Roslyn <see cref="Renamer"/>——
/// 在 <see cref="EditorDocumentWorkspace"/> 的 Solution 上改符号，diff 新旧解：
/// 源 .cs 文档的编辑直接交付；生成文档的编辑经统一映射服务回映到 .bui，
/// **任何一处映不回，整个重命名拒绝**（§8 硬边界，不猜）。
///
/// 相比阶段 1 的手写引用扫描，Renamer 自然覆盖 @code 里声明的成员（声明与引用都在
/// 生成文档里，逐字映射回 @code）与跨文档引用（含其他 .bui 的生成文档）。同名冲突两条
/// 防线：不可解冲突 RenameSymbolAsync 抛 ArgumentException → 拒绝；可解冲突（如同名
/// 成员）4.9.2 不抛异常，交付前比对编译错误数，引入新错误 → 拒绝。
///
/// 范围闸门：只接受**本项目源码声明**的成员/局部变量/参数——元数据符号改不了，
/// 跨项目符号改不全，类型/命名空间不改（类名来自文件名契约，spec/02 §2.2）。
/// @foreach 迭代变量不经过这里（生成代码里是无映射的 lambda 参数，由
/// <see cref="RenameProvider.IsForeachVariable"/> 预路由回 legacy 文档内路径）。
/// </summary>
public static class WorkspaceRenameProvider
{
    public static async Task<UiPrepareRename?> PrepareAsync(
        DocumentAnalysis analysis, string uiPath, int offset, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(analysis, uiPath, offset, cancellationToken).ConfigureAwait(false)
            is not { } resolved) return null;
        var span = resolved.UiSpan;
        return new UiPrepareRename(span, analysis.Text.Substring(span.Start, span.Length));
    }

    public static async Task<UiRenameResult?> RenameAsync(
        DocumentAnalysis analysis, ProjectWorkspace projects, string uiPath, int offset,
        string newName, CancellationToken cancellationToken)
    {
        if (!RenameProvider.IsIdentifier(newName)) return null;
        if (await ResolveAsync(analysis, uiPath, offset, cancellationToken).ConfigureAwait(false)
            is not { } resolved) return null;

        var project = analysis.Project!;
        var editor = EditorDocumentWorkspace.For(project);
        var solution = resolved.Document.Project.Solution;

        Solution newSolution;
        try
        {
            // 默认选项全 false（不重载/不字符串/不注释/不改文件名）——保守口径。
            // OptionSet 重载在 4.9.2 已弃用，用 SymbolRenameOptions 重载。
            newSolution = await Renamer.RenameSymbolAsync(
                solution, resolved.Symbol, new SymbolRenameOptions(), newName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return null; // 不可解冲突（RenameSymbolAsync 抛 ArgumentException）：整体拒绝
        }

        // 4.9.2 的 Renamer 对**可解**冲突（如同名成员/局部变量）不抛异常，直接产出带重复
        // 声明的解（实测 task→items 生成两个 items 字段仍 IsSuccessful）。交付前比对编译
        // 错误数：rename 引入新错误 = 冲突 → 整体拒绝（§8「改坏就拒绝」；旧解的既有错误
        // 两边相同会抵消，只有新增才触发）。重命名是低频用户操作，两次编译可接受。
        var projectId = resolved.Document.Project.Id;
        var oldCompilation = await solution.GetProject(projectId)!.GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false);
        var newCompilation = await newSolution.GetProject(projectId)!.GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (oldCompilation is null || newCompilation is null) return null;
        if (newCompilation.GetDiagnostics(cancellationToken).Count(static d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            > oldCompilation.GetDiagnostics(cancellationToken).Count(static d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            return null;

        var currentUiPath = Path.GetFullPath(uiPath);
        var uiEdits = new Dictionary<string, List<UiTextEdit>>(StringComparer.OrdinalIgnoreCase);
        var uiSeen = new Dictionary<string, HashSet<(int Start, int Length, string Text)>>(StringComparer.OrdinalIgnoreCase);
        var files = new List<UiRenameFile>();

        foreach (var projectChanges in newSolution.GetChanges(solution).GetProjectChanges())
        {
            // 重命名不应增删文档；出现即超出本服务的交付形态，拒绝。
            if (projectChanges.GetAddedDocuments().Any() || projectChanges.GetRemovedDocuments().Any())
                return null;

            foreach (var id in projectChanges.GetChangedDocuments())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var oldDocument = solution.GetDocument(id)!;
                var newDocument = newSolution.GetDocument(id)!;
                // Document 级 diff：SourceText.GetTextChanges 对多处小改可能退化成整篇替换
                // （[0,旧长] 单条），此 API 在该情形自动回落语法树 diff 取细粒度编辑。
                // TextChange.Span 是**旧文本**坐标（生成坐标），正好喂映射服务。
                var changes = await newDocument.GetTextChangesAsync(oldDocument, cancellationToken)
                    .ConfigureAwait(false);

                if (editor.UiPathFor(id) is { } mappedUiPath)
                {
                    var mappings = project.GeneratedDocuments[mappedUiPath].Mappings;
                    if (!uiEdits.TryGetValue(mappedUiPath, out var edits))
                    {
                        uiEdits[mappedUiPath] = edits = new List<UiTextEdit>();
                        uiSeen[mappedUiPath] = new HashSet<(int, int, string)>();
                    }
                    foreach (var change in changes)
                    {
                        if (!mappings.TryMapToOriginal(
                                new UiSpan(change.Span.Start, change.Span.Length), out var span))
                            return null;
                        var text = change.NewText ?? "";
                        // @bind 的 readSource/writeSource 双出现映射到同一原文区间：去重。
                        if (uiSeen[mappedUiPath].Add((span.Start, span.Length, text)))
                            edits.Add(new UiTextEdit(span, text));
                    }
                }
                else if (editor.SourcePathFor(id) is { } sourcePath)
                {
                    var oldText = await oldDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    var edits = changes
                        .Select(static c => new UiTextEdit(
                            new TextSpan(c.Span.Start, c.Span.Length), c.NewText ?? ""))
                        .ToList();
                    if (edits.Count > 0) files.Add(new UiRenameFile(sourcePath, oldText.ToString(), edits));
                }
                else
                {
                    return null; // 既不是生成文档也不是源 .cs：不猜
                }
            }
        }

        foreach (var (path, edits) in uiEdits)
        {
            if (edits.Count == 0) continue;
            if (string.Equals(path, currentUiPath, StringComparison.OrdinalIgnoreCase))
            {
                files.Insert(0, new UiRenameFile(null, analysis.Text, edits));
            }
            else
            {
                // 其他 .bui：文本口径与快照生成时一致（缓冲区优先，其次磁盘）。
                var text = projects.TryBuffer(path, out var buffered) ? buffered : File.ReadAllText(path);
                files.Add(new UiRenameFile(path, text, edits));
            }
        }

        return files.Count > 0 ? new UiRenameResult(files) : null;
    }

    /// <summary>
    /// 光标 → 生成文档里的符号 + 光标处标识符在 .bui 的区间。符号经
    /// <see cref="SymbolFinder.FindSymbolAtPositionAsync"/>（touch 语义，与 IDE 一致）；
    /// .bui 区间直接取 .bui 文本的标识符范围（与 CursorScanner 同一套字符规则），
    /// 不依赖生成 token 的边界选取。
    /// </summary>
    private static async Task<(Document Document, ISymbol Symbol, TextSpan UiSpan)?> ResolveAsync(
        DocumentAnalysis analysis, string uiPath, int offset, CancellationToken cancellationToken)
    {
        var generated = analysis.GeneratedDocument;
        var project = analysis.Project;
        if (generated is null || project is null) return null;
        if (!generated.Mappings.TryMapPositionToGenerated(offset, out var generatedOffset)) return null;

        var document = EditorDocumentWorkspace.For(project).GeneratedDocumentFor(uiPath);
        if (document is null) return null;
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, generatedOffset, cancellationToken)
            .ConfigureAwait(false);
        if (symbol is null) return null;

        // 只改成员与局部变量/参数；类型/命名空间不改（类名 = 文件名契约）。
        if (symbol is not (ILocalSymbol or IParameterSymbol or IMethodSymbol
            or IPropertySymbol or IFieldSymbol or IEventSymbol)) return null;
        // 元数据符号没有源码声明，改不了；跨项目符号在本解里改不全。
        if (!symbol.Locations.Any(static l => l.IsInSource)) return null;
        if (!string.Equals(symbol.ContainingAssembly?.Name, project.Evaluation.AssemblyName,
                StringComparison.Ordinal)) return null;

        var text = analysis.Text;
        int start = offset, end = offset;
        while (start > 0 && IsIdentChar(text[start - 1])) start--;
        while (end < text.Length && IsIdentChar(text[end])) end++;
        if (start == end) return null;

        return (document, symbol, new TextSpan(start, end - start));
    }

    private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}