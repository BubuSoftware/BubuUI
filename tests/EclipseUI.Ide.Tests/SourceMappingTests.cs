using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EclipseUI.Ide.Tests;

/// <summary>
/// design/18 §5.2 / §7 阶段 0 验收：SourceMapping 双向 round-trip。
/// 三层：映射服务本身的合成用例 → 真生成器（GeneratorHost.Run）产出的映射 →
/// 仓库全部 Gallery .ui 夹具逐表达式 round-trip。生成文本/#line 不变由
/// Compiler.Tests 的 golden 契约钉住，这里只验证映射坐标。
/// </summary>
public class SourceMappingTests
{
    // GeneratorHost keys generated documents by the platform's absolute path.
    private static readonly string UiPath = Path.GetFullPath("/templates/Probe.ui");

    // ---- A. DocumentMappingService 合成用例 ----

    private static DocumentMappingService Service(params SourceMapping[] mappings) =>
        DocumentMappingService.Create(mappings);

    private static SourceMapping Verbatim(int origStart, int genStart, int length) =>
        new(new TextSpan(origStart, length), new TextSpan(genStart, length), true);

    [Fact]
    public void VerbatimMapping_RoundTripsExactAndSubSpan()
    {
        var service = Service(Verbatim(10, 100, 8));
        Assert.True(service.TryMapToGenerated(new TextSpan(10, 8), out var generated));
        Assert.Equal(new TextSpan(100, 8), generated);
        Assert.True(service.TryMapToOriginal(generated, out var back));
        Assert.Equal(new TextSpan(10, 8), back);

        // 子区间按偏移差换算（表达式里的一个标识符）。
        Assert.True(service.TryMapToGenerated(new TextSpan(12, 3), out var sub));
        Assert.Equal(new TextSpan(102, 3), sub);
        Assert.True(service.TryMapToOriginal(sub, out var subBack));
        Assert.Equal(new TextSpan(12, 3), subBack);

        Assert.True(service.TryMapPositionToGenerated(15, out var position));
        Assert.Equal(105, position);
    }

    [Fact]
    public void StatementMapping_OnlyMapsTheWholeSpan()
    {
        // 语句级：original 8 字符，生成 20 字符（包了 cast/前缀）——子区间不猜。
        var service = Service(new SourceMapping(new TextSpan(10, 8), new TextSpan(100, 20), false));
        Assert.True(service.TryMapToGenerated(new TextSpan(10, 8), out var generated));
        Assert.Equal(new TextSpan(100, 20), generated);
        Assert.True(service.TryMapToOriginal(generated, out var back));
        Assert.Equal(new TextSpan(10, 8), back);

        Assert.False(service.TryMapToGenerated(new TextSpan(12, 3), out _));
        Assert.False(service.TryMapToOriginal(new TextSpan(102, 3), out _));
        Assert.False(service.TryMapPositionToGenerated(15, out _));
    }

    [Fact]
    public void DifferentOriginals_OverlapIsRejected()
    {
        // 两条不同 original 相互重叠（病态输入）：查询同时落在两者里 = 歧义，拒绝。
        var service = Service(Verbatim(10, 100, 10), Verbatim(12, 200, 10));
        Assert.False(service.TryMapToGenerated(new TextSpan(13, 5), out _));
    }

    [Fact]
    public void DuplicateOriginals_ForwardFirstReverseAll()
    {
        // bind 的 readSource/writeSource：同一 original，两处生成出现。
        var service = Service(Verbatim(10, 100, 7), Verbatim(10, 300, 7));
        Assert.True(service.TryMapToGenerated(new TextSpan(10, 7), out var forward));
        Assert.Equal(new TextSpan(100, 7), forward);
        Assert.True(service.TryMapToOriginal(new TextSpan(100, 7), out var back1));
        Assert.True(service.TryMapToOriginal(new TextSpan(300, 7), out var back2));
        Assert.Equal(new TextSpan(10, 7), back1);
        Assert.Equal(back1, back2);
    }

    [Fact]
    public void SameOriginal_ExpressionLevelWinsOverStatement()
    {
        // bind 回调头（语句级）与回调体内的表达式（1:1）共享 original：
        // 光标级查询必须选 1:1，否则落进生成前缀。
        var statement = new SourceMapping(new TextSpan(10, 5), new TextSpan(100, 30), false);
        var expression = Verbatim(10, 200, 5);
        var service = Service(statement, expression);
        Assert.True(service.TryMapPositionToGenerated(12, out var position));
        Assert.Equal(202, position);
        Assert.True(service.TryMapToGenerated(new TextSpan(10, 5), out var whole));
        Assert.Equal(new TextSpan(200, 5), whole);
    }

