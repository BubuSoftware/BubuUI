using EclipseUI.Compiler;
using EclipseUI.Compiler.Probe;
using Microsoft.CodeAnalysis;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// 表达式环境：光标外围的 <c>@foreach</c> 变量链（<see cref="CursorContext.OpenForeaches"/>，
/// 外层在前）→ (类型, 名字) 注入列表。item 类型不是猜的——把集合表达式交给
/// <see cref="ExpressionProbe.InferType"/>，与编译期 EUI035 的推断同一台机器。
/// 外层必须先行：内层集合表达式（如 <c>group.Items</c>）的解析依赖外层 item 已在环境里。
/// 推断失败的层直接丢弃（补全少一层变量，好过给出错误类型）。
/// </summary>
public static class ExpressionEnvironment
{
    public static List<(ITypeSymbol Type, string Name)> Build(
        UiDocument document, IReadOnlyList<ForeachScope> scopes,
        ExpressionProbe probe, Compilation compilation)
    {
        var env = new List<(ITypeSymbol Type, string Name)>();
        foreach (var scope in scopes)
        {
            if (scope.VarName.Length == 0 || scope.Collection.Trim().Length == 0) continue;
            var collectionType = probe.InferType(scope.Collection, env);
            if (collectionType is null) continue;
            var itemType = ExpressionProbe.ItemTypeOf(collectionType, compilation);
            if (itemType is null || itemType.TypeKind == TypeKind.Error || itemType is IDynamicTypeSymbol)
                continue;
            env.Add((itemType, scope.VarName));
        }
        return env;
    }
}
