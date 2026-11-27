using System;
using System.Collections.Generic;
using System.Linq;
using BubuUI.Compiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BubuUI.Compiler.Tests;

[CollectionDefinition(CatalogPerformanceCollection.Name, DisableParallelization = true)]
public sealed class CatalogPerformanceCollection
{
    public const string Name = "Catalog performance";
}

// A timing budget must run without other test collections consuming the same process's CPU.
// Only this performance test is isolated; ordinary generator correctness tests stay parallel.
[Collection(CatalogPerformanceCollection.Name)]
public class CatalogPerformanceTests
{
    private const string Fx = NamespaceDeclarationFixture.SyntheticNamespace;

    private static CSharpCompilation CreateCompilation(string codeBehind)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(UiObject).Assembly.Location));
        return CSharpCompilation.Create(
            "TemplateProbe",
            new[] { CSharpSyntaxTree.ParseText(codeBehind) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// 「每轮重建目录」的代价必须是可承受的数字（§14.10.3 禁止用手工缓存换性能）。
    /// 探针编译引用了整个测试输出目录的程序集（比真实应用工程更宽），所以这里是悲观上界。
    /// </summary>
    /// <remarks>
    /// 墙钟在 <c>dotnet test</c> 并行跑多个测试程序集时会被吞掉——同一份代码实测
    /// 5.6 ms/轮（独占）对 20.7 ms/轮（四个测试程序集同时跑）。这是同核共享带宽/turbo 的
    /// 系统性差异，不是可以靠多测几轮消除的噪声，所以：
    /// ① 样本先按「进程真的拿到了 CPU」过滤（CPU/墙钟 &lt; 0.8 说明核被别的测试进程抢走）；
    /// ② 干净样本的目标档取 40 ms——仍能抓住数量级回归（独占基线 5.6 ms 的 7 倍），
    ///    而对全量并行负载下的 20.7 ms 留出余量；
    /// ③ 一个干净样本都取不到时退到 100 ms 硬上限（design 的预算值）。
    /// </remarks>
    [Fact]
    public void Catalog_Rebuild_Is_Measured_And_Bounded()
    {
        const int rounds = 20;
        const double targetMs = 40;
        const double ceilingMs = 100;
        var compilation = CreateCompilation($"namespace {Fx}; public partial class Probe {{ public int a = 1; }}");
        var baseline = UiCatalogFactory.Build(compilation);

        var process = System.Diagnostics.Process.GetCurrentProcess();
        var clean = new List<double>();
        var best = double.MaxValue;
        for (var attempt = 0; attempt < 10 && clean.Count < 3 && best >= targetMs; attempt++)
        {
            var cpuBefore = process.TotalProcessorTime;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var round = 0; round < rounds; round++)
                Assert.True(UiCatalogFactory.Build(compilation).Equals(baseline), $"round {round} differs");
            var wallMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started)
                         / (double)System.Diagnostics.Stopwatch.Frequency * 1000;
            var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;

            best = Math.Min(best, wallMs / rounds);
            if (cpuMs >= wallMs * 0.8) clean.Add(wallMs / rounds);
        }

        var message = $"catalog rebuild {(clean.Count > 0 ? clean.Min() : best):0.00} ms per round " +
                      $"(best {best:0.00} ms, {clean.Count} clean of 10 attempts) over {rounds} rounds " +
                      $"for {baseline.Elements.Count} tags";
        Assert.True(clean.Count > 0 ? clean.Min() < targetMs : best < ceilingMs, message);
    }

}
