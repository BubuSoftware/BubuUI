using EclipseUI.Compiler;
using Microsoft.CodeAnalysis;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// 一条 original↔generated 区间映射（design/18 §5.2，与 Razor SourceMapping 同构）。
/// <see cref="Verbatim"/> = 生成文本与原文逐字对应（表达式级/@code 块）：子区间可按偏移差
/// 双向换算；false = 语句级（生成代码包了 cast/lambda/前缀），只支持整段对应。
/// </summary>
public sealed record SourceMapping(TextSpan Original, TextSpan Generated, bool Verbatim);

/// <summary>
/// 一个 .ui 的生成文档（design/18 §5.2）：hint name + 语法树（已在项目 Compilation 里，
/// 阶段 2 起作为 Roslyn Document 的载体）+ 双向映射表。
/// </summary>
public sealed record GeneratedDocument(string HintName, SyntaxTree Tree, DocumentMappingService Mappings);

/// <summary>
/// 统一的双向映射服务（design/18 §5.2）：编辑类功能（补全 textEdit、悬停 range、重命名
/// WorkspaceEdit、代码修复）的区间换算唯一入口。诊断仍走 #line（GetMappedLineSpan），
/// 两条通道分工不混用。
///
/// 规则（与 Razor 同口径）：**只做完整包含映射**；跨边界、部分重叠、生成独有代码一律
/// 返回 false，调用方丢弃该项/编辑，绝不近似。两个方向各按起点排序 + 二分查找。
///
/// 发射不变量：记录的 original 区间两两**不相交或完全相同**（bind 的 readSource/writeSource
/// 两次出现共享同一 original；generated 区间则恒不相交）。据此：
/// - 正向命中多个**不同** original = 歧义 → false（零宽边界点取排序首个，确定性）；
/// - 同一 original 的多条映射优先取 <see cref="SourceMapping.Verbatim"/>（语句级映射的
///   子区间位置不对应，光标级查询不能选它），再取发射序首个；
/// - 反向（generated → original）区间天然唯一，子区间仅 Verbatim 可换算。
/// </summary>
public sealed class DocumentMappingService
{
    public static readonly DocumentMappingService Empty = new(Array.Empty<SourceMapping>());

    private readonly SourceMapping[] _byOriginal;
    private readonly SourceMapping[] _byGenerated;

    private DocumentMappingService(SourceMapping[] mappings)
    {
        Mappings = mappings;
        // OrderBy 是稳定排序：同起点映射保留发射顺序，「取首个」才是确定性的。
        _byOriginal = mappings.OrderBy(static m => m.Original.Start).ToArray();
        _byGenerated = mappings.OrderBy(static m => m.Generated.Start).ToArray();
    }

    public static DocumentMappingService Create(IEnumerable<SourceMapping> mappings) =>
        new(mappings.ToArray());

    public IReadOnlyList<SourceMapping> Mappings { get; }

    /// <summary>.ui 区间 → 生成 C# 区间；不可完整映射返回 false。</summary>
    public bool TryMapToGenerated(TextSpan original, out TextSpan generated)
    {
        generated = default;
        var candidates = Containing(_byOriginal, original, byOriginal: true);
        if (candidates.Count == 0) return false;
        if (original.Length > 0 && candidates.Any(c => c.Original != candidates[0].Original))
            return false;
        var pick = candidates.FirstOrDefault(static c => c.Verbatim) ?? candidates[0];
        if (original == pick.Original)
        {
            generated = pick.Generated;
            return true;
        }
        if (!pick.Verbatim) return false;
        generated = new TextSpan(pick.Generated.Start + (original.Start - pick.Original.Start), original.Length);
        return true;
    }

    /// <summary>.ui 光标偏移 → 生成 C# 偏移（零宽查询）；落在生成独有代码里返回 false。</summary>
    public bool TryMapPositionToGenerated(int originalOffset, out int generatedOffset)
    {
        if (TryMapToGenerated(new TextSpan(originalOffset, 0), out var generated))
        {
            generatedOffset = generated.Start;
            return true;
        }
        generatedOffset = 0;
        return false;
    }

    /// <summary>生成 C# 区间 → .ui 区间；不可完整映射返回 false。</summary>
    public bool TryMapToOriginal(TextSpan generated, out TextSpan original)
    {
        original = default;
        var candidates = Containing(_byGenerated, generated, byOriginal: false);
        if (candidates.Count == 0) return false;
        if (generated.Length > 0 && candidates.Any(c => c.Generated != candidates[0].Generated))
            return false;
        var pick = candidates[0];
        if (generated == pick.Generated)
        {
            original = pick.Original;
            return true;
        }
        if (!pick.Verbatim) return false;
        original = new TextSpan(pick.Original.Start + (generated.Start - pick.Generated.Start), generated.Length);
        return true;
    }

    /// <summary>生成 C# 区间 → .ui 区间（重命名/代码修复的编辑回映入口，语义同 TryMapToOriginal）。</summary>
    public bool TryMapRangeToOriginal(TextSpan generated, out TextSpan original) =>
        TryMapToOriginal(generated, out original);

    /// <summary>
    /// 二分找起点 ≤ query.Start 的最右区间，再向左收集完整包含者。区间不相交或全同
    /// （发射不变量），遇到首个不包含者即可停：更左的区间 End 只会更小。
    /// 返回按数组序（发射序）排列。
    /// </summary>
    private static List<SourceMapping> Containing(SourceMapping[] sorted, TextSpan query, bool byOriginal)
    {
        var result = new List<SourceMapping>();
        int lo = 0, hi = sorted.Length - 1, index = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (SpanOf(sorted[mid], byOriginal).Start <= query.Start)
            {
                index = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        for (var i = index; i >= 0; i--)
        {
            var span = SpanOf(sorted[i], byOriginal);
            if (span.Start <= query.Start && query.End <= span.End)
                result.Add(sorted[i]);
            else
                break;
        }
        result.Reverse();
        return result;
    }

    private static TextSpan SpanOf(SourceMapping mapping, bool byOriginal) =>
        byOriginal ? mapping.Original : mapping.Generated;
}