    [Fact]
    public void GeneratedOnlyAndCrossBoundary_ReturnFalse()
    {
        var service = Service(Verbatim(10, 100, 8));
        Assert.False(service.TryMapToOriginal(new TextSpan(50, 4), out _));   // 生成独有
        Assert.False(service.TryMapToOriginal(new TextSpan(96, 8), out _));   // 跨左边界
        Assert.False(service.TryMapToOriginal(new TextSpan(104, 8), out _));  // 跨右边界
        Assert.False(service.TryMapToGenerated(new TextSpan(5, 4), out _));   // 原文独有
        Assert.False(service.TryMapToGenerated(new TextSpan(16, 4), out _));  // 跨原文右边界
    }

    [Fact]
    public void ZeroWidthBoundaryPosition_IsDeterministic()
    {
        // 相邻表达式 {a}{b} 的共享边界点：取排序首个（确定性），不报歧义。
        var service = Service(Verbatim(10, 100, 1), Verbatim(11, 200, 1));
        Assert.True(service.TryMapPositionToGenerated(11, out var position));
        Assert.Equal(101, position);
    }

    // ---- B. 真生成器 round-trip ----

    private const string ComprehensiveUi = """
        @namespace ProbeApp
        @using EclipseUI.Elements

        <StackPanel Gap="8">
            <TextBlock FontSize={size}>Hello {name}, you have {count} items</TextBlock>
            <CheckBox @bind-Checked={visible}>toggle</CheckBox>
            @if (visible) {
                <Button OnClick={() => count++}>+1</Button>
            }
            @foreach (var item in items) {
                <TextBlock>{item}</TextBlock>
            }
        </StackPanel>

        @code {
            public int size = 12;
            public string name = "world";
            public int count;
            public bool visible;
            public System.Collections.Generic.List<string> items = new();
        }
        """;

