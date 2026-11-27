using System.Text;
using BubuUI.Compiler;
using BubuUI.Compiler.Metadata;
using BubuUI.Compiler.Probe;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using UiSpan = BubuUI.Compiler.TextSpan;

namespace BubuUI.LanguageServer.Analysis;

// ---- LSP 中立模型（宿主负责 JSON 翻译，Rider 走同一份） ----

public sealed record UiSignatureParameter(string Label, string? Documentation);

public sealed record UiSignature(string Label, string? Documentation, IReadOnlyList<UiSignatureParameter> Parameters);

public sealed record UiSignatureHelp(
    IReadOnlyList<UiSignature> Signatures, int ActiveSignature, int ActiveParameter);

public sealed record UiSemanticToken(int Line, int Character, int Length, int TokenType, int TokenModifiers);

/// <summary>代码操作的编辑目标：<see cref="Path"/> null = 当前 .bui，否则磁盘文件路径。</summary>
public sealed record UiCodeAction(
    string Title, string Kind, string? Path, IReadOnlyList<UiTextEdit> Edits, string? DiagnosticCode);

/// <summary>
/// L8 的语义 token 类型表（LSP 3.17 标准顺序的一部分）。legend 由宿主原样声明，
/// 索引与 <see cref="SemanticTokenTypes.All"/> 一一对应，不能随手插入。
/// </summary>
public static class SemanticTokenTypes
{
    public static readonly string[] All =
    {
        "namespace", "type", "class", "struct", "enum", "interface",
        "method", "property", "field", "variable", "parameter",
        "keyword", "string", "number", "operator", "comment",
    };

    public static int IndexOf(string name) => Array.IndexOf(All, name);
}

/// <summary>
/// L8：内嵌 C# 完整语言服务（design/06 §6.9.0 第 5 条）。全部挂在**同一份项目 Compilation**
/// 与同一套偏移回映上，不接管 .cs 文件（那由 C# Dev Kit / ReSharper 负责）。
///
/// 投影与生成器的 ExpressionProbe 同形态：#nullable enable + @using 行 + 同命名空间 partial
/// 类 + 方法体（@foreach 变量按推断类型注入局部变量），表达式原文逐字进树，所以
/// 「树内偏移 ↔ .bui 偏移」只是一次加法，不另造映射（§6.9.3 L8）。
/// </summary>
public static class EmbeddedCSharpService
{
    private static readonly SymbolDisplayFormat Minimal = SymbolDisplayFormat.MinimallyQualifiedFormat;

    // ---- 投影 ----

    private sealed class Projection
    {
        public required SyntaxTree Tree { get; init; }
        public required Compilation Compilation { get; init; }
        public required int BodyStart { get; init; }
        public required int BodyLength { get; init; }
        public SemanticModel Model => Compilation.GetSemanticModel(Tree);
        public string Text => Tree.ToString();
    }

