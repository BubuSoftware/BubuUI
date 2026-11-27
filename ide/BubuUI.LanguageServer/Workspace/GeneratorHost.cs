extern alias euiGen;

using System.Collections.Immutable;
using BubuUI.LanguageServer.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using UiSpan = BubuUI.Compiler.TextSpan;

namespace BubuUI.LanguageServer.Workspace;

/// <summary>L3 生成器执行结果。</summary>
public sealed record GeneratorResult(
    Compilation Compilation,
    IReadOnlyList<SyntaxTree> GeneratedTrees,
    ImmutableArray<Diagnostic> Diagnostics,
    IReadOnlyDictionary<string, GeneratedDocument> GeneratedDocuments);

/// <summary>
/// L3（design/06 §6.9.3）：在 IDE 进程里跑**真的** <c>TemplateGenerator</c>。
///
/// 生成器把 Compiler/Metadata/Probe 源码并入自己程序集，直接引用会 CS0433 类型撞车，
/// 因此 ProjectReference 挂 <c>Aliases="euiGen"</c>，只在这个文件顶部用 extern alias；
/// 公开签名只有 <c>Initialize(IncrementalGeneratorInitializationContext)</c>，不跨界传
/// Compiler 类型，别名足够，不必拆工程（R2，2026-11-11 spike 实测通过）。
///
/// 同时喂 <c>build_property.rootnamespace</c>（生成器唯一读的 analyzer 选项）与
/// AdditionalTexts = 项目求值的 .bui + .bss。生成树加回编译后再 Build 目录，
/// 于是 .bui 派生组件（Counter/TaskList/…）未 build 时也自然在目录里。
///
/// design/18 §5.2：跑驱动期间开生成器旁路窗口（<c>IdeSourceMappingSideband</c>），
/// 发射器在写 #line 的同时记录 SourceMapping；跑完按 .bui 路径 Take，与 hint name
/// 对应的生成树配对成 <see cref="GeneratedDocument"/>。项目会话保留增量驱动；未重新发射的
/// 文件只有在原文与生成树均未变时才复用旧映射。旁路 API 不跨 alias 传 Roslyn 类型。
/// </summary>
public static class GeneratorHost
{
    // 旁路收集器是进程级静态、按 .bui 路径键控：两个并发 Run 会互相覆盖条目、
    // Take 走对方的映射（甚至配错生成树）。整个「开窗口 → 跑驱动 → Take」必须串行。
    private static readonly object RunGate = new();

    public static GeneratorResult Run(
        Compilation compilation,
        IEnumerable<(string Path, string Text)> additionalFiles,
        CSharpParseOptions parseOptions,
        string rootNamespace,
        Session? session = null)
    {
        lock (RunGate) return RunCore(compilation, additionalFiles, parseOptions, rootNamespace, session ?? new Session());
    }

    public sealed class Session
    {
        internal GeneratorDriver? Driver;
        internal CSharpParseOptions? Options;
        internal string? RootNamespace;
        internal Dictionary<string, (string Text, AdditionalText Input)> Inputs = new(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyDictionary<string, GeneratedDocument> Documents = new Dictionary<string, GeneratedDocument>();
    }

    private static GeneratorResult RunCore(
        Compilation compilation,
        IEnumerable<(string Path, string Text)> additionalFiles,
        CSharpParseOptions parseOptions,
        string rootNamespace,
        Session session)
    {
        var files = additionalFiles
            .Where(static f => Path.GetExtension(f.Path) is ".bui" or ".bss")
            .ToArray();
        var inputs = files.ToDictionary(f => f.Path,
            f => (Text: f.Text, Input: session.Inputs.TryGetValue(f.Path, out var previous) && previous.Text == f.Text
                ? previous.Input : (AdditionalText)new InMemoryAdditionalText(f.Path, f.Text)), StringComparer.OrdinalIgnoreCase);
        var texts = inputs.Values.Select(f => f.Input).ToImmutableArray();

        var provider = new FixedOptionsProvider(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_property.rootnamespace"] = rootNamespace,
        });

        GeneratorDriver driver = session.Driver is not null && Equals(session.Options, parseOptions)
            && session.RootNamespace == rootNamespace
            ? session.Driver.ReplaceAdditionalTexts(texts)
            : CSharpGeneratorDriver.Create(
            new ISourceGenerator[]
            {
                new euiGen::BubuUI.Compiler.Generator.TemplateGenerator().AsSourceGenerator(),
            },
            texts, parseOptions, provider);

        Compilation output;
        ImmutableArray<Diagnostic> diagnostics;
        using (euiGen::BubuUI.Compiler.Generator.IdeSourceMappingSideband.Enable())
        {
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out output, out diagnostics);
        }
        var input = new HashSet<SyntaxTree>(compilation.SyntaxTrees);
        var generated = output.SyntaxTrees.Where(tree => !input.Contains(tree)).ToArray();

        var documents = new Dictionary<string, GeneratedDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (Path.GetExtension(file.Path) != ".bui") continue;
            var sideband = euiGen::BubuUI.Compiler.Generator.IdeSourceMappingSideband.Take(file.Path);
            if (sideband is null)
            {
                // Cached source outputs do not re-enter the sideband. Never retain a
                // mapping for a changed/deleted/erroring file or a changed generated tree.
                var fullPath = Path.GetFullPath(file.Path);
                if (session.Inputs.TryGetValue(file.Path, out var oldInput) && oldInput.Text == file.Text
                    && session.Documents.TryGetValue(fullPath, out var previous))
                {
                    var cachedTree = generated.FirstOrDefault(t => t.FilePath == previous.Tree.FilePath);
                    if (cachedTree is not null && cachedTree.GetText().ContentEquals(previous.Tree.GetText()))
                        documents[fullPath] = previous with { Tree = cachedTree };
                }
                continue;
            }
            // Roslyn 4.9.2 的生成树 FilePath 带生成器前缀（程序集名\生成器类型\hintName），
            // 只有末段才是 AddSource 的 hint name——按末段匹配。
            var tree = generated.FirstOrDefault(t =>
                string.Equals(Path.GetFileName(t.FilePath), sideband.HintName, StringComparison.Ordinal));
            if (tree is null) continue;
            var mappings = sideband.Mappings.Select(static m => new SourceMapping(
                new UiSpan(m.OriginalStart, m.OriginalLength),
                new UiSpan(m.GeneratedStart, m.GeneratedLength),
                m.Verbatim)).ToArray();
            documents[Path.GetFullPath(file.Path)] = new GeneratedDocument(
                sideband.HintName, tree, DocumentMappingService.Create(mappings));
        }
        session.Driver = driver;
        session.Options = parseOptions;
        session.RootNamespace = rootNamespace;
        session.Inputs = inputs;
        session.Documents = documents;
        return new GeneratorResult(output, generated, diagnostics, documents);
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
