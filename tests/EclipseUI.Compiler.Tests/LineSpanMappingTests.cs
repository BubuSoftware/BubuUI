using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using EclipseUI.Compiler.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

/// <summary>
/// G3 `#line` 列映射（spec/02 §2.4，台账 D79）。前半固化 Roslyn 4.9.2 增强型 `#line span`
/// 指令的实测语义——列平移只作用于紧接的一行、end 元组不参与单行映射、start 不得晚于 end；
/// 后半守住房生成器：非法 span 会报 CS8939 并让整段映射失效，类型错误因此落回 .g.cs。
/// </summary>
public class LineSpanMappingTests
{
    private sealed class ProbeText : AdditionalText
    {
        private readonly SourceText _text;

        public ProbeText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }

    private static List<Diagnostic> Probe(string body)
    {
        var code = "class C { void M() {\n" + body + "\n} }";
        var tree = CSharpSyntaxTree.ParseText(code, path: "Gen.cs");
        var comp = CSharpCompilation.Create("probe", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        return comp.GetDiagnostics().Where(d => d.Id is "CS0103" or "CS8939").ToList();
    }

    [Fact]
    public void SpanDirective_ShiftsColumnsOnlyOnFirstMappedLine()
    {
        var errors = Probe("#line (5, 7) - (5, 12) \"P.ui\"\n        BadCall();\n        AlsoBad();\n#line hidden")
            .Where(d => d.Id == "CS0103").ToList();

        Assert.Equal(2, errors.Count);
        var first = errors[0].Location.GetMappedLineSpan();
        var second = errors[1].Location.GetMappedLineSpan();
        Assert.Equal("P.ui", first.Path);
        Assert.Equal(4, first.StartLinePosition.Line);        // 1-based 5
        Assert.Equal(14, first.StartLinePosition.Character);  // 1-based 15 = (7-1) + 生成列 9
        Assert.Equal(5, second.StartLinePosition.Line);      // 1-based 6：行号递增
        Assert.Equal(8, second.StartLinePosition.Character); // 1-based 9：列平移不延续到第二行
    }

    [Fact]
    public void SpanDirective_EndTupleDoesNotAffectSingleLineMapping()
    {
        static LinePosition Mapped(string directive)
        {
            var d = Assert.Single(Probe(directive + "\n        BadCall();\n#line hidden"), x => x.Id == "CS0103");
            return d.Location.GetMappedLineSpan().StartLinePosition;
        }

        Assert.Equal(
            Mapped("#line (5, 7) - (9, 3) \"P.ui\""),
            Mapped("#line (5, 7) - (5, 99) \"P.ui\""));
    }

    [Fact]
    public void SpanDirective_StartAfterEnd_IsCS8939_AndDropsTheMapping()
    {
        var diags = Probe("#line (5, 20) - (5, 12) \"P.ui\"\n        BadCall();\n#line hidden");

        Assert.Contains(diags, d => d.Id == "CS8939");
        var error = Assert.Single(diags, d => d.Id == "CS0103");
        Assert.Equal("Gen.cs", error.Location.GetMappedLineSpan().Path); // 映射整体失效
    }

    private static (string Code, Compilation Output) Generate(string uiPath, string ui, string codeBehind)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Component).Assembly.Location));
        var compilation = CSharpCompilation.Create("LineSpanProbe",
            new[] { CSharpSyntaxTree.ParseText(codeBehind) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new ProbeText(uiPath, ui) });
        var updated = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var code = updated.GetRunResult().Results.Single().GeneratedSources.Single().SourceText.ToString();
        return (code, output);
    }

    [Fact]
    public void GeneratedCode_EmitsOnlyValidSpanDirectives_AndNoCS8939()
    {
        const string uiPath = "C:\\templates\\Probe.ui";
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Gap={gap}>
                <TextBlock>{message}</TextBlock>
                @if (flag) {
                    <Button OnClick={() => Bump()}>go</Button>
                }
            </StackPanel>
            """;
        const string codeBehind = """
            public partial class Probe
            {
                public double gap = 4;
                public string message = "start";
                public bool flag = true;
                private void Bump() { }
            }
            """;

        var (code, output) = Generate(uiPath, ui, codeBehind);

        Assert.Contains("#line (", code);
        Assert.DoesNotMatch(@"(?m)^#line \d+ ", code); // 已全部换成 span 型
        var spans = Regex.Matches(code, @"#line \((\d+), (\d+)\) - \((\d+), (\d+)\)");
        Assert.NotEmpty(spans);
        foreach (Match m in spans)
        {
            var sl = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var sc = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var el = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var ec = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            Assert.True(sc >= 1 && ec >= 1, m.Value);
            Assert.True(el > sl || (el == sl && ec > sc), $"CS8939 (start 不得晚于 end): {m.Value}");
        }
        Assert.DoesNotContain(output.GetDiagnostics(), d => d.Id is "CS8939" or "CS1576");
    }

    /// <summary>
    /// 表达式在 .ui 里足够靠右时（生成前缀短于源列），列平移不再被钳制到 1，
    /// C# 类型错误能精确落回该表达式的列；表达式靠左时按 §2.4 的口径退化为行映射。
    /// </summary>
    [Fact]
    public void TypeError_MapsBackToExactColumn_WhenExpressionStartsFarRight()
    {
        const string uiPath = "C:\\templates\\Probe.ui";
        const string pad = "                                        "; // 40 空格
        const string prefix = "<StackPanel Gap={";
        var ui = "@using EclipseUI.Elements\n" + pad + prefix + "gap}>\n" + pad + "    <TextBlock>{message}</TextBlock>\n" + pad + "</StackPanel>";
        const string codeBehind = """
            public partial class Probe
            {
                public string gap = "oops";
                public string message = "start";
            }
            """;

        var (_, output) = Generate(uiPath, ui, codeBehind);

        var error = Assert.Single(output.GetDiagnostics(), d => d.Id == "CS0029");
        var mapped = error.Location.GetMappedLineSpan();
        Assert.Equal(uiPath, mapped.Path);
        Assert.Equal(1, mapped.StartLinePosition.Line);   // 第 0 行是 @using 前缀（K22 迁移）
        Assert.Equal(pad.Length + prefix.Length, mapped.StartLinePosition.Character);
    }
}
