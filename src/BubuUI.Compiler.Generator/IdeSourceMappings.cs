using System;
using System.Collections.Generic;
using System.Threading;

namespace BubuUI.Compiler.Generator;

/// <summary>
/// 一条 original↔generated 区间映射（design/18 §5.2）。**只用 BCL 类型**：LSP 经
/// extern alias 引用生成器程序集（Compiler 源码并入导致 Roslyn 类型双重身份），
/// 跨界 API 出现 Roslyn 类型会 CS0433。
/// </summary>
public sealed class IdeSourceMapping
{
    public IdeSourceMapping(int originalStart, int originalLength,
        int generatedStart, int generatedLength, bool verbatim)
    {
        OriginalStart = originalStart;
        OriginalLength = originalLength;
        GeneratedStart = generatedStart;
        GeneratedLength = generatedLength;
        Verbatim = verbatim;
    }

    public int OriginalStart { get; }
    public int OriginalLength { get; }
    public int GeneratedStart { get; }
    public int GeneratedLength { get; }

    /// <summary>生成文本与原文逐字对应（长度相等）：子区间可按偏移差双向换算；
    /// false = 语句级映射（生成代码包了 cast/lambda/前缀），只支持整段对应。</summary>
    public bool Verbatim { get; }
}

/// <summary>一个 .bui 的旁路产物：生成树 hint name + 映射表。</summary>
public sealed class IdeGeneratedSideband
{
    public IdeGeneratedSideband(string hintName, IdeSourceMapping[] mappings)
    {
        HintName = hintName;
        Mappings = mappings;
    }

    public string HintName { get; }
    public IdeSourceMapping[] Mappings { get; }
}

/// <summary>
/// IDE 专用旁路（design/18 §10.3 允许的「只增加元数据」通道）。Roslyn 4.9.2 没有公开的
/// RegisterHostOutput（4.12+ 才有），生成器管道又刻意全静态 lambda，实例收集器挂不进去——
/// 所以用**静态 opting-in 收集器**：只有 IDE 的 GeneratorHost 在跑驱动前 <see cref="Enable"/>，
/// 发射器才记录映射；build 路径 <see cref="IsActive"/> 恒 false，零开销、零行为变化。
///
/// 键是 .bui 文件路径。GeneratorHost 每次 Run 都新建驱动 → 全部文件必然重新发射，
/// Take 拿到的就是本轮产物，不需要再跑一遍生成器做 parity 校验。
/// </summary>
public static class IdeSourceMappingSideband
{
    private static int _enableCount;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>开一个收集窗口（引用计数，可嵌套）；Dispose 关闭。窗口外的发射不记录。</summary>
    public static IDisposable Enable()
    {
        Interlocked.Increment(ref _enableCount);
        return new Window();
    }

    public static bool IsActive => Volatile.Read(ref _enableCount) > 0;

    /// <summary>取走并移除一个 .bui 的本轮旁路产物；没发射（错误短路）或没开窗口时 null。</summary>
    public static IdeGeneratedSideband? Take(string uiFilePath)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(uiFilePath, out var entry)) return null;
            Entries.Remove(uiFilePath);
            return entry.HintName is null || entry.Mappings is null
                ? null
                : new IdeGeneratedSideband(entry.HintName, entry.Mappings);
        }
    }

    internal static void Record(string uiFilePath, IdeSourceMapping[] mappings)
    {
        if (!IsActive) return;
        lock (Gate)
        {
            if (!Entries.TryGetValue(uiFilePath, out var entry))
                Entries[uiFilePath] = entry = new Entry();
            entry.Mappings = mappings;
        }
    }

    internal static void SetHintName(string uiFilePath, string hintName)
    {
        if (!IsActive) return;
        lock (Gate)
        {
            if (!Entries.TryGetValue(uiFilePath, out var entry))
                Entries[uiFilePath] = entry = new Entry();
            entry.HintName = hintName;
        }
    }

    private sealed class Entry
    {
        public string? HintName;
        public IdeSourceMapping[]? Mappings;
    }

    private sealed class Window : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _enableCount);
        }
    }
}