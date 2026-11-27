using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BubuUI.Compiler.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace BubuUI.Compiler.Tests;

/// <summary>
/// K35：模板生成对象本身就是控件，类集、局部样式和附加属性直接作用于该实例。
/// 同时检查输出项目确实可以编译，防止目录放行而生成类型不兼容。
/// </summary>
public class ComponentTagElementAttributesTests
{
    private static CSharpCompilation Compile(params (string Path, string Text)[] files)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(UiObject).Assembly.Location));
        // Style= 的值走样式属性目录（StyleBuilder/StyledProperty 符号），探针编译必须看得见渲染程序集。
        references.Add(MetadataReference.CreateFromFile(typeof(BubuUI.Rendering.StyleRegistry).Assembly.Location));
        return CSharpCompilation.Create(
            "ComponentTagAttributesProbe",
            files.Select(f => CSharpSyntaxTree.ParseText(f.Text, path: f.Path)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private sealed class TextFile : AdditionalText
    {
        private readonly SourceText _text;

        public TextFile(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }

    private static (GeneratorRunResult Result, Compilation Output) RunProject(
        string codeBehind, params (string Path, string Text)[] files)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            files.Select(f => (AdditionalText)new TextFile(f.Path, f.Text)));
        driver = driver.RunGeneratorsAndUpdateCompilation(Compile(("CodeBehind.cs", codeBehind)),
            out var output, out _);
        return (driver.GetRunResult().Results.Single(), output);
    }

    private static string GeneratedSource(GeneratorRunResult result, string className)
    {
        Assert.Null(result.Exception);
        var match = result.GeneratedSources.FirstOrDefault(s =>
            s.HintName.StartsWith(className + ".", StringComparison.Ordinal));
        Assert.True(match.HintName is not null,
            $"no source for '{className}'; diagnostics: " +
            string.Join("; ", result.Diagnostics.Select(d => d.Id + ": " + d.GetMessage())));
        return match.SourceText.ToString();
    }

    private static IEnumerable<string> Errors(Compilation output) =>
        output.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} :: {d.GetMessage()}");

    private static void AssertCompiles(Compilation output)
    {
        var errors = Errors(output).ToList();
        Assert.True(errors.Count == 0, "generated project does not compile: " + string.Join(" | ", errors));
    }

    /// <summary>断言失败时把整份生成代码打出来——这组用例断言的是发射形态，看源码最快。</summary>
    private static void AssertContains(string expected, string source) =>
        Assert.True(source.Contains(expected, StringComparison.Ordinal),
            $"expected '{expected}' in generated source:\n{source}");

    private static void AssertDoesNotContain(string unexpected, string source) =>
        Assert.False(source.Contains(unexpected, StringComparison.Ordinal),
            $"unexpected '{unexpected}' in generated source:\n{source}");

    private const string CardUi = """
        @using BubuUI.Elements
        <StackPanel Gap="4">
            <TextBlock FontSize="12">card</TextBlock>
        </StackPanel>
        @namespace Gallery
        """;

    private const string HostCodeBehind = "namespace Gallery;\npublic partial class Host { }";

    [Fact]
    public void ClassStyleAndAttachedProperties_LandOnTheRootElement()
    {
        // 三种元素级属性一起上——它们的共同点是「写到 cmp.Root，而不是 cmp」。
        const string hostUi = """
            @using BubuUI.Elements
            @using Gallery
            @style {
                .wide { CornerRadius: 6; }
            }
            <Grid RowDefinitions="Auto,40">
                <Card Class="wide" Grid.Row="1" Style="CornerRadius: 12" />
            </Grid>
            @namespace Gallery
            """;

        var (result, output) = RunProject(HostCodeBehind, ("Card.bui", CardUi), ("Host.bui", hostUi));

        Assert.Empty(Errors(output));
        var host = GeneratedSource(result, "Host");

        // Class：合并进根元素（ApplyClasses 负责与组件自身的根类合并），不是 cmp*.Classes。
        AssertContains(".ApplyClasses(new global::System.String[] { \"Host__wide\" });", host);
        AssertDoesNotContain(".Classes = new global::System.String[] { \"Host__wide\" }", host);
        // 附加属性：目标同样是根元素。
        AssertContains("global::BubuUI.Elements.Grid.SetRow(el1, 1);", host);
        // Style=：local 层写根元素。
        AssertContains("el1.SetValue(", host);
        AssertDoesNotContain(".Root", host);
        AssertCompiles(output);
    }

    [Fact]
    public void ElementLevelAttributes_AreEmittedBeforeInitializationAndAttachment()
    {
        // 顺序是根因的一半：Root 由 Mount() 里的 Build() 产生，发射点必须在那之后、
        // 挂父级之前（§5.1 冻结顺序）。
        const string hostUi = """
            @using BubuUI.Elements
            @using Gallery
            <StackPanel>
                <Card Grid.Row="1" />
            </StackPanel>
            @namespace Gallery
            """;

        var (result, _) = RunProject(HostCodeBehind, ("Card.bui", CardUi), ("Host.bui", hostUi));
        var host = GeneratedSource(result, "Host");

        var initialize = host.IndexOf("el1.EnsureInitialized();", StringComparison.Ordinal);
        var attached = host.IndexOf("Grid.SetRow(el1, 1);", StringComparison.Ordinal);
        var add = host.IndexOf("this.Children.Add(el1);", StringComparison.Ordinal);
        Assert.True(attached >= 0 && initialize > attached && add > initialize,
            "caller properties must be set before initialization and attachment; source:\n" + host);
    }

    [Fact]
    public void DynamicClassAndAttachedAtRoot_AreFieldPromotedAndCompile()
    {
        // 根级动态形态进 U{N} 方法——U 方法读字段，所以组件变量必须提升成字段，
        // 否则生成代码报「找不到 cmp0」。附加属性的动态形态此前完全没被这个判定覆盖。
        const string hostUi = """
            @using BubuUI.Elements
            @using Gallery
            <Card Class={outerClass} Grid.Row={row} />
            @namespace Gallery
            @code {
                public string outerClass = "wide";
                public int row = 1;
            }
            """;

        var (result, output) = RunProject("// no code-behind", ("Card.bui", CardUi), ("Host.bui", hostUi));

        Assert.Empty(Errors(output));
        var host = GeneratedSource(result, "Host");

        AssertContains("partial class Host : global::Gallery.Card", host);
        AssertContains("this.ApplyTemplateClasses(", host);
        AssertContains("Grid.SetRow(this,", host);
        AssertDoesNotContain("__cmp", host);
        AssertCompiles(output);
    }

    [Fact]
    public void ClassInsideABlock_StillTargetsRootAndCompiles()
    {
        // 块内是局部变量 + 局部函数捕获，形态与根级不同（R{N}），同样必须写在 Root 上。
        const string hostUi = """
            @using BubuUI.Elements
            @using Gallery
            <StackPanel>
                @if (show) {
                    <Card Class={outerClass} Grid.Row="2" />
                }
            </StackPanel>
            @namespace Gallery
            @code {
                public bool show = true;
                public string outerClass = "wide";
            }
            """;

        var (result, output) = RunProject("// no code-behind", ("Card.bui", CardUi), ("Host.bui", hostUi));

        Assert.Empty(Errors(output));
        var host = GeneratedSource(result, "Host");
        AssertContains(".ApplyClasses(", host);
        AssertContains("Grid.SetRow(", host);
        AssertDoesNotContain(".Root", host);
        AssertCompiles(output);
    }

    [Fact]
    public void InjectedCollapseClasses_AlsoLandOnRoot()
    {
        // E4：塌缩注入（`<Card>` 被 `@style` 结构选择器命中、自身没写 Class=）走同一个入口，
        // 否则这条路径生成的 `cmp0.Classes` 依旧编译不过。
        const string hostUi = """
            @using BubuUI.Elements
            @using Gallery
            @style {
                .panel {
                    CornerRadius: 4;
                    > Card { CornerRadius: 8; }
                }
            }
            <StackPanel Class="panel">
                <Card />
                <Card />
            </StackPanel>
            @namespace Gallery
            """;

        var (result, output) = RunProject(HostCodeBehind, ("Card.bui", CardUi), ("Host.bui", hostUi));

        Assert.Empty(Errors(output));
        var host = GeneratedSource(result, "Host");
        // 注入名形如 `Host__panel__card`——关键不是名字，而是它经 ApplyClasses 落到根元素。
        AssertContains("ApplyClasses(new global::System.String[] {", host);
        AssertDoesNotContain("cmp0.Classes", host);
        AssertCompiles(output);
    }
}