    private static GeneratorResult Run(string ui)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(global::EclipseUI.UiObject).Assembly.Location));
        var compilation = CSharpCompilation.Create("MappingProbe",
            Array.Empty<SyntaxTree>(), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return GeneratorHost.Run(compilation, new[] { (Path: UiPath, Text: ui) },
            CSharpParseOptions.Default, "ProbeApp");
    }

    private static (int Start, int End) TrimmedBounds(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return (start, end);
    }

    /// <summary>验收 §7：每个 {expr} span 正向 → 文本逐字一致 → 反向回到原 span；光标位置同。</summary>
    private static void AssertExpressionsRoundTrip(string ui, GeneratedDocument document)
    {
        var generatedText = document.Tree.ToString();
        var mappings = document.Mappings;
        var expressions = CursorScanner.CollectExpressions(ui);
        Assert.NotEmpty(expressions);
        foreach (var expression in expressions)
        {
            var (start, end) = expression.Header is { } header
                ? (header.CollectionStart, header.CollectionEnd)
                : TrimmedBounds(ui, expression.Start, expression.End);
            var span = new TextSpan(start, end - start);
            var label = ui.Substring(start, end - start);
            Assert.True(mappings.TryMapToGenerated(span, out var generated),
                $"no forward mapping for '{label}'");
            Assert.Equal(label, generatedText.Substring(generated.Start, generated.Length));
            Assert.True(mappings.TryMapToOriginal(generated, out var back),
                $"no reverse mapping for '{label}'");
            Assert.Equal(span, back);

            Assert.True(mappings.TryMapPositionToGenerated(start, out var position),
                $"no cursor mapping for '{label}'");
            Assert.True(mappings.TryMapToOriginal(new TextSpan(position, 0), out var backPosition));
            Assert.Equal(start, backPosition.Start);
        }
    }

    private static void AssertCodeBlocksRoundTrip(string ui, GeneratedDocument document)
    {
        var generatedText = document.Tree.ToString();
        var mappings = document.Mappings;
        var ast = FrontEnd.Parse(ui, UiPath, new List<UiDiagnostic>());
        var blocks = ast.Nodes.OfType<UiCodeBlock>().ToList();
        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
        {
            Assert.True(mappings.TryMapToGenerated(block.CodeSpan, out var generated));
            Assert.Equal(block.Code, generatedText.Substring(generated.Start, generated.Length));
            Assert.True(mappings.TryMapToOriginal(generated, out var back));
            Assert.Equal(new TextSpan(block.CodeSpan.Start, block.Code.Length), back);

            // 块内子区间（成员声明里的标识符）双向换算。
            var index = block.Code.IndexOf("count", StringComparison.Ordinal);
            if (index < 0) continue;
            var sub = new TextSpan(block.CodeSpan.Start + index, 5);
            Assert.True(mappings.TryMapToGenerated(sub, out var generatedSub));
            Assert.Equal("count", generatedText.Substring(generatedSub.Start, generatedSub.Length));
            Assert.True(mappings.TryMapToOriginal(generatedSub, out var backSub));
            Assert.Equal(sub, backSub);
        }
    }

    [Fact]
    public void GeneratorExpressions_RoundTripThroughGeneratedDocument()
    {
        var result = Run(ComprehensiveUi);
        Assert.True(result.GeneratedDocuments.TryGetValue(UiPath, out var document));
        Assert.NotEmpty(document!.Mappings.Mappings);
        AssertExpressionsRoundTrip(ComprehensiveUi, document);
    }

    [Fact]
    public void GeneratorCodeBlocks_RoundTripIncludingSubSpans()
    {
        var result = Run(ComprehensiveUi);
        Assert.True(result.GeneratedDocuments.TryGetValue(UiPath, out var document));
        AssertCodeBlocksRoundTrip(ComprehensiveUi, document!);
    }

    [Fact]
    public void CrLfTemplate_RoundTrips()
    {
        var ui = ComprehensiveUi.Replace("\n", "\r\n");
        var result = Run(ui);
        Assert.True(result.GeneratedDocuments.TryGetValue(UiPath, out var document));
        AssertExpressionsRoundTrip(ui, document!);
        AssertCodeBlocksRoundTrip(ui, document!);
    }

    [Fact]
    public void GeneratedOnlyCode_HasNoOriginal()
    {
        var result = Run(ComprehensiveUi);
        Assert.True(result.GeneratedDocuments.TryGetValue(UiPath, out var document));
        var text = document!.Tree.ToString();
        var build = text.IndexOf("protected override void InitializeUi()", StringComparison.Ordinal);
        Assert.True(build >= 0);
        Assert.False(document.Mappings.TryMapToOriginal(new TextSpan(build, 8), out _));
        var track = text.IndexOf("Track(", StringComparison.Ordinal);
        Assert.True(track >= 0);
        Assert.False(document.Mappings.TryMapToOriginal(new TextSpan(track, 5), out _));
    }

    [Fact]
    public void BindExpression_BothOccurrencesMapBack()
    {
        var result = Run(ComprehensiveUi);
        Assert.True(result.GeneratedDocuments.TryGetValue(UiPath, out var document));
        var bind = ComprehensiveUi.IndexOf("@bind-Checked={", StringComparison.Ordinal) + "@bind-Checked={".Length;
        var span = new TextSpan(bind, "visible".Length);
        var occurrences = document!.Mappings.Mappings.Where(m => m.Original == span).ToArray();
        Assert.Equal(2, occurrences.Length);
        Assert.All(occurrences, m => Assert.True(m.Verbatim));
        foreach (var occurrence in occurrences)
        {
            Assert.True(document.Mappings.TryMapToOriginal(occurrence.Generated, out var back));
            Assert.Equal(span, back);
        }
    }

    [Fact]
    public void ErrorShortCircuit_HasNoGeneratedDocument()
    {
        // EUI001 未知标签 → 错误短路，不发射 → 没有旁路产物（Take 返回 null）。
        var result = Run("@namespace ProbeApp\n<NotARealTag />\n");
        Assert.False(result.GeneratedDocuments.ContainsKey(UiPath));
    }

    // ---- C. 当前 Gallery 与测试专用模板夹具 ----

    [Theory]
    [InlineData("samples/Gallery")]
    [InlineData("tests/GalleryFixtures")]
    public void GalleryFixtures_RoundTripAllExpressionsAndCodeBlocks(string relativeDirectory)
    {
        var root = FindRepositoryRoot();
        var workspace = new ProjectWorkspace(new CatalogOptions { ProbeRoot = root });
        var gallery = Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        var files = Directory.GetFiles(gallery, "*.ui");
        Assert.NotEmpty(files);
        foreach (var path in files)
        {
            var snapshot = Assert.Single(workspace.ResolveAll(path));
            Assert.True(snapshot.GeneratedDocuments.TryGetValue(Path.GetFullPath(path), out var document),
                $"no generated document for '{path}'");
            var ui = File.ReadAllText(path);
            AssertExpressionsRoundTrip(ui, document!);
            if (ui.Contains("@code", StringComparison.Ordinal))
                AssertCodeBlocksRoundTrip(ui, document!);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "EclipseUI.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("cannot locate EclipseUI.slnx from the test output directory");
    }
}
