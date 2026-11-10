using EclipseUI.Compiler;
using EclipseUI.Compiler.Probe;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// 跳转定义（design/06 §6.3 L3 第一片）：<c>{expr}</c> 内光标处的标识符 → 探针符号反查
/// （<see cref="ExpressionProbe.Locate"/>，与悬停/补全同一台机器）→ 落点二选一：
/// <c>@foreach</c> 迭代变量跳回本文档的 foreach 头；code-behind 成员跳到兄弟
/// <c>{Name}.cs</c>（spec/02 §2.2 文件契约）里的声明名字。<c>@code</c> 成员的声明在
/// 合成树里，没有可去的真实文件——返回 null 而不是错位置（design/06 的"不猜"口径）。
/// </summary>
public static class DefinitionProvider
{
    public static UiDefinition? Definition(DocumentAnalysis analysis, int offset)
    {
        var context = CursorScanner.Scan(analysis.Text, offset);
        if (context.Kind != CursorKind.Expression || context.ExpressionStart < 0) return null;

        var probe = analysis.Probe;
        if (probe is null) return null;

        // 表达式要取**完整的**（ExpressionEnd）：光标在 token 中间时（task.Na|me），
        // 按 offset 截断会得到 task.Na——绑不出符号。
        var end = context.ExpressionEnd < 0 ? offset : context.ExpressionEnd;
        if (end <= context.ExpressionStart) return null;
        var expression = analysis.Text.Substring(context.ExpressionStart, end - context.ExpressionStart);
        var cursor = Math.Clamp(offset - context.ExpressionStart, 0, expression.Length);

        var env = ExpressionEnvironment.Build(analysis.Ast!, context.OpenForeaches, probe, probe.Compilation);
        var symbol = probe.Locate(expression, cursor, env);
        if (symbol is null) return null;

        // 1) @foreach 迭代变量：定义就在本文档（foreach 头里的变量名）。
        if (symbol is ILocalSymbol local)
        {
            var scope = context.OpenForeaches.FirstOrDefault(
                f => f.VarName == local.Name && f.NameOffset >= 0);
            if (scope is not null)
                return new UiDefinition(null, new TextSpan(scope.NameOffset, scope.VarName.Length), local.Name);
        }

        // 2) code-behind 成员：声明位置必须落在兄弟文件那棵树里。
        var location = symbol.Locations.FirstOrDefault(static l => l.IsInSource);
        if (location?.SourceTree is null || !analysis.IsCodeBehindTree(location.SourceTree)) return null;
        if (analysis.CodeBehindPath is null) return null;

        return new UiDefinition(analysis.CodeBehindPath, NameSpan(location), symbol.Name);
    }

    /// <summary>声明节点的名字区间（导航落在标识符上，而不是整个属性/方法声明）。</summary>
    private static TextSpan NameSpan(Location location)
    {
        var node = location.SourceTree!.GetRoot().FindNode(location.SourceSpan);
        TextSpan Of(Microsoft.CodeAnalysis.Text.TextSpan span) => new(span.Start, span.Length);
        return node switch
        {
            PropertyDeclarationSyntax property => Of(property.Identifier.Span),
            MethodDeclarationSyntax method => Of(method.Identifier.Span),
            FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault() is { } variable
                ? Of(variable.Identifier.Span)
                : Of(node.Span),
            EventDeclarationSyntax @event => Of(@event.Identifier.Span),
            TypeDeclarationSyntax type => Of(type.Identifier.Span),
            ParameterSyntax parameter => Of(parameter.Identifier.Span),
            _ => Of(node.Span),
        };
    }
}