    private static Projection? ForExpression(DocumentAnalysis analysis, CursorContext context, string expression)
    {
        var probe = analysis.Probe;
        if (probe is null || analysis.Ast is null || expression.Length == 0) return null;
        try
        {
            var env = ExpressionEnvironment.Build(analysis.Ast, context.OpenForeaches, probe, probe.Compilation);
            var usings = TemplateAnalyzer.CollectUsingLines(analysis.Ast.Nodes);
            var type = probe.ThisType;
            var namespaceName = type?.ContainingNamespace is { IsGlobalNamespace: false } ns
                ? ns.ToDisplayString()
                // spec/02 §2.2：声明优先于项目根命名空间推导。
                : analysis.Ast.Namespace is { Length: > 0 } declaredNs
                    ? declaredNs
                    : analysis.Project?.Evaluation.RootNamespace ?? "";
            var className = type?.Name ?? analysis.Ast.ClassName;
            var accessibility = type is null || type.DeclaredAccessibility == Accessibility.Public
                ? "public" : "internal";

            var sb = new StringBuilder();
            sb.AppendLine("#nullable enable");
            foreach (var line in usings) sb.AppendLine("using " + line + ";");
            if (namespaceName.Length > 0)
            {
                sb.Append("namespace ").Append(namespaceName).AppendLine(";");
                sb.AppendLine();
            }
            sb.Append(accessibility).Append(" partial class ").AppendLine(className);
            sb.AppendLine("{");
            sb.AppendLine("    private void __BubuUiL8()");
            sb.AppendLine("    {");
            foreach (var (envType, name) in env)
                sb.Append("        ").Append(envType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    .Append(' ').Append(name).AppendLine(" = default!;");
            sb.Append("        var __value = (");
            var bodyStart = sb.Length;
            sb.Append(expression);
            sb.AppendLine(");");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            var text = sb.ToString();
            var parseOptions = probe.Compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
                ?? CSharpParseOptions.Default;
            var tree = CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(text), parseOptions, "<bubuui-l8>");
            return new Projection
            {
                Tree = tree,
                Compilation = probe.Compilation.AddSyntaxTrees(tree),
                BodyStart = bodyStart,
                BodyLength = expression.Length,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Projection? ForCodeBlock(DocumentAnalysis analysis, int offset)
    {
        var probe = analysis.Probe;
        if (probe is null || analysis.Ast is null) return null;
        var memberTree = analysis.MemberTree;
        if (memberTree is null) return null;
        var block = analysis.Ast.Nodes.OfType<UiCodeBlock>().FirstOrDefault(b =>
            offset >= b.CodeSpan.Start && offset <= b.CodeSpan.Start + b.CodeSpan.Length);
        if (block is null) return null;
        try
        {
            // design/18 §5.2：项目模式走统一映射（发射时记录）；符号视图按块体原文定位。
            var bodyStart = -1;
            if (analysis.GeneratedDocument is { } generated && ReferenceEquals(generated.Tree, memberTree))
            {
                if (generated.Mappings.TryMapToGenerated(block.CodeSpan, out var mapped))
                    bodyStart = mapped.Start;
            }
            else
            {
                bodyStart = memberTree.ToString().IndexOf(block.Code, StringComparison.Ordinal);
            }
            if (bodyStart < 0) return null;
            var compilation = probe.Compilation;
            return new Projection
            {
                Tree = memberTree,
                Compilation = compilation,
                BodyStart = bodyStart,
                BodyLength = block.Code.Length,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Projection? At(DocumentAnalysis analysis, int offset, out int treeOffset, out int uiStart)
    {
        treeOffset = 0;
        uiStart = 0;
        var context = CursorScanner.Scan(analysis.Text, offset);
        if (context.Kind == CursorKind.CodeBlock)
        {
            var projection = ForCodeBlock(analysis, offset);
            if (projection is null) return null;
            var block = analysis.Ast!.Nodes.OfType<UiCodeBlock>().First(b =>
                offset >= b.CodeSpan.Start && offset <= b.CodeSpan.Start + b.CodeSpan.Length);
            uiStart = block.CodeSpan.Start;
            treeOffset = projection.BodyStart + (offset - uiStart);
            return projection;
        }
        if (context.Kind == CursorKind.Expression &&
            context.ExpressionStart >= 0 && context.ExpressionEnd > context.ExpressionStart)
        {
            var expression = analysis.Text.Substring(context.ExpressionStart,
                context.ExpressionEnd - context.ExpressionStart);
            var projection = ForExpression(analysis, context, expression);
            if (projection is null) return null;
            uiStart = context.ExpressionStart;
            treeOffset = projection.BodyStart + Math.Clamp(offset - uiStart, 0, expression.Length);
            return projection;
        }
        return null;
    }

    // ---- @code 光标符号（design/18 阶段 1：悬停 / 重命名共享入口） ----

    /// <summary>
    /// <c>@code</c> 块内光标处的符号 + 其标识符在 .bui 的区间。投影/绑定复用 <see cref="At"/>
    /// （真实成员树 + 项目 Compilation），回映走统一映射服务
    /// （<see cref="DocumentAnalysis.TryMapMemberSpanToUi"/>）。解析不出或映不回返回 null——不猜。
    /// </summary>
    public static (ISymbol Symbol, UiSpan Span)? CodeBlockSymbolAt(DocumentAnalysis analysis, int offset)
    {
        var context = CursorScanner.Scan(analysis.Text, offset);
        if (context.Kind != CursorKind.CodeBlock) return null;
        var projection = At(analysis, offset, out var treeOffset, out _);
        if (projection is null) return null;
        try
        {
            var token = projection.Tree.GetRoot().FindToken(treeOffset);
            var (symbol, span) = SymbolAtToken(projection.Model, token);
            if (symbol is null) return null;
            if (!analysis.TryMapMemberSpanToUi(new UiSpan(span.Start, span.Length), out var uiSpan)) return null;
            return (symbol, uiSpan);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>token 处的符号与其标识符的树内区间（声明取声明器名字，引用取 IdentifierName）。</summary>
    private static (ISymbol? Symbol, Microsoft.CodeAnalysis.Text.TextSpan Span) SymbolAtToken(
        SemanticModel model, SyntaxToken token) => token.Parent switch
    {
        IdentifierNameSyntax identifier =>
            (model.GetSymbolInfo(identifier).Symbol ?? model.GetDeclaredSymbol(identifier), identifier.Span),
        VariableDeclaratorSyntax variable => (model.GetDeclaredSymbol(variable), variable.Identifier.Span),
        ParameterSyntax parameter => (model.GetDeclaredSymbol(parameter), parameter.Identifier.Span),
        MethodDeclarationSyntax method => (model.GetDeclaredSymbol(method), method.Identifier.Span),
        PropertyDeclarationSyntax property => (model.GetDeclaredSymbol(property), property.Identifier.Span),
        LocalDeclarationStatementSyntax local when local.Declaration.Variables.Count == 1 =>
            (model.GetDeclaredSymbol(local.Declaration.Variables[0]), local.Declaration.Variables[0].Identifier.Span),
        SingleVariableDesignationSyntax designation => (model.GetDeclaredSymbol(designation), designation.Identifier.Span),
        TypeDeclarationSyntax type => (model.GetDeclaredSymbol(type), type.Identifier.Span),
        _ => (null, default),
    };

    // ---- 签名帮助 ----

    public static UiSignatureHelp? SignatureHelp(DocumentAnalysis analysis, int offset)
    {
        var projection = At(analysis, offset, out var treeOffset, out _);
        if (projection is null) return null;
        try
        {
            var root = projection.Tree.GetRoot();
            var token = root.FindToken(treeOffset);
            var invocation = token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (invocation is null) return null;

            var activeParameter = 0;
            foreach (var separator in invocation.ArgumentList.Arguments.GetSeparators())
                if (separator.SpanStart < treeOffset) activeParameter++;

            var info = projection.Model.GetSymbolInfo(invocation);
            var methods = new List<IMethodSymbol>();
            if (info.Symbol is IMethodSymbol resolved) methods.Add(resolved);
            methods.AddRange(info.CandidateSymbols.OfType<IMethodSymbol>());
            if (methods.Count == 0)
                methods.AddRange(projection.Model.GetMemberGroup(invocation.Expression).OfType<IMethodSymbol>());
            methods = methods.Distinct(SymbolEqualityComparer.Default).OfType<IMethodSymbol>().ToList();
            if (methods.Count == 0) return null;

            var signatures = methods.Select(ToSignature).ToArray();
            var active = info.Symbol is IMethodSymbol exact
                ? Math.Max(0, methods.FindIndex(m => SymbolEqualityComparer.Default.Equals(m, exact)))
                : 0;
            return new UiSignatureHelp(signatures, active, activeParameter);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static UiSignature ToSignature(IMethodSymbol method) => new(
        method.ReturnType.ToDisplayString(Minimal) + " " + method.Name + "(" +
        string.Join(", ", method.Parameters.Select(static p =>
            p.Type.ToDisplayString(Minimal) + " " + p.Name)) + ")",
        method.ContainingType?.ToDisplayString(Minimal),
        method.Parameters.Select(static p => new UiSignatureParameter(
            p.Type.ToDisplayString(Minimal) + " " + p.Name,
            p.Name)).ToArray());

    // ---- 语义着色 ----

    /// <summary>
    /// 只着色 <c>{expr}</c> 与 <c>@code</c> 区间（模板表面仍归 TextMate）。每个区间各自投影一次，
    /// token 的树内偏移按 BodyStart 回映到 .bui；同位置重叠 token 丢弃而不是猜（R6）。
    /// 独立文档 / 无生成文档时的路径；项目模式优先走 <see cref="SemanticTokensAsync"/>。
    /// </summary>
    public static IReadOnlyList<UiSemanticToken> SemanticTokens(DocumentAnalysis analysis)
    {
        if (analysis.Ast is null) return Array.Empty<UiSemanticToken>();
        var hits = new List<(int Start, int Length, int Type)>();
        var positions = analysis.Positions;

        foreach (var expression in CursorScanner.CollectExpressions(analysis.Text))
        {
            var start = expression.Header?.CollectionStart ?? expression.Start;
            var end = expression.Header?.CollectionEnd ?? expression.End;
            if (end <= start || start >= analysis.Text.Length) continue;
            var body = analysis.Text.Substring(start, end - start);
            if (body.Trim().Length == 0) continue;
            var context = CursorScanner.Scan(analysis.Text, Math.Min(start + 1, analysis.Text.Length));
            var projection = ForExpression(analysis, context, body);
            if (projection is null) continue;
            Collect(projection, start, hits);
        }

        foreach (var block in analysis.Ast.Nodes.OfType<UiCodeBlock>())
        {
            var projection = ForCodeBlock(analysis, block.CodeSpan.Start);
            if (projection is null) continue;
            Collect(projection, block.CodeSpan.Start, hits);
        }

        return ToTokens(hits, analysis.Text, positions);
    }

    /// <summary>
    /// design/18 §5.8：项目模式用 Roslyn <see cref="Classifier"/> 对**生成文档**分类，
    /// 经统一映射服务把每个分类区间回映到 .bui——只有 {expr}/@code 的逐字区间映得回，
    /// 生成独有代码（类声明/Build/Track 等）天然被丢弃，模板表面仍归 TextMate。
    /// 映不回或分类不了的区间丢弃而不是猜（R6）。无生成文档 / 分类失败回落
    /// <see cref="SemanticTokens"/> 的探针投影路径，行为不回归。
    /// </summary>
    public static async Task<IReadOnlyList<UiSemanticToken>> SemanticTokensAsync(
        DocumentAnalysis analysis, string uiPath, CancellationToken cancellationToken)
    {
        if (analysis.GeneratedDocument is not { } generated || analysis.Project is not { } project)
            return SemanticTokens(analysis);
        var document = EditorDocumentWorkspace.For(project).GeneratedDocumentFor(uiPath);
        if (document is null) return SemanticTokens(analysis);
        try
        {
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var spans = await Classifier.GetClassifiedSpansAsync(
                document, new Microsoft.CodeAnalysis.Text.TextSpan(0, text.Length), cancellationToken)
                .ConfigureAwait(false);
            var hits = new List<(int Start, int Length, int Type)>();
            foreach (var span in spans)
            {
                if (!generated.Mappings.TryMapToOriginal(
                        new UiSpan(span.TextSpan.Start, span.TextSpan.Length), out var ui)) continue;
                var type = MapClassification(span.ClassificationType);
                if (type < 0 || ui.Length <= 0) continue;
                hits.Add((ui.Start, ui.Length, type));
            }
            return ToTokens(hits, analysis.Text, analysis.Positions);
        }
        catch (Exception)
        {
            return SemanticTokens(analysis);
        }
    }

    /// <summary>按起点排序、丢弃重叠与零宽，并将多行分类拆成客户端都支持的单行 token。</summary>
    private static IReadOnlyList<UiSemanticToken> ToTokens(
        List<(int Start, int Length, int Type)> hits, string text, PositionMap positions)
    {
        var result = new List<UiSemanticToken>();
        var lastEnd = -1;
        foreach (var hit in hits.OrderBy(static h => h.Start))
        {
            if (hit.Start < lastEnd || hit.Length <= 0) continue;
            lastEnd = hit.Start + hit.Length;
            // Roslyn returns one span for a multiline comment or verbatim/raw
            // string. Clients need multilineTokenSupport to accept it unchanged;
            // emit single-line spans without CR/LF for every host instead.
            var start = hit.Start;
            for (var offset = start; offset < lastEnd; offset++)
            {
                if (text[offset] is not ('\r' or '\n')) continue;
                AddLine(start, offset, hit.Type);
                start = offset + 1;
            }
            AddLine(start, lastEnd, hit.Type);
        }
        return result;

        void AddLine(int start, int end, int type)
        {
            if (end <= start) return;
            var (line, character) = positions.ToPosition(start);
            result.Add(new UiSemanticToken(line, character, end - start, type, 0));
        }
    }

    /// <summary>
    /// Roslyn 分类类型 → L8 legend 索引。Roslyn 粒度更细（字符串拆 escape、控制关键字单列、
    /// record/扩展方法/常量各有专名），这里归并到 16 类 legend；映不上的（identifier/text/
    /// punctuation/事件等）返回 -1 丢弃，与迷你分类器「不认识就不着色」一致。
    /// </summary>
    private static int MapClassification(string classificationType) => classificationType switch
    {
        ClassificationTypeNames.NamespaceName => SemanticTokenTypes.IndexOf("namespace"),
        ClassificationTypeNames.ClassName or ClassificationTypeNames.RecordClassName
            => SemanticTokenTypes.IndexOf("class"),
        ClassificationTypeNames.StructName or ClassificationTypeNames.RecordStructName
            => SemanticTokenTypes.IndexOf("struct"),
        ClassificationTypeNames.EnumName => SemanticTokenTypes.IndexOf("enum"),
        ClassificationTypeNames.InterfaceName => SemanticTokenTypes.IndexOf("interface"),
        ClassificationTypeNames.DelegateName or ClassificationTypeNames.ModuleName
            or ClassificationTypeNames.TypeParameterName => SemanticTokenTypes.IndexOf("type"),
        ClassificationTypeNames.MethodName or ClassificationTypeNames.ExtensionMethodName
            => SemanticTokenTypes.IndexOf("method"),
        ClassificationTypeNames.PropertyName => SemanticTokenTypes.IndexOf("property"),
        ClassificationTypeNames.FieldName or ClassificationTypeNames.EnumMemberName
            or ClassificationTypeNames.ConstantName => SemanticTokenTypes.IndexOf("field"),
        ClassificationTypeNames.LocalName => SemanticTokenTypes.IndexOf("variable"),
        ClassificationTypeNames.ParameterName => SemanticTokenTypes.IndexOf("parameter"),
        ClassificationTypeNames.Keyword or ClassificationTypeNames.ControlKeyword
            or ClassificationTypeNames.PreprocessorKeyword => SemanticTokenTypes.IndexOf("keyword"),
        ClassificationTypeNames.StringLiteral or ClassificationTypeNames.VerbatimStringLiteral
            or ClassificationTypeNames.StringEscapeCharacter => SemanticTokenTypes.IndexOf("string"),
        ClassificationTypeNames.NumericLiteral => SemanticTokenTypes.IndexOf("number"),
        ClassificationTypeNames.Operator or ClassificationTypeNames.OperatorOverloaded
            => SemanticTokenTypes.IndexOf("operator"),
        ClassificationTypeNames.Comment => SemanticTokenTypes.IndexOf("comment"),
        _ => -1,
    };

    private static void Collect(Projection projection, int uiStart, List<(int Start, int Length, int Type)> hits)
    {
        var bodyStart = projection.BodyStart;
        var bodyEnd = bodyStart + projection.BodyLength;
        var model = projection.Model;
        foreach (var token in projection.Tree.GetRoot().DescendantTokens())
        {
            if (token.SpanStart < bodyStart || token.Span.End > bodyEnd || token.IsMissing) continue;
            var kind = Classify(token, model);
            if (kind < 0) continue;
            hits.Add((uiStart + (token.SpanStart - bodyStart), token.Span.Length, kind));
        }
    }

    private static int Classify(SyntaxToken token, SemanticModel model)
    {
        var kind = token.Kind();
        if (SyntaxFacts.IsKeywordKind(kind) || SyntaxFacts.IsContextualKeyword(kind))
            return SemanticTokenTypes.IndexOf("keyword");
        if (kind == SyntaxKind.StringLiteralToken || kind == SyntaxKind.InterpolatedStringTextToken)
            return SemanticTokenTypes.IndexOf("string");
        if (kind == SyntaxKind.NumericLiteralToken) return SemanticTokenTypes.IndexOf("number");
        if (kind is SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.AsteriskToken
            or SyntaxKind.SlashToken or SyntaxKind.EqualsToken or SyntaxKind.EqualsEqualsToken
            or SyntaxKind.GreaterThanToken or SyntaxKind.LessThanToken or SyntaxKind.AmpersandAmpersandToken
            or SyntaxKind.BarBarToken or SyntaxKind.QuestionToken or SyntaxKind.ColonToken)
            return SemanticTokenTypes.IndexOf("operator");
        if (kind != SyntaxKind.IdentifierToken) return -1;

        var node = token.Parent;
        if (node is null) return -1;
        var symbol = model.GetSymbolInfo(node).Symbol ?? model.GetDeclaredSymbol(node);
        // A method group (especially an overloaded event handler) has no single
        // symbol in the standalone `var __value = (...)` projection. Its member
        // group still identifies it as a method, just like the declaration.
        if (symbol is null && node is ExpressionSyntax expression)
        {
            var members = model.GetMemberGroup(expression);
            if (members.Length > 0 && members.All(static member => member is IMethodSymbol))
                return SemanticTokenTypes.IndexOf("method");
        }
        if (symbol is null) return SemanticTokenTypes.IndexOf("variable");
        return symbol switch
        {
            INamespaceSymbol => SemanticTokenTypes.IndexOf("namespace"),
            INamedTypeSymbol { TypeKind: TypeKind.Class } => SemanticTokenTypes.IndexOf("class"),
            INamedTypeSymbol { TypeKind: TypeKind.Struct } => SemanticTokenTypes.IndexOf("struct"),
            INamedTypeSymbol { TypeKind: TypeKind.Enum } => SemanticTokenTypes.IndexOf("enum"),
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => SemanticTokenTypes.IndexOf("interface"),
            INamedTypeSymbol => SemanticTokenTypes.IndexOf("type"),
            IMethodSymbol => SemanticTokenTypes.IndexOf("method"),
            IPropertySymbol => SemanticTokenTypes.IndexOf("property"),
            IFieldSymbol => SemanticTokenTypes.IndexOf("field"),
            IParameterSymbol => SemanticTokenTypes.IndexOf("parameter"),
            ILocalSymbol => SemanticTokenTypes.IndexOf("variable"),
            _ => -1,
        };
    }

    // ---- 代码操作 ----

    /// <summary>
    /// 快速修复。Roslyn 的 CodeFixProvider 结果要经回映转 WorkspaceEdit，映不回的编辑
    /// **丢弃而不是猜**（R6）；模板级诊断（EUI001/EUI002）直接按目录给确定性修复。
    /// </summary>
    public static IReadOnlyList<UiCodeAction> CodeActions(
        DocumentAnalysis analysis, UiCatalog catalog, IReadOnlyList<UiDiagnostic> diagnostics)
    {
        var actions = new List<UiCodeAction>();
        foreach (var diagnostic in diagnostics)
        {
            switch (diagnostic.Code)
            {
                case UiDiagnostics.UnknownTag:
                    AddImportFix(analysis, catalog, diagnostic, actions);
                    break;
                case UiDiagnostics.UnknownProperty:
                    AddAttributeFix(analysis, catalog, diagnostic, actions);
                    break;
                case "CS0103":
                    AddMissingMemberFix(analysis, catalog, diagnostic, actions);
                    break;
            }
        }
        return actions;
    }

    private static void AddImportFix(DocumentAnalysis analysis, UiCatalog catalog,
        UiDiagnostic diagnostic, List<UiCodeAction> actions)
    {
        var tag = SafeSubstring(analysis.Text, diagnostic.Span);
        if (tag.Length == 0) return;
        var element = catalog.Elements.FirstOrDefault(e => e.Tag == tag)
                      ?? catalog.Elements.FirstOrDefault(e =>
                          string.Equals(TypeNames.Bare(e.ClrTypeDisplayName), tag, StringComparison.Ordinal));
        var ns = element is null ? null : TypeNames.NamespaceOf(element.ClrTypeDisplayName);
        // K33：@namespace 链内已隐式可见，不该再建议加 @using
        if (ns is null || analysis.IsNamespaceVisible(ns)) return;
        actions.Add(new UiCodeAction(
            "Add `@using " + ns + "`", "quickfix", null,
            new[] { new UiTextEdit(new UiSpan(0, 0), "@using " + ns + Environment.NewLine) },
            diagnostic.Code));
    }

    private static void AddAttributeFix(DocumentAnalysis analysis, UiCatalog catalog,
        UiDiagnostic diagnostic, List<UiCodeAction> actions)
    {
        var name = SafeSubstring(analysis.Text, diagnostic.Span);
        if (name.Length == 0) return;
        var context = CursorScanner.Scan(analysis.Text, diagnostic.Span.Start + 1);
        var element = analysis.AttributeOwner(catalog, context);
        if (element is null) return;
        var candidates = element.Properties.Keys
            .Concat(element.Events.Keys)
            .Concat(element.Parameters.Keys)
            .Concat(catalog.AttachedProperties.Select(static a => a.OwnerTag + "." + a.Name))
            .Distinct(StringComparer.Ordinal);
        var nearest = candidates.OrderBy(c => Levenshtein(name, c)).FirstOrDefault();
        if (nearest is null || string.Equals(nearest, name, StringComparison.Ordinal)) return;
        actions.Add(new UiCodeAction(
            "Replace with `" + nearest + "`", "quickfix", null,
            new[] { new UiTextEdit(diagnostic.Span, nearest) }, diagnostic.Code));
    }

    /// <summary>
    /// 生成缺失的 code-behind 成员：目标类型从目录属性（表达式写入的属性）取，映不出类型
    /// 就丢弃整个 action，不猜一个 object 字段出来（R6）。落在既有 @code 块末尾，
    /// 没有 @code 块则在文档末尾新建一个。
    /// </summary>
    private static void AddMissingMemberFix(DocumentAnalysis analysis, UiCatalog catalog,
        UiDiagnostic diagnostic, List<UiCodeAction> actions)
    {
        var name = SafeSubstring(analysis.Text, diagnostic.Span);
        if (name.Length == 0 || !IsIdentifier(name)) return;
        var context = CursorScanner.Scan(analysis.Text, diagnostic.Span.Start + 1);
        if (context.Kind != CursorKind.Expression || !context.IsAttributeExpression) return;
        var attribute = analysis.FindAttribute(catalog, context);
        var clrType = attribute?.Context is UiPropDef property
            ? property.ClrTypeDisplayName
            : attribute?.Context is UiParameterDef parameter
                ? parameter.ClrTypeDisplayName
                : null;
        if (clrType is null) return;

        var member = "public " + clrType + " " + name + " { get; set; } = default!;";
        var block = analysis.Ast?.Nodes.OfType<UiCodeBlock>().LastOrDefault();
        if (block is not null)
        {
            var at = block.CodeSpan.Start + block.CodeSpan.Length;
            actions.Add(new UiCodeAction(
                "Generate `" + name + "` in @code", "quickfix", null,
                new[] { new UiTextEdit(new UiSpan(at, 0), Environment.NewLine + "    " + member) },
                diagnostic.Code));
        }
        else
        {
            var at = analysis.Text.Length;
            actions.Add(new UiCodeAction(
                "Generate `" + name + "` in a new @code block", "quickfix", null,
                new[] { new UiTextEdit(new UiSpan(at, 0),
                    Environment.NewLine + "@code {" + Environment.NewLine + "    " + member +
                    Environment.NewLine + "}" + Environment.NewLine) },
                diagnostic.Code));
        }
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string SafeSubstring(string text, UiSpan span)
    {
        var start = Math.Clamp(span.Start, 0, text.Length);
        var length = Math.Clamp(span.Length, 0, text.Length - start);
        return length == 0 ? "" : text.Substring(start, length);
    }

    private static int Levenshtein(string a, string b)
    {
        var row = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) row[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var temp = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1),
                    previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = temp;
            }
        }
        return row[b.Length];
    }

    // ---- 格式化 ----

    /// <summary>
    /// 模板缩进由 AST/标签形态决定；@code 块体交 Roslyn <see cref="Formatter"/>
    /// （Workspaces 层，锁 4.9.2 与现有 Roslyn 同版）。返回逐行最小编辑，
    /// rangeFormatting 只需按请求区间过滤同一批编辑。
    /// </summary>
    public static IReadOnlyList<UiTextEdit> Format(DocumentAnalysis analysis)
    {
        var text = analysis.Text;
        var edits = new List<UiTextEdit>();
        var codeRanges = analysis.Ast?.Nodes.OfType<UiCodeBlock>()
            .Select(static b => (Start: b.CodeSpan.Start, End: b.CodeSpan.Start + b.CodeSpan.Length))
            .Concat(analysis.Ast.Nodes.OfType<UiStyleBlock>().Select(static b => (Start: b.CodeSpan.Start, End: b.CodeSpan.End)))
            .ToArray() ?? Array.Empty<(int Start, int End)>();

        var depth = 0;
        var offset = 0;
        while (offset <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', offset);
            if (lineEnd < 0) lineEnd = text.Length;
            var lineStart = offset;
            var contentStart = lineStart;
            while (contentStart < lineEnd && (text[contentStart] == ' ' || text[contentStart] == '\t'))
                contentStart++;
            var trimmed = contentStart < lineEnd ? text.Substring(contentStart, lineEnd - contentStart).Trim() : "";

            var inCode = codeRanges.Any(r => contentStart >= r.Start && contentStart < r.End);
            if (!inCode && trimmed.Length > 0)
            {
                if (trimmed.StartsWith("</", StringComparison.Ordinal) ||
                    trimmed.StartsWith("}", StringComparison.Ordinal) ||
                    trimmed.StartsWith(")", StringComparison.Ordinal))
                    depth = Math.Max(0, depth - 1);
                var expected = new string(' ', depth * 4);
                var current = text.Substring(lineStart, contentStart - lineStart);
                if (!string.Equals(current, expected, StringComparison.Ordinal))
                    edits.Add(new UiTextEdit(new UiSpan(lineStart, contentStart - lineStart), expected));
                if (trimmed.StartsWith("<", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("</", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("<!", StringComparison.Ordinal) &&
                    !trimmed.EndsWith("/>", StringComparison.Ordinal))
                    depth++;
                if (trimmed.EndsWith("{", StringComparison.Ordinal)) depth++;
            }

            if (lineEnd == text.Length) break;
            offset = lineEnd + 1;
        }

        if (analysis.Ast is not null)
        {
            foreach (var block in analysis.Ast.Nodes.OfType<UiCodeBlock>())
            {
                var formatted = FormatCode(block.Code);
                if (formatted is null || string.Equals(formatted, block.Code, StringComparison.Ordinal)) continue;
                edits.Add(new UiTextEdit(
                    new UiSpan(block.CodeSpan.Start, block.CodeSpan.Length), formatted));
            }
        }
        return edits;
    }

    private static string? FormatCode(string body)
    {
        try
        {
            using var workspace = new AdhocWorkspace();
            var wrapped = "class __BubuUiFormat\n{\n" + body + "\n}";
            var tree = CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(wrapped));
            var formatted = Formatter.Format(tree.GetRoot(), workspace).ToFullString();
            var open = formatted.IndexOf('{');
            var close = formatted.LastIndexOf('}');
            if (open < 0 || close <= open) return null;
            return formatted.Substring(open + 1, close - open - 1).Trim('\r', '\n');
        }
        catch (Exception)
        {
            return null;
        }
    }
}
