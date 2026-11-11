using System;
using System.Linq;
using System.Threading;
using EclipseUI.Compiler.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

/// <summary>
/// 组件表面的**组合**守卫：一个模板里把所有参数/绑定/内容/插槽/事件形态各用一次，
/// 断言「生成产物 + code-behind 合成一个编译单元后零错误」。
/// <para>
/// 单点用例（各 EUI 码一条）保不住这类回归：D109（组件标签上的 <c>Class</c>/附加属性）与
/// D110（<c>{P}Changed</c> 形状）都是「分析器放行、生成代码 CS1061/CS1503」——
/// 只有真把生成结果交给 Roslyn 编译才会红。这条用例把三类属性的组合、默认内容与具名插槽
/// 同框、块模板内的绑定与推送都覆盖，任何一处形态退化都会在这里编译失败。
/// </para>
/// </summary>
public class ComponentSurfaceMatrixTests
{
    private const string CodeBehind = """
        using System;
        using System.Collections.Generic;
        using EclipseUI;
        using EclipseUI.Elements;

        namespace Matrix
        {
            public class ProbeCard : Component
            {
                [UiParameter] public string Title { get; set; } = "";
                [UiParameter] public int Count { get; set; }
                [UiParameter] public string? Hue { get; set; }
                [UiParameter] public Action<string?>? HueChanged { get; set; }
                [UiParameter] public Action? Clicked { get; set; }
                [UiParameter] public Action<int>? Picked { get; set; }
                [UiSlot("Header")] public Element? Header { get; set; }
                [UiSlot("Rows")] public IReadOnlyList<Element>? Rows { get; set; }
                [UiContent] public IReadOnlyList<Element>? Body { get; set; }

                protected override Element Build()
                {
                    var root = new StackPanel();
                    if (Header is not null) root.Children.Add(Header);
                    if (Rows is not null) foreach (var row in Rows) root.Children.Add(row);
                    if (Body is not null) foreach (var child in Body) root.Children.Add(child);
                    return root;
                }
            }

            public partial class Showcase
            {
                public string title = "t";
                public int count = 1;
                public string? hue = "red";
                public bool flag = true;
                public string[] items = { "a", "b" };

                private void OnClicked() { }
                private void OnPicked(int value) { }
            }
        }
        """;

    /// <summary>每一种用法各出现一次；顺序无关，但都写进同一个模板才能真正验证「组合」。</summary>
    private const string Ui = """
        @using EclipseUI.Elements
        @namespace Matrix

        @style {
            .card { CornerRadius: 4; }
        }

        <StackPanel>
            <ProbeCard Title="literal" Count="2" Hue="red" />

            <ProbeCard Title={title} Count={count} @bind-Hue={hue} />

            <ProbeCard Clicked={OnClicked} Picked={OnPicked} />

            <ProbeCard Class="card" Grid.Row="1" Style="CornerRadius: 4">
                <TextBlock>default content</TextBlock>
            </ProbeCard>

            <ProbeCard>
                <Slot Name="Header"><TextBlock>h</TextBlock></Slot>
                <Slot Name="Rows"><TextBlock>r1</TextBlock><Button Label="r2" /></Slot>
            </ProbeCard>

            @foreach (var item in items)
            {
                <ProbeCard Title={item} @bind-Hue={hue} />
            }

            @if (flag)
            {
                <ProbeCard Title="branch" Count={count} />
            }
        </StackPanel>
        """;

    [Fact]
    public void EveryParameterFormInOneTemplate_ProducesAProjectThatCompiles()
    {
        var (result, output) = RunProject(CodeBehind, ("Showcase.ui", Ui));

        var errors = result.Diagnostics
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} :: {d.GetMessage()}")
            .ToList();
        Assert.True(errors.Count == 0, "template diagnostics: " + string.Join(" | ", errors));

        // 防空转：没生成源码时「零编译错误」毫无意义。
        var code = Assert.Single(result.GeneratedSources).SourceText.ToString();
        // ProbeCard 就在 @namespace Matrix 里：K33 起不需要 `@using Matrix`（模板刻意不写）
        Assert.Contains("new global::Matrix.ProbeCard()", code);
        Assert.Contains(".HueChanged = v =>", code);          // @bind 的回调（build-once）
        Assert.Contains(".ApplyClasses(", code);              // 组件标签上的 Class → 根元素
        Assert.Contains("Grid.SetRow(", code);                // 附加属性 → 根元素

        var compileErrors = output.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} :: {d.GetMessage()}")
            .ToList();
        Assert.True(compileErrors.Count == 0,
            "generated project does not compile: " + string.Join(" | ", compileErrors));
    }

    private static Compilation Compile((string Path, string Text) codeBehind)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Component).Assembly.Location));

        return CSharpCompilation.Create(
            "ComponentSurfaceProbe",
            new[] { CSharpSyntaxTree.ParseText(codeBehind.Text, path: codeBehind.Path) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
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

    private sealed class TextFile : AdditionalText
    {
        private readonly SourceText _text;

        public TextFile(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text);
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }
}
