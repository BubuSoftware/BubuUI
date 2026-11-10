extern alias euiGen;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace EclipseUI.LanguageServer.Workspace;

/// <summary>L3 生成器执行结果。</summary>
public sealed record GeneratorResult(
    Compilation Compilation,
    IReadOnlyList<SyntaxTree> GeneratedTrees,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// L3（design/06 §6.9.3）：在 IDE 进程里跑**真的** <c>TemplateGenerator</c>。
///
/// 生成器把 Compiler/Metadata/Probe 源码并入自己程序集，直接引用会 CS0433 类型撞车，
/// 因此 ProjectReference 挂 <c>Aliases="euiGen"</c>，只在这个文件顶部用 extern alias；
/// 公开签名只有 <c>Initialize(IncrementalGeneratorInitializationContext)</c>，不跨界传
/// Compiler 类型，别名足够，不必拆工程（R2，2026-11-11 spike 实测通过）。
///
/// 同时喂 <c>build_property.rootnamespace</c>（生成器唯一读的 analyzer 选项）与
/// AdditionalTexts = 项目求值的 .ui + .eustyle。生成树加回编译后再 Build 目录，
/// 于是 .ui 派生组件（Counter/TaskList/…）未 build 时也自然在目录里。
/// </summary>
public static class GeneratorHost
{
    public static GeneratorResult Run(
        Compilation compilation,
        IEnumerable<(string Path, string Text)> additionalFiles,
        CSharpParseOptions parseOptions,
        string rootNamespace)
    {
        var texts = additionalFiles
            .Where(static f => Path.GetExtension(f.Path) is ".ui" or ".eustyle")
            .Select(static f => (AdditionalText)new InMemoryAdditionalText(f.Path, f.Text))
            .ToArray();

        var provider = new FixedOptionsProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.rootnamespace"] = rootNamespace,
        });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ISourceGenerator[]
            {
                new euiGen::EclipseUI.Compiler.Generator.TemplateGenerator().AsSourceGenerator(),
            },
            texts, parseOptions, provider);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var input = new HashSet<SyntaxTree>(compilation.SyntaxTrees);
        var generated = output.SyntaxTrees.Where(tree => !input.Contains(tree)).ToArray();
        return new GeneratorResult(output, generated, diagnostics);
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    private sealed class FixedOptionsProvider : AnalyzerConfigOptionsProvider
    {
        public FixedOptionsProvider(Dictionary<string, string> values) => GlobalOptions = new FixedOptions(values);

        public override AnalyzerConfigOptions GlobalOptions { get; }

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;

        private sealed class FixedOptions : AnalyzerConfigOptions
        {
            private readonly Dictionary<string, string> _values;

            public FixedOptions(Dictionary<string, string> values) => _values = values;

            public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
        }
    }
}
