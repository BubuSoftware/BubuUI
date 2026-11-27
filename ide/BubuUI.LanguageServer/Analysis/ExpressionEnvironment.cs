using BubuUI.Compiler;
using BubuUI.Compiler.Probe;
using Microsoft.CodeAnalysis;

namespace BubuUI.LanguageServer.Analysis;

/// <summary>
/// 表达式环境：光标外围的 <c>@foreach</c> 变量链（<see cref="CursorContext.OpenForeaches"/>，
/// 外层在前）→ (类型, 名字) 注入列表。item 类型不是猜的——把集合表达式交给
/// <see cref="ExpressionProbe.InferType"/>，与编译期 EUI035 的推断同一台机器。
/// 外层必须先行：内层集合表达式（如 <c>group.Items</c>）的解析依赖外层 item 已在环境里。
/// 推断失败的层直接丢弃（补全少一层变量，好过给出错误类型）。
/// </summary>
public static class ExpressionEnvironment
{
    /// <summary>单层解析结果：注入 / 同名遮蔽跳过 / 推断失败——三者语义不同，不能合并。</summary>
    private enum ScopeOutcome { Added, Skipped, Failed }

    public static List<(ITypeSymbol Type, string Name)> Build(
        UiDocument document, IReadOnlyList<ForeachScope> scopes,
        ExpressionProbe probe, Compilation compilation)
    {
        var env = new List<(ITypeSymbol Type, string Name)>();
        for (var i = 0; i < scopes.Count; i++)
        {
            var (outcome, entry) = ResolveScope(scopes, i, probe, compilation, env);
            if (outcome == ScopeOutcome.Added) env.Add(entry);
        }
        return env;
    }

    /// <summary>
    /// 与 <see cref="Build"/> 同一路径，但**任何一层推断失败都判整体失败**（env 为空）。
    /// 重命名引用扫描用它：环境缺一个名字，裸标识符就会绑到别的成员上——宁可跳过
    /// 该表达式，也不给错引用。同名遮蔽的跳过**不算**失败（只注入内层，绑定语义不变）。
    /// </summary>
    public static bool TryBuild(
        UiDocument document, IReadOnlyList<ForeachScope> scopes,
        ExpressionProbe probe, Compilation compilation,
        out List<(ITypeSymbol Type, string Name)> env)
    {
        env = new List<(ITypeSymbol Type, string Name)>();
        for (var i = 0; i < scopes.Count; i++)
        {
            var (outcome, entry) = ResolveScope(scopes, i, probe, compilation, env);
            switch (outcome)
            {
                case ScopeOutcome.Added:
                    env.Add(entry);
                    break;
                case ScopeOutcome.Skipped:
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 推断第 <paramref name="index"/> 层。<see cref="ScopeOutcome.Skipped"/> 是同名遮蔽的
    /// 外层：C# 裸名绑定最内层，只注入内层——既保住绑定语义，也避免探针里重复声明
    /// CS0128 弄坏探针树。<see cref="ScopeOutcome.Failed"/> = 头解析不了 / 集合推不出类型 /
    /// 元素类型不是引用形态。
    /// </summary>
    private static (ScopeOutcome Outcome, (ITypeSymbol Type, string Name) Entry) ResolveScope(
        IReadOnlyList<ForeachScope> scopes, int index,
        ExpressionProbe probe, Compilation compilation,
        List<(ITypeSymbol Type, string Name)> env)
    {
        var scope = scopes[index];
        if (scope.VarName.Length == 0 || scope.Collection.Trim().Length == 0)
            return (ScopeOutcome.Failed, default);
        for (var j = index + 1; j < scopes.Count; j++)
            if (scopes[j].VarName == scope.VarName)
                return (ScopeOutcome.Skipped, default);

        var collectionType = probe.InferType(scope.Collection, env);
        if (collectionType is null) return (ScopeOutcome.Failed, default);
        var itemType = ExpressionProbe.ItemTypeOf(collectionType, compilation);
        if (itemType is null || itemType.TypeKind == TypeKind.Error || itemType is IDynamicTypeSymbol)
            return (ScopeOutcome.Failed, default);
        return (ScopeOutcome.Added, (itemType, scope.VarName));
    }
}
