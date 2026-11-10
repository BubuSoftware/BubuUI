using EclipseUI.Compiler;
using EclipseUI.Compiler.Probe;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Roslyn 与本项目的 TextSpan 同名：walker 内部用 Roslyn 的（树 span），对外交付用项目的
// （Start/Length 值语义，两者恰好同形，转换只差构造）。别名避免歧义。
using RoslynTextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// 重命名（L3 第二片）：跳转定义已经能定位符号，这里反扫**所有出现点**并给出编辑。
/// 范围口径（v1，宁可拒绝也不给不完备的编辑）：
///
/// - <b>@foreach 迭代变量</b>：改本文档（头声明 + 表达式内引用）。变量作用域就是文档，
///   天然完备——迭代变量的引用不可能出现在别的文件里。
/// - <b>code-behind 类自身成员</b>：本文档 <c>{expr}</c> + <c>@code</c> 块引用 +
///   兄弟 .cs 声明与引用 + 工作区其余 .cs（在探针编译上解析）。声明不在兄弟文件
///   （<c>@code</c> 合成树）、或在其他类型上（<c>task.Name</c> 的 Name、工具类静态成员）
///   → 拒绝——那些声明/引用不在本 server 能保证改全的文件集合里。
///
/// 已知边界：工作区 .cs 依赖探针编译的引用集，外部包的引用缺失时个别标识符绑不出来
/// 会漏改（宁可少改不错改）；重命名后编译一次兜底。表达式环境有一层 foreach 推断
/// 失败时，成员引用扫描跳过该表达式（同理由）。
/// </summary>
public static class RenameProvider
{
    /// <summary>工作区 .cs 扫描上限：超过就拒绝成员重命名（不能声称改全）。</summary>
    internal const int MaxWorkspaceFiles = 500;

    internal static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".vscode", "packages", "artifacts", "testresults",
    };

    private abstract record RenameTarget;

    /// <summary>@foreach 迭代变量：作用域 = 本文档，改动全部落在 .ui。</summary>
    private sealed record ForeachVarTarget(ForeachScope Scope) : RenameTarget;

    /// <summary>code-behind 类自身成员：.ui + 兄弟 .cs + 工作区 .cs。</summary>
    private sealed record MemberTarget(ISymbol Symbol) : RenameTarget;

    /// <summary>
    /// prepareRename：告诉客户端光标处是否可改名 + 替换区间。不可重命名（标签名上、
    /// 点号上、外部类型成员上）返回 null，客户端据此给出"无法重命名"。
    /// </summary>
    public static UiPrepareRename? Prepare(DocumentAnalysis analysis, int offset)
    {
        var resolved = ResolveTarget(analysis, offset);
        if (resolved is null) return null;
        var span = resolved.Value.Span;
        return new UiPrepareRename(span, analysis.Text.Substring(span.Start, span.Length));
    }

    /// <summary>
    /// 重命名：解析目标 → 反扫出现点 → 按文件分组编辑。目标不在允许范围内、新名字不是
    /// 合法标识符（含 C# 关键字）、工作区不可用（成员分支）时返回 null。
    /// </summary>
    public static UiRenameResult? Rename(DocumentAnalysis analysis, int offset, string newName, string? workspaceRoot)
    {
        if (!IsIdentifier(newName)) return null;
        var probe = analysis.Probe;
        if (probe is null || analysis.Ast is null) return null;

        var resolved = ResolveTarget(analysis, offset);
        if (resolved is null) return null;
        var (target, _, context) = resolved.Value;

        var uiEdits = new List<UiTextEdit>();
        UiRenameFile? codeBehindFile = null;
        var extraCodeBehindFiles = new List<UiRenameFile>();

        switch (target)
        {
            case ForeachVarTarget variable:
                // 目标层必须推得出来：集合推不出类型 = 引用扫描必然漏（env 缺这个名字，
                // 表达式里的引用绑不到注入的局部变量上），此时拒绝整个改名。
                if (!TargetScopeInfers(analysis, probe, variable.Scope, context.OpenForeaches)) return null;
                uiEdits.Add(new UiTextEdit(
                    new TextSpan(variable.Scope.NameOffset, variable.Scope.VarName.Length), newName));
                CollectUiOccurrences(analysis, probe, target, uiEdits, newName);
                break;

            case MemberTarget member:
            {
                // 声明必须落在兄弟 .cs：@code 合成树没有真实文件可编辑（跳转定义同口径）；
                // 基类/外部类型的成员已被 containing-type 检查拒绝。
                var location = member.Symbol.Locations.FirstOrDefault(static l => l.IsInSource);
                if (location?.SourceTree is null || !analysis.IsCodeBehindTree(location.SourceTree)) return null;
                if (analysis.CodeBehindPath is null || analysis.CodeBehindText is null) return null;
                if (workspaceRoot is null) return null;

                CollectUiOccurrences(analysis, probe, target, uiEdits, newName);
                CollectCodeBlockOccurrences(analysis, probe, member.Symbol, newName, uiEdits);
                var codeBehindFiles = CollectCodeBehindFiles(
                    analysis, probe, member.Symbol, newName, workspaceRoot);
                if (codeBehindFiles is null) return null;
                codeBehindFile = codeBehindFiles.Count > 0 ? codeBehindFiles[0] : null;
                extraCodeBehindFiles.AddRange(codeBehindFiles.Skip(1));
                break;
            }
        }

        var files = new List<UiRenameFile> { new(null, analysis.Text, uiEdits) };
        if (codeBehindFile is not null) files.Add(codeBehindFile);
        files.AddRange(extraCodeBehindFiles);
        return new UiRenameResult(files);
    }

    // ---- 目标定位 ----

    private static (RenameTarget Target, TextSpan Span, CursorContext Context)? ResolveTarget(
        DocumentAnalysis analysis, int offset)
    {
        var probe = analysis.Probe;
        if (probe is null) return null;
        var text = analysis.Text;
        var context = CursorScanner.Scan(text, offset);
        if (context.Kind != CursorKind.Expression || context.ExpressionStart < 0) return null;

        // foreach 头：光标在变量名上 → 变量目标；在集合表达式上 → 常规符号路径。
        // 头文本整体不是合法 C#（`var x in y`），只有这两段能单独探针。
        if (context.Directive == "foreach")
        {
            var header = CursorScanner.ParseForeachHeader(
                text, context.ExpressionStart, context.ExpressionEnd - context.ExpressionStart);
            if (header is null) return null;
            if (offset >= header.NameOffset && offset <= header.NameOffset + header.VarName.Length)
            {
                var scope = new ForeachScope
                {
                    VarName = header.VarName,
                    Collection = header.Collection,
                    NameOffset = header.NameOffset,
                };
                return (new ForeachVarTarget(scope),
                    new TextSpan(header.NameOffset, header.VarName.Length), context);
            }
            if (offset >= header.CollectionStart && offset <= header.CollectionEnd)
            {
                var collection = text.Substring(
                    header.CollectionStart, header.CollectionEnd - header.CollectionStart);
                var env = ExpressionEnvironment.Build(
                    analysis.Ast!, context.OpenForeaches, probe, probe.Compilation);
                var binding = probe.LocateWithIdentifier(
                    collection, offset - header.CollectionStart, env);
                if (binding is null) return null;
                var span = new TextSpan(header.CollectionStart + binding.Value.Offset, binding.Value.Length);
                return Classify(probe, binding.Value, span, context.OpenForeaches) is { } hit
                    ? (hit.Target, hit.Span, context)
                    : null;
            }
            return null; // 落在 `var`/`in`/空白上：不可改名。
        }

        var expression = text.Substring(
            context.ExpressionStart, context.ExpressionEnd - context.ExpressionStart);
        var locateEnv = ExpressionEnvironment.Build(
            analysis.Ast!, context.OpenForeaches, probe, probe.Compilation);
        var located = probe.LocateWithIdentifier(expression, offset - context.ExpressionStart, locateEnv);
        if (located is null) return null;
        var targetSpan = new TextSpan(context.ExpressionStart + located.Value.Offset, located.Value.Length);
        return Classify(probe, located.Value, targetSpan, context.OpenForeaches) is { } resolved
            ? (resolved.Target, resolved.Span, context)
            : null;
    }

    /// <summary>
    /// 符号 → 重命名目标。环境注入的局部变量 = <see cref="ForeachVarTarget"/>（绑定者是
    /// 栈内最内层同名作用域——<see cref="ExpressionEnvironment.Build"/> 只注入最内层，
    /// 一一对应）；成员符号只接受 code-behind 类自身成员，其他类型一律拒绝。
    /// </summary>
    private static (RenameTarget Target, TextSpan Span)? Classify(
        ExpressionProbe probe, IdentifierBinding binding, TextSpan span,
        IReadOnlyList<ForeachScope> scopes)
    {
        if (binding.Symbol is ILocalSymbol local)
        {
            ForeachScope? binder = null;
            foreach (var scope in scopes) // 外层在前：同名取最后 = 最内层 = 绑定者
                if (scope.VarName == local.Name) binder = scope;
            return binder is null ? null : (new ForeachVarTarget(binder), span);
        }

        if (probe.ThisType is null || binding.Symbol.ContainingType is null ||
            !SameSymbol(SignatureOf(binding.Symbol.ContainingType), SignatureOf(probe.ThisType)))
            return null;
        return (new MemberTarget(binding.Symbol), span);
    }

    /// <summary>
    /// 跨编译的符号同一性（v1 口径）：探针每次调用都派生新编译，含 <c>@code</c> 成员树时
    /// merged partial 的 identity key（declaring sites 集合）与单 site 版不同——Roslyn 的
    /// <see cref="SymbolEqualityComparer"/> 会给出**假阴性**（实测 Gallery.ProbeDoc 不等于
    /// Gallery.ProbeDoc）。改用全限定签名字符串：成员含 containing 类型与参数签名（区分
    /// 重载），类型含命名空间。跨程序集同名同签名的极端误报是 v1 已知边界。
    /// </summary>
    private static string SignatureOf(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static bool SameSymbol(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>
    /// 目标 foreach 层能否推断出来（外层链用 Build 丢失败层——它们不阻碍目标层，除非
    /// 集合真的引用了失败层的名字，那时目标层自然也推不出）。
    /// </summary>
    private static bool TargetScopeInfers(
        DocumentAnalysis analysis, ExpressionProbe probe, ForeachScope target,
        IReadOnlyList<ForeachScope> contextStack)
    {
        var enclosing = new List<ForeachScope>();
        foreach (var scope in contextStack)
        {
            if (scope.NameOffset == target.NameOffset) break;
            enclosing.Add(scope);
        }
        var env = ExpressionEnvironment.Build(analysis.Ast!, enclosing, probe, probe.Compilation);
        var collectionType = probe.InferType(target.Collection, env);
        if (collectionType is null) return false;
        var itemType = ExpressionProbe.ItemTypeOf(collectionType, probe.Compilation);
        return itemType is not null && itemType.TypeKind != TypeKind.Error && itemType is not IDynamicTypeSymbol;
    }

    // ---- .ui 出现点 ----

    /// <summary>
    /// 扫本文档所有表达式里的引用。foreach 头只探集合表达式（头整体不是合法 C#）；
    /// 成员目标要求环境**一层都不缺**（缺层时变量名会错误绑到成员上，引用就错了），
    /// 变量目标用 Build 丢失败层即可——目标层已由 <see cref="TargetScopeInfers"/> 保证，
    /// 其他层缺失不影响目标名字的绑定。
    /// </summary>
    private static void CollectUiOccurrences(
        DocumentAnalysis analysis, ExpressionProbe probe,
        RenameTarget target, List<UiTextEdit> edits, string newName)
    {
        foreach (var collected in CursorScanner.CollectExpressions(analysis.Text))
        {
            var rangeStart = collected.Header?.CollectionStart ?? collected.Start;
            var rangeEnd = collected.Header?.CollectionEnd ?? collected.End;
            if (rangeEnd <= rangeStart) continue;

            List<(ITypeSymbol Type, string Name)> env;
            if (target is MemberTarget)
            {
                if (!ExpressionEnvironment.TryBuild(analysis.Ast!, collected.ForeachStack, probe, probe.Compilation, out env))
                    continue;
            }
            else
            {
                env = ExpressionEnvironment.Build(analysis.Ast!, collected.ForeachStack, probe, probe.Compilation);
            }

            var snippet = analysis.Text.Substring(rangeStart, rangeEnd - rangeStart);
            foreach (var hit in probe.ClassifyIdentifiers(snippet, env))
            {
                if (Matches(target, hit.Symbol, collected.ForeachStack))
                    edits.Add(new UiTextEdit(new TextSpan(rangeStart + hit.Offset, hit.Length), newName));
            }
        }
    }

    private static bool Matches(RenameTarget target, ISymbol symbol, IReadOnlyList<ForeachScope> stack) =>
        target switch
        {
            ForeachVarTarget variable => symbol is ILocalSymbol local
                && Innermost(stack, local.Name) is { } binder
                && binder.NameOffset == variable.Scope.NameOffset,
            MemberTarget member => SameSymbol(SignatureOf(symbol), SignatureOf(member.Symbol)),
            _ => false,
        };

    /// <summary>栈内最内层的同名作用域（栈外层在前 → 同名取最后一个）。裸名绑定语义与之一致。</summary>
    private static ForeachScope? Innermost(IReadOnlyList<ForeachScope> stack, string name)
    {
        ForeachScope? binder = null;
        foreach (var scope in stack)
            if (scope.VarName == name) binder = scope;
        return binder;
    }

    /// <summary>
    /// 扫 <c>@code</c> 块体里的成员引用：块体在合成成员树里逐字出现，IndexOf 定位后
    /// 按偏移差回映到 .ui（<see cref="UiCodeBlock.CodeSpan"/>）。变量目标不扫这里——
    /// foreach 变量在 C# 体里不可见。
    /// </summary>
    private static void CollectCodeBlockOccurrences(
        DocumentAnalysis analysis, ExpressionProbe probe,
        ISymbol target, string newName, List<UiTextEdit> edits)
    {
        var memberTree = analysis.MemberTree;
        if (memberTree is null) return;
        var augmented = analysis.CompilationContains(memberTree)
            ? probe.Compilation
            : probe.Compilation.AddSyntaxTrees(memberTree);
        var model = augmented.GetSemanticModel(memberTree);
        var treeText = memberTree.ToString();
        var targetSignature = SignatureOf(target);
        var searchFrom = 0;
        foreach (var block in analysis.Ast!.Nodes.OfType<UiCodeBlock>())
        {
            var bodyStart = treeText.IndexOf(block.Code, searchFrom, StringComparison.Ordinal);
            if (bodyStart < 0) continue;
            searchFrom = bodyStart + block.Code.Length;
            foreach (var span in SymbolReferenceSpans(memberTree.GetRoot(), model, targetSignature))
                edits.Add(new UiTextEdit(
                    new TextSpan(block.CodeSpan.Start + (span.Start - bodyStart), span.Length), newName));
        }
    }

    // ---- code-behind 与工作区 .cs 出现点 ----

    /// <summary>
    /// 兄弟 .cs（声明 + 本文件引用，已在探针编译里）+ 工作区其余 .cs（同一派生编译里
    /// 解析，工程内类型互相可见），**按文件分组**返回——每个文件自己的原文换算行位，
    /// span 绝不跨文件混装。工作区文件超过 <see cref="MaxWorkspaceFiles"/> 返回 null——
    /// 不能声称改全，调用方拒绝整个重命名。
    /// </summary>
    private static List<UiRenameFile>? CollectCodeBehindFiles(
        DocumentAnalysis analysis, ExpressionProbe probe,
        ISymbol target, string newName, string workspaceRoot)
    {
        var result = new List<UiRenameFile>();
        var siblingTree = analysis.CodeBehindTree;
        if (siblingTree is null) return null;
        var targetSignature = SignatureOf(target);
        AppendFile(result, analysis.CodeBehindPath!, analysis.CodeBehindText!,
            SymbolReferenceSpans(siblingTree.GetRoot(), probe.Compilation.GetSemanticModel(siblingTree), targetSignature),
            newName);

        var files = ListWorkspaceSourceFiles(workspaceRoot);
        if (files is null) return null;
        var parseOptions = (probe.Compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions)
            ?? CSharpParseOptions.Default;
        var extraTrees = new List<SyntaxTree>();
        var freshTrees = new List<SyntaxTree>();
        foreach (var file in files)
        {
            if (SamePath(file, analysis.CodeBehindPath!)) continue;
            // 项目 Compilation（L2）已含该文件的源码树时复用它；重新 ParseText 会造出
            // 同路径的第二棵树，让同一类型出现两次、重命名绑错符号。
            var existing = analysis.Project is null
                ? null
                : probe.Compilation.SyntaxTrees.FirstOrDefault(t => SamePath(t.FilePath, file));
            if (existing is not null)
            {
                extraTrees.Add(existing);
                continue;
            }
            try
            {
                var parsed = CSharpSyntaxTree.ParseText(File.ReadAllText(file), parseOptions, file);
                extraTrees.Add(parsed);
                freshTrees.Add(parsed);
            }
            catch (Exception)
            {
                // 读不了（被锁/权限）：跳过。该文件里的引用绑不出来 → 漏改，编译兜底。
            }
        }
        if (extraTrees.Count > 0)
        {
            var augmented = freshTrees.Count == 0
                ? probe.Compilation
                : probe.Compilation.AddSyntaxTrees(freshTrees);
            foreach (var tree in extraTrees)
            {
                AppendFile(result, tree.FilePath, tree.GetText().ToString(),
                    SymbolReferenceSpans(tree.GetRoot(), augmented.GetSemanticModel(tree), targetSignature), newName);
            }
        }
        return result;
    }

    private static void AppendFile(
        List<UiRenameFile> result, string path, string text,
        IEnumerable<RoslynTextSpan> spans, string newName)
    {
        var edits = spans.Select(s => new UiTextEdit(new TextSpan(s.Start, s.Length), newName)).ToList();
        if (edits.Count > 0) result.Add(new UiRenameFile(path, text, edits));
    }

    /// <summary>
    /// 一棵 C# 树里目标符号的所有出现点：引用（IdentifierName）与声明（属性/方法/字段
    /// 声明器/类型/参数/foreach 变量），落在标识符区间上而不是整个声明。跨编译比较用
    /// <paramref name="targetSignature"/>（identity key 会因 merged partial 假阴性）。
    /// </summary>
    private static IEnumerable<RoslynTextSpan> SymbolReferenceSpans(SyntaxNode root, SemanticModel model, string targetSignature)
    {
        foreach (var node in root.DescendantNodes())
        {
            ISymbol? symbol;
            RoslynTextSpan span;
            switch (node)
            {
                case IdentifierNameSyntax identifier:
                    var info = model.GetSymbolInfo(identifier);
                    symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
                    span = identifier.Span;
                    break;
                case VariableDeclaratorSyntax declarator:
                    symbol = model.GetDeclaredSymbol(declarator);
                    span = declarator.Identifier.Span;
                    break;
                case PropertyDeclarationSyntax property:
                    symbol = model.GetDeclaredSymbol(property);
                    span = property.Identifier.Span;
                    break;
                case MethodDeclarationSyntax method:
                    symbol = model.GetDeclaredSymbol(method);
                    span = method.Identifier.Span;
                    break;
                case ConstructorDeclarationSyntax constructor:
                    symbol = model.GetDeclaredSymbol(constructor);
                    span = constructor.Identifier.Span;
                    break;
                case EventDeclarationSyntax @event:
                    symbol = model.GetDeclaredSymbol(@event);
                    span = @event.Identifier.Span;
                    break;
                case TypeDeclarationSyntax type:
                    symbol = model.GetDeclaredSymbol(type);
                    span = type.Identifier.Span;
                    break;
                case ParameterSyntax parameter:
                    symbol = model.GetDeclaredSymbol(parameter);
                    span = parameter.Identifier.Span;
                    break;
                case ForEachStatementSyntax forEach:
                    symbol = model.GetDeclaredSymbol(forEach);
                    span = forEach.Identifier.Span;
                    break;
                default:
                    continue;
            }
            if (symbol is not null && SameSymbol(SignatureOf(symbol), targetSignature))
                yield return span;
        }
    }

    private static List<string>? ListWorkspaceSourceFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    if (SkippedDirectories.Contains(Path.GetFileName(sub))) continue;
                    pending.Push(sub);
                }
                foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
                {
                    files.Add(file);
                    if (files.Count > MaxWorkspaceFiles) return null;
                }
            }
            catch (Exception)
            {
                // 权限/并发删除：跳过该目录，不终止整个扫描。
            }
        }
        return files;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>合法 C# 标识符（保留关键字拒绝；var 是上下文关键字，允许）。</summary>
    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        for (var i = 1; i < name.Length; i++)
            if (!(char.IsAsciiLetterOrDigit(name[i]) || name[i] == '_')) return false;
        return SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;
    }
}
