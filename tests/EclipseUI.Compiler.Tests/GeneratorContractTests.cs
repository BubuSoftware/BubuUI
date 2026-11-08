using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using EclipseUI.Compiler;
using EclipseUI.Compiler.Generator;
using EclipseUI.Compiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

public class GeneratorContractTests
{
    private sealed class UiAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public UiAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }

    /// <summary>
    /// C2 起生成器的模板表面从编译符号派生（design/14 §14.10.2）：探针编译必须引用运行时，
    /// 否则目录为空、每个标签都是 EUI001，生成器不出代码。
    /// </summary>
    private static GeneratorRunResult Run(string uiPath, string ui, string? codeBehind = null,
        string? referencedLibrary = null)
        => RunWithReferences(uiPath, ui,
            codeBehind ?? "public partial class Probe { public int gap = 4; public string message = \"start\"; }",
            referencedLibrary);

    private static CSharpCompilation CreateCompilation(string codeBehind, string? referencedLibrary = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Component).Assembly.Location));
        if (referencedLibrary is not null)
            references.Add(MetadataReference.CreateFromFile(referencedLibrary));
        return CSharpCompilation.Create(
            "TemplateProbe",
            new[] { CSharpSyntaxTree.ParseText(codeBehind) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static GeneratorRunResult RunWithReferences(string uiPath, string ui, string codeBehind,
        string? referencedLibrary = null)
    {
        var driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiAdditionalText(uiPath, ui) });
        var compilation = CreateCompilation(codeBehind, referencedLibrary);
        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    private static string Generated(GeneratorRunResult result)
    {
        Assert.Null(result.Exception);
        return result.GeneratedSources.Single().SourceText.ToString();
    }

    /// <summary>
    /// D74 ② 后生成器有两个 SourceOutput：逐文件生成与目录级诊断出口（后者不产出生成文件）。
    /// 逐文件管道用 WithTrackingName("PerFileGeneration") 命名，增量断言只读这个命名节点，
    /// 不再把所有 "SourceOutput" 混成一组（§14.10.3）。
    /// </summary>
    private static List<IncrementalStepRunReason> GeneratedSourceReasons(GeneratorRunResult result) =>
        result.TrackedSteps["PerFileGeneration"]
            .SelectMany(step => step.Outputs)
            .Select(o => o.Reason)
            .ToList();

    [Fact]
    public void Generation_IsDeterministic_WithStableHint()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Gap={gap}>
                <TextBlock>{message}</TextBlock>
            </StackPanel>
            """;

        var first = Run("C:\\templates\\Probe.ui", ui);
        var second = Run("C:\\templates\\Probe.ui", ui);

        var firstSource = first.GeneratedSources.Single();
        var secondSource = second.GeneratedSources.Single();
        Assert.Matches("^Probe\\.[0-9a-f]{16}\\.g\\.cs$", firstSource.HintName);
        Assert.Equal(firstSource.HintName, secondSource.HintName);
        Assert.Equal(firstSource.SourceText.ToString(), secondSource.SourceText.ToString());
    }

    [Fact]
    public void RootDynamic_PromotesFields_AndEmitsRenderWithLineMapping()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Gap={gap}>
                <TextBlock>{message}</TextBlock>
            </StackPanel>
            """;

        var code = Generated(Run("C:\\templates\\Probe.ui", ui));

        Assert.Contains("private global::EclipseUI.Elements.StackPanel __el0 = null!;", code);
        Assert.Contains("private global::EclipseUI.Elements.TextRun __run2 = null!;", code);
        Assert.Contains("protected override void Render()", code);
        Assert.Contains("private void U0()", code);
        Assert.Contains("private void U1()", code);
        Assert.Matches(@"#line \(2, \d+\) - \(\d+, \d+\) ""C:\\templates\\Probe\.ui""", code);
        Assert.Matches(@"#line \(3, \d+\) - \(\d+, \d+\) ""C:\\templates\\Probe\.ui""", code);
        Assert.Contains("__el0.Gap = gap;", code);
        Assert.Contains("__run2.Text = $\"{(message)}\";", code);
    }

    [Fact]
    public void TypeError_InDynamicBinding_MapsBackToUiLine()
    {
        const string uiPath = "C:\\templates\\Probe.ui";
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Gap={gap}>
                <TextBlock>{message}</TextBlock>
            </StackPanel>
            """;
        const string codeBehind = """
            public partial class Probe : global::EclipseUI.Component
            {
                public string gap = "oops";
                public string message = "start";
            }
            """;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Component).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "TemplateProbe",
            new[] { CSharpSyntaxTree.ParseText(codeBehind) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiAdditionalText(uiPath, ui) });
        var updatedDriver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var sources = updatedDriver.GetRunResult().Results.Single().GeneratedSources;
        Assert.Single(sources);

        var errors = output.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToList();
        var typeError = Assert.Single(errors, d => d.Id == "CS0029");
        var lineSpan = typeError.Location.GetMappedLineSpan();
        Assert.Equal(uiPath, lineSpan.Path);
        Assert.Equal(1, lineSpan.StartLinePosition.Line);   // 第 0 行是 @using 前缀（K22 迁移）
    }

    [Fact]
    public void ControlEvent_EmitsHandlerLocal_AndInvalidateWrapper()
    {
        const string ui = "@using EclipseUI.Elements\n<StackPanel><Button OnClick={() => Bump()}>go</Button></StackPanel>";
        const string codeBehind = "public partial class Probe { private void Bump() { } }";

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, codeBehind));

        Assert.Contains("var h2 = (global::System.Action)(() => Bump());", code);
        Assert.Contains(
            "AddHandler(global::EclipseUI.Elements.Button.ClickEvent, () => { h2(); Invalidate(); });",
            code);
        Assert.DoesNotContain("Track(", code);
        Assert.DoesNotContain(".Subscribe(", code);
    }

    [Fact]
    public void TextRuns_UseContentHost_AndPromoteDynamicRun()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Gap="8">
                <TextBlock>Hi {message}</TextBlock>
            </StackPanel>
            """;

        var code = Generated(Run("C:\\templates\\Probe.ui", ui));

        Assert.Contains("((global::EclipseUI.ITextContentHost)el1).AddRun(\"Hi \");", code);
        Assert.Contains("private global::EclipseUI.Elements.TextRun __run2 = null!;", code);
        Assert.Contains("__run2 = ((global::EclipseUI.ITextContentHost)el1).AddRun(\"\");", code);
        Assert.Contains("private void U0()", code);
        Assert.Contains("__run2.Text = $\"{(message)}\";", code);
        Assert.DoesNotContain("new global::EclipseUI.Elements.TextRun", code);
    }

    [Fact]
    public void IfBlock_EmitsCoordinatorShape_WithLocalRenderAction()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                @if (show) {
                    <TextBlock>{message}</TextBlock>
                }
            </StackPanel>
            """;
        const string codeBehind = """
            public partial class Probe : global::EclipseUI.Component
            {
                public bool show = true;
                public string message = "";
            }
            """;

        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind));

        Assert.Contains("private global::EclipseUI.ConditionalBlock __blk1 = null!;", code);
        Assert.Contains("__blk1.SetCondition(() => show);", code);
        Assert.Contains("__blk1.SetContent(scope2 =>", code);
        Assert.Contains("scope2.AddRoot(el3);", code);
        Assert.Contains("void R0()", code);
        Assert.Contains("run4.Text = $\"{(message)}\";", code);
        Assert.Contains("scope2.AddRenderAction(R0);", code);
        Assert.Contains("Track(__blk1);", code);
        Assert.DoesNotContain("private void R0", code); // 局部函数，不是实例方法

        var render = code[code.IndexOf("protected override void Render()", StringComparison.Ordinal)..];
        Assert.Contains("__blk1.Refresh();", render);
    }

    [Fact]
    public void IfBlock_EmptyBranch_OmitsSetContent()
    {
        const string ui = "@using EclipseUI.Elements\n<StackPanel>@if (show) { }</StackPanel>";
        const string codeBehind = """
            public partial class Probe : global::EclipseUI.Component
            {
                public bool show = true;
            }
            """;

        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind));

        Assert.Contains("private global::EclipseUI.ConditionalBlock __blk1 = null!;", code);
        Assert.Contains("__blk1.SetCondition(() => show);", code);
        Assert.Contains("Track(__blk1);", code);
        Assert.DoesNotContain(".SetContent(", code);
    }

    [Fact]
    public void ForeachBlock_InfersItemType_AndEmitsKeyedTemplate()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                @foreach (var t in items) {
                    <TextBlock Key={t.Id}>{t.Name}</TextBlock>
                }
            </StackPanel>
            """;
        const string codeBehind = """
            public sealed class Item
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }

            public partial class Probe : global::EclipseUI.Component
            {
                public System.Collections.Generic.List<Item> items = new();
            }
            """;

        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind));

        Assert.Contains("private global::EclipseUI.ForEachBlock<global::Item> __blk1 = null!;", code);
        Assert.Contains("__blk1.SetItems(() => items);", code);
        Assert.Contains("__blk1.SetKeySelector((global::Item t) => (object?)(t.Id));", code);
        Assert.Contains(
            "__blk1.SetTemplate((global::Item t, global::EclipseUI.BlockScope scope2) =>", code);
        Assert.Contains("scope2.AddRoot(el3);", code);
        Assert.Contains("run4.Text = $\"{(t.Name)}\";", code);
        Assert.Contains("scope2.AddRenderAction(R0);", code);
        Assert.Contains("Track(__blk1);", code);

        var render = code[code.IndexOf("protected override void Render()", StringComparison.Ordinal)..];
        Assert.Contains("__blk1.Refresh();", render);
    }

    [Fact]
    public void ForeachBlock_WithoutKey_OmitsKeySelector()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                @foreach (var t in items) {
                    <TextBlock>{t.Name}</TextBlock>
                }
            </StackPanel>
            """;
        const string codeBehind = """
            public sealed class Item { public string Name { get; set; } = ""; }

            public partial class Probe : global::EclipseUI.Component
            {
                public System.Collections.Generic.List<Item> items = new();
            }
            """;

        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind));

        Assert.Contains("ForEachBlock<global::Item>", code);
        Assert.DoesNotContain("SetKeySelector", code);
    }

    [Fact]
    public void ForeachBlock_InfersItemType_FromArrayDictionaryValuesAndLinq()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                @foreach (var t in items) {
                    <TextBlock>{t.Name}</TextBlock>
                }
                @foreach (var t in map.Values) {
                    <TextBlock>{t.Name}</TextBlock>
                }
                @foreach (var t in System.Linq.Enumerable.Where(items, i => i.Id > 1)) {
                    <TextBlock>{t.Name}</TextBlock>
                }
            </StackPanel>
            """;
        const string codeBehind = """
            public sealed class Item
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }

            public partial class Probe : global::EclipseUI.Component
            {
                public Item[] items = System.Array.Empty<Item>();
                public System.Collections.Generic.Dictionary<int, Item> map = new();
            }
            """;

        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind));

        Assert.Equal(3, code.Split("private global::EclipseUI.ForEachBlock<global::Item>").Length - 1);
        Assert.Contains(".SetItems(() => items);", code);
        Assert.Contains(".SetItems(() => map.Values);", code);
        Assert.Contains(".SetItems(() => System.Linq.Enumerable.Where(items, i => i.Id > 1));", code);
    }

    [Fact]
    public void NestedForeach_InjectsOuterItemIntoInferenceEnvironment()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                @foreach (var group in groups) {
                    @foreach (var t in group.Items) {
                        <TextBlock>{t.Name}</TextBlock>
                    }
                }
            </StackPanel>
            """;
        const string codeBehind = """
            public sealed class Item { public string Name { get; set; } = ""; }
            public sealed class Group
            {
                public System.Collections.Generic.List<Item> Items { get; } = new();
            }

            public partial class Probe : global::EclipseUI.Component
            {
                public System.Collections.Generic.List<Group> groups = new();
            }
            """;

        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind));

        Assert.Contains("ForEachBlock<global::Group>", code);
        Assert.Contains("ForEachBlock<global::Item>", code);
        Assert.Contains("__blk1.SetItems(() => groups);", code);
        Assert.Contains("blk3.SetItems(() => group.Items);", code);
        Assert.Contains(
            "blk3.SetTemplate((global::Item t, global::EclipseUI.BlockScope scope4) =>", code);
        Assert.Contains("run6.Text = $\"{(t.Name)}\";", code);
    }

    [Fact]
    public void Binding_EmitsUiBinding_WithNamedDelegates()
    {
        const string ui = "@using EclipseUI.Elements\n<StackPanel><CheckBox @bind-Checked={flag}>x</CheckBox></StackPanel>";
        const string codeBehind = """
            public partial class Probe : global::EclipseUI.Component
            {
                public bool flag;
            }
            """;

        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind));

        Assert.Contains("private global::EclipseUI.UiBinding __bind2 = null!;", code);
        Assert.Contains(
            "__bind2 = global::EclipseUI.UiBinding.TwoWay(el1, " +
            "global::EclipseUI.Elements.CheckBox.CheckedProperty, " +
            "readTarget: () => el1.Checked, writeTarget: v => el1.Checked = v, " +
            "readSource: () => flag, writeSource: v => flag = v, invalidate: Invalidate);",
            code);
        Assert.Contains("el1.Label = \"x\";", code);
        Assert.Contains("Track(__bind2);", code);

        var render = code[code.IndexOf("protected override void Render()", StringComparison.Ordinal)..];
        Assert.Contains("__bind2.Refresh();", render);
        Assert.DoesNotContain(".Subscribe(", code);
    }

    [Fact]
    public void Binding_InBlock_UsesScopeDisposable_AndRenderAction()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                @if (show) {
                    <CheckBox @bind-Checked={flag}>x</CheckBox>
                }
            </StackPanel>
            """;
        const string codeBehind = """
            public partial class Probe : global::EclipseUI.Component
            {
                public bool show = true;
                public bool flag;
            }
            """;

        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind));

        Assert.Contains(
            "var bind4 = global::EclipseUI.UiBinding.TwoWay(el3, " +
            "global::EclipseUI.Elements.CheckBox.CheckedProperty,", code);
        Assert.Contains("scope2.AddDisposable(bind4);", code);
        Assert.Contains("void R0()", code);
        Assert.Contains("bind4.Refresh();", code);
        Assert.Contains("scope2.AddRenderAction(R0);", code);
        Assert.DoesNotContain("Track(bind4)", code);
    }

    [Fact]
    public void Foreach_NonEnumerableCollection_ReportsEUI007()
    {
        const string ui = "@using EclipseUI.Elements\n<StackPanel>@foreach (var x in count) { <TextBlock>{message}</TextBlock> }</StackPanel>";
        const string codeBehind = """
            public partial class Probe : global::EclipseUI.Component
            {
                public int count;
                public string message = "";
            }
            """;

        var result = RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI007");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("count", diag.GetMessage());
    }

    // ---------- S3：@bind-P 语法、旧语法与源校验 ----------

    private const string BindFixtures = """
        public partial class Probe : global::EclipseUI.Component
        {
            public bool flag;
            public int count;
            public static bool shared;
            public const bool Always = true;
            public readonly bool FrozenField = true;
            public bool ReadOnlyFlag => flag;
            public bool InitOnlyFlag { get; init; }
            public Cell StructCell;
            public Holder Wrapped = new();
            public System.Collections.Generic.List<Cell> cells = new();
        }
        public struct Cell { public bool Done; }
        public class Holder { public Cell Inner { get; set; } }
        """;

    private static void AssertBindRejected(string expression)
    {
        var ui = "@using EclipseUI.Elements\n" + $"<StackPanel><CheckBox @bind-Checked={{{expression}}}>x</CheckBox></StackPanel>";
        var result = RunWithReferences("C:\\templates\\Probe.ui", ui, BindFixtures);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI037");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
    }

    [Theory]
    [InlineData("flag.ToString()")]      // 方法调用
    [InlineData("cells[0].Done")]        // 索引器
    [InlineData("Wrapped?.Inner")]       // 条件访问
    [InlineData("flag ?? true")]         // 空合并
    [InlineData("(object)flag")]         // 强制转换
    [InlineData("flag as object")]       // as
    [InlineData("new Cell()")]           // new
    [InlineData("() => flag")]           // lambda
    [InlineData("shared")]               // 静态字段
    [InlineData("Always")]               // 常量
    [InlineData("FrozenField")]          // readonly 字段
    [InlineData("ReadOnlyFlag")]         // 无 setter 属性
    [InlineData("InitOnlyFlag")]         // init-only setter
    [InlineData("count")]                // 与目标类型不一致
    [InlineData("Wrapped.Inner.Done")]   // 中间段是属性返回的 struct
    [InlineData("missing")]              // 名字不存在
    public void Bind_NonWritableSource_ReportsEUI037(string expression) => AssertBindRejected(expression);

    [Theory]
    [InlineData("flag")]
    [InlineData("this.flag")]
    [InlineData("StructCell.Done")] // 中间段是 struct 字段：合法
    public void Bind_WritableSource_GeneratesBinding(string expression)
    {
        var ui = "@using EclipseUI.Elements\n" + $"<StackPanel><CheckBox @bind-Checked={{{expression}}}>x</CheckBox></StackPanel>";
        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, BindFixtures));

        Assert.Contains($"writeSource: v => {expression} = v", code);
    }

    [Fact]
    public void BindNewSyntax_IsDiagnosticFree()
    {
        var modern = RunWithReferences("C:\\templates\\Probe.ui",
            "@using EclipseUI.Elements\n<StackPanel><CheckBox @bind-Checked={flag}>x</CheckBox></StackPanel>", BindFixtures);

        Assert.Matches(@"#line \(\d+, \d+\) - \(\d+, \d+\)", Generated(modern));
        Assert.Empty(modern.Diagnostics);
    }

    [Fact]
    public void LegacyBind_ReportsEUI046Error_AndGeneratesNothing()
    {
        // K27（Q7-B）：P:bind 本波移除——EUI046 Error，且错误诊断阻断生成输出。
        var result = RunWithReferences("C:\\templates\\Probe.ui",
            "@using EclipseUI.Elements\n<StackPanel><CheckBox Checked:bind={flag}>x</CheckBox></StackPanel>", BindFixtures);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI046");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains("@bind-Checked", diag.GetMessage());
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void LegacyValue_ReportsEUI046Error_AndGeneratesNothing()
    {
        var result = RunWithReferences("C:\\templates\\Probe.ui",
            "@using EclipseUI.Elements\n<StackPanel Gap:value=\"8\" />", BindFixtures);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI046");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
        Assert.Contains(":value", diag.GetMessage());
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Bind_InvalidTarget_ReportsEUI040_AndGeneratesNothing()
    {
        var result = RunWithReferences("C:\\templates\\Probe.ui",
            "@using EclipseUI.Elements\n<StackPanel><Button @bind-OnClick={() => flag}>x</Button></StackPanel>", BindFixtures);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
    }

    [Fact]
    public void Bind_OnItemMember_IsEntryScopedAndWritesThroughItem()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                @foreach (var t in rows) {
                    <CheckBox @bind-Checked={t.Done}>x</CheckBox>
                }
            </StackPanel>
            """;
        const string codeBehind = """
            public partial class Probe : global::EclipseUI.Component
            {
                public System.Collections.Generic.List<Row> rows = new();
            }
            public class Row { public bool Done { get; set; } }
            """;

        var result = RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind);
        var code = Generated(result);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("global::EclipseUI.ForEachBlock<global::Row>", code);
        Assert.Contains("readSource: () => t.Done, writeSource: v => t.Done = v", code);
        Assert.Contains(".AddDisposable(bind", code);
        Assert.Contains(".SetTemplate((global::Row t, global::EclipseUI.BlockScope scope", code);
    }

    [Fact]
    public void Bind_OnValueTypeItemMember_ReportsEUI037()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                @foreach (var t in cells) {
                    <CheckBox @bind-Checked={t.Done}>x</CheckBox>
                }
            </StackPanel>
            """;

        var result = RunWithReferences("C:\\templates\\Probe.ui", ui, BindFixtures);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI037");
        Assert.Contains("value type", diag.GetMessage());
    }

    // ---- C2：继承的属性与事件进入模板表面（design/14 §14.4.2/§14.4.3、§14.12 C2 出口）----

    /// <summary>
    /// C2 出口里的「<c>Padding</c> 等属性变为模板可用」：这三个属性都声明在 <c>Panel</c>/
    /// <c>StackPanelBase</c>/<c>Element</c> 上、靠继承进入每个标签的表面，字面量与表达式两种写法都要通。
    /// </summary>
    [Fact]
    public void InheritedStyledProperties_AreTemplateUsable_LiteralAndExpression()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Padding="12" ClipToBounds={clip}>
                <TextBlock Padding={pad} IsHitTestVisible="false" />
            </StackPanel>
            """;
        const string codeBehind = """
            public partial class Probe : global::EclipseUI.Component
            {
                public bool clip = true;
                public double pad = 2;
            }
            """;

        var result = RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind);
        Assert.Empty(result.Diagnostics);
        var code = Generated(result);

        Assert.Contains(".Padding = 12d;", code);
        Assert.Contains("ClipToBounds = clip;", code);
        Assert.Contains("Padding = pad;", code);
        Assert.Contains("IsHitTestVisible = false;", code);
    }

    /// <summary>
    /// C2 出口里的「8 个事件变为模板可用」：7 个继承自 <c>Element</c> 的输入/焦点事件 +
    /// 控件自有事件，生成的 <c>AddHandler</c> 必须引用**声明类型**的字段（继承事件即 <c>Element.XxxEvent</c>）。
    /// </summary>
    [Theory]
    [InlineData("Button", "OnClick", "global::EclipseUI.Elements.Button.ClickEvent")]
    [InlineData("CheckBox", "OnCheckedChanged", "global::EclipseUI.Elements.CheckBox.CheckedChangedEvent")]
    [InlineData("StackPanel", "OnPointerPressed", "global::EclipseUI.Element.PointerPressedEvent")]
    [InlineData("StackPanel", "OnPointerReleased", "global::EclipseUI.Element.PointerReleasedEvent")]
    [InlineData("StackPanel", "OnPointerMoved", "global::EclipseUI.Element.PointerMovedEvent")]
    [InlineData("TextBlock", "OnKeyDown", "global::EclipseUI.Element.KeyDownEvent")]
    [InlineData("TextBlock", "OnKeyUp", "global::EclipseUI.Element.KeyUpEvent")]
    [InlineData("Button", "OnGotFocus", "global::EclipseUI.Element.GotFocusEvent")]
    [InlineData("CheckBox", "OnLostFocus", "global::EclipseUI.Element.LostFocusEvent")]
    public void EventAttribute_EmitsAddHandlerAgainstTheDeclaringType(
        string tag, string attribute, string fieldReference)
    {
        var ui = "@using EclipseUI.Elements\n" + $"<{tag} {attribute}={{() => Bump()}} />";
        const string codeBehind = "public partial class Probe { private void Bump() { } }";

        var result = RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind);
        Assert.Empty(result.Diagnostics);
        Assert.Contains($"AddHandler({fieldReference},", Generated(result));
    }

    /// <summary>§14.10.3 的缓存前提：同一批引用、无关源码变化下目录必须按值相等（不手写缓存）。</summary>
    [Fact]
    public void Catalog_IsValueEqual_WhenOnlyUnrelatedSourceChanges()
    {
        var first = UiCatalogFactory.Build(CreateCompilation(
            "public partial class Probe { public int a = 1; }"));
        var second = UiCatalogFactory.Build(CreateCompilation(
            "public partial class Probe { public string b = \"two\"; public System.Collections.Generic.List<int> c = new(); }"));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    /// <summary>
    /// §14.10.3 的增量结构实测：管道根把编译与附加文本分成两步，目录只挂在 Compilation 之后，
    /// 所以只改 .ui 时扫描输入不变、代码仍随模板更新。
    /// G3 更正：Roslyn 4.9.2 的 <c>TrackedSteps[].Outputs</c> 元素是
    /// <c>(object Value, IncrementalStepRunReason Reason)</c> 二元组——reason 公开可断言；
    /// 旧注释「公开 API 没有 step reason」系误读（只看了 step 的属性表，没看 Outputs 元素类型）。
    /// </summary>
    [Fact]
    public void Incremental_TemplateOnlyChange_KeepsCatalogInput_AndUpdatesSource()
    {
        var uiPath = Path.Combine(AppContext.BaseDirectory, "Probe.ui");
        var initial = new UiAdditionalText(uiPath, "@using EclipseUI.Elements\n<StackPanel Gap=\"4\" />");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { initial },
            driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));
        var compilation = CreateCompilation("public partial class Probe { public int a = 1; }");

        var first = driver.RunGenerators(compilation).GetRunResult().Results.Single();
        driver = driver.ReplaceAdditionalText(initial, new UiAdditionalText(uiPath, "@using EclipseUI.Elements\n<StackPanel Gap=\"9\" />"));
        var second = driver.RunGenerators(compilation).GetRunResult().Results.Single();

        Assert.Empty(first.Diagnostics);
        Assert.Empty(second.Diagnostics);
        Assert.Contains(".Gap = 4d;", Generated(first));
        Assert.Contains(".Gap = 9d;", Generated(second));

        // 目录扫描的上游是 Compilation；附加文本走另一条根
        Assert.Contains("Compilation", second.TrackedSteps.Keys);
        Assert.Contains("AdditionalTexts", second.TrackedSteps.Keys);
        Assert.Contains("SourceOutput", second.TrackedSteps.Keys);
        var reason = Assert.Single(GeneratedSourceReasons(second));
        Assert.True(reason is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New, $"reason={reason}");
    }

    /// <summary>
    /// design/14 §14.10.3/§14.11.2 验收：无关源码改动后目录重建，但每个生成文件的
    /// SourceOutput step reason 为 Cached/Unchanged（逐文件管道，G3 落地）。
    /// </summary>
    [Fact]
    public void Incremental_UnrelatedSourceChange_KeepsPerFileOutputsCached()
    {
        var uiA = new UiAdditionalText(Path.Combine(AppContext.BaseDirectory, "A.ui"), "@using EclipseUI.Elements\n<StackPanel Gap=\"4\" />");
        var uiB = new UiAdditionalText(Path.Combine(AppContext.BaseDirectory, "B.ui"), "@using EclipseUI.Elements\n<StackPanel Gap=\"7\" />");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { uiA, uiB },
            driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));
        var compilation = CreateCompilation(
            "public partial class A { public int a = 1; } public partial class B { public int b = 2; }");

        driver = driver.RunGenerators(compilation);
        var first = driver.GetRunResult().Results.Single();
        var unrelated = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("public static class Unrelated { public int X => 42; }"));
        var second = driver.RunGenerators(unrelated).GetRunResult().Results.Single();

        Assert.Empty(first.Diagnostics);
        Assert.Empty(second.Diagnostics);
        Assert.Equal(2, second.GeneratedSources.Length);
        Assert.Equal(
            first.GeneratedSources.OrderBy(s => s.HintName).Select(s => s.SourceText.ToString()),
            second.GeneratedSources.OrderBy(s => s.HintName).Select(s => s.SourceText.ToString()));
        var reasons = GeneratedSourceReasons(second);
        Assert.Equal(2, reasons.Count);
        Assert.All(reasons, r => Assert.True(
            r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged, $"reason={r}"));
    }

    /// <summary>逐文件增量：只改一个 .ui 时，另一个文件的 SourceOutput 仍 Cached/Unchanged。</summary>
    [Fact]
    public void Incremental_SingleUiChange_RerunsOnlyThatFile()
    {
        var pathA = Path.Combine(AppContext.BaseDirectory, "A.ui");
        var pathB = Path.Combine(AppContext.BaseDirectory, "B.ui");
        var uiA = new UiAdditionalText(pathA, "@using EclipseUI.Elements\n<StackPanel Gap=\"4\" />");
        var uiB = new UiAdditionalText(pathB, "@using EclipseUI.Elements\n<StackPanel Gap=\"7\" />");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { uiA, uiB },
            driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));
        var compilation = CreateCompilation(
            "public partial class A { public int a = 1; } public partial class B { public int b = 2; }");

        driver = driver.RunGenerators(compilation);
        var first = driver.GetRunResult().Results.Single();
        driver = driver.ReplaceAdditionalText(uiA, new UiAdditionalText(pathA, "@using EclipseUI.Elements\n<StackPanel Gap=\"9\" />"));
        var second = driver.RunGenerators(compilation).GetRunResult().Results.Single();

        Assert.Empty(second.Diagnostics);
        var reasons = GeneratedSourceReasons(second);
        Assert.Equal(2, reasons.Count);
        Assert.Single(reasons, r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged);
        Assert.Single(reasons, r => r is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New);
        Assert.Equal(
            first.GeneratedSources.Single(s => s.HintName.StartsWith("B.", StringComparison.Ordinal)).SourceText.ToString(),
            second.GeneratedSources.Single(s => s.HintName.StartsWith("B.", StringComparison.Ordinal)).SourceText.ToString());
        Assert.Contains(".Gap = 9d;", second.GeneratedSources.Single(s => s.HintName.StartsWith("A.", StringComparison.Ordinal)).SourceText.ToString());
    }

    /// <summary>
    /// 「每轮重建目录」的代价必须是可承受的数字（§14.10.3 禁止用手工缓存换性能）。
    /// 探针编译引用了整个测试输出目录的程序集（比真实应用工程更宽），所以这里是悲观上界。
    /// </summary>
    [Fact]
    public void Catalog_Rebuild_Is_Measured_And_Bounded()
    {
        const int rounds = 20;
        var compilation = CreateCompilation("public partial class Probe { public int a = 1; }");
        var baseline = UiCatalogFactory.Build(compilation);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var round = 0; round < rounds; round++)
            Assert.True(UiCatalogFactory.Build(compilation).Equals(baseline), $"round {round} differs");
        var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;

        var perRoundMs = (double)elapsed / System.Diagnostics.Stopwatch.Frequency / rounds * 1000;
        Assert.True(perRoundMs < 20,
            $"catalog rebuild averaged {perRoundMs:0.00} ms per round over {rounds} rounds for {baseline.Elements.Count} tags");
    }

    // ---- C4：组件标签的生成形态（spec/02 §4.12、§4.3.3、§4.4.3、§4.6.2）----

    private const string ComponentFixtures = """
            using System;
            using System.Collections.Generic;
            using EclipseUI;

            public partial class Probe : Component
            {
                public int gap = 4;
                public string? color;
                public void Store(int i) { }
                public void Close() { }
                [UiParameter(Required = true)] public int Count { get; set; }
                [UiParameter] public string? Title { get; set; }
                [UiParameter] public Action? Closed { get; set; }
                [UiParameter] public Action<int>? Picked { get; set; }
                [UiParameter] public string? Color { get; set; }
                [UiParameter] public Action<string?>? ColorChanged { get; set; }
                [UiParameter]
                [UiContent]
                public Element? Body { get; set; }
            }
            public partial class Group : Component
            {
                [UiParameter(Required = true)] public int Count { get; set; }
                [UiParameter]
                [UiContent]
                public IReadOnlyList<Element> Items { get; set; }
            }
            """;

    [Fact]
    public void ComponentRoot_Lifecycle_UnderTheFrozenOrder()
    {
        const string ui = """
            @using EclipseUI.Elements
            <Probe Count={gap} Title="Hi" Picked={i => Store(i)} @bind-Color={color}>
                <StackPanel />
            </Probe>
            """;

        var result = RunWithReferences("C:\\templates\\Probe.ui", ui, ComponentFixtures);
        Assert.Empty(result.Diagnostics);
        var code = Generated(result);

        // 字段提升 + new → Track → 参数（源码顺序）→ 内容 → Mount → 根返回 .Root
        Assert.Contains("private global::Probe __cmp0 = null!;", code);
        Assert.Contains("__cmp0 = new global::Probe();", code);
        Assert.Contains("Track(__cmp0);", code);
        Assert.Contains("__cmp0.Title = \"Hi\";", code);
        Assert.Contains("var h1 = (global::System.Action<global::System.Int32>)(i => Store(i));", code);
        Assert.Contains("__cmp0.Picked = a0 => { h1(a0); Invalidate(); };", code);
        Assert.Contains("var content2 = new global::System.Collections.Generic.List<global::EclipseUI.Element>();", code);
        Assert.Contains("var el3 = new global::EclipseUI.Elements.StackPanel();", code);
        Assert.Contains("content2.Add(el3);", code);
        Assert.Contains("__cmp0.Body = content2.Count == 0 ? null : content2[0];", code);
        Assert.Contains("__cmp0.Mount();", code);
        Assert.Contains("return __cmp0.Root;", code);
    }

    [Fact]
    public void ComponentRoot_DynamicParameterPush_UsesEqualityComparerGuard()
    {
        const string ui = "@using EclipseUI.Elements\n<Probe Count={gap} Title=\"Hi\" />";

        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, ComponentFixtures));

        Assert.Contains("private void U0()", code);
        Assert.Contains("var v = (global::System.Int32)(gap);", code);
        Assert.Contains("if (!global::System.Collections.Generic.EqualityComparer<global::System.Int32>.Default.Equals(__cmp0.Count, v))", code);
        Assert.Contains("__cmp0.Count = v;", code);
        Assert.Contains("__cmp0.Invalidate();", code);
        Assert.Contains("U0();", code); // 参数位置的首次调用 + Render 登记
        Assert.Contains("protected override void Render()", code);
    }

    [Fact]
    public void ComponentRoot_BindParameter_EmitsChangedCallbackThenPush()
    {
        const string ui = "@using EclipseUI.Elements\n<Probe Count={gap} @bind-Color={color} />";

        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, ComponentFixtures));

        Assert.Contains("__cmp0.ColorChanged = v =>", code);
        Assert.Contains("if (!global::System.Collections.Generic.EqualityComparer<global::System.String>.Default.Equals(color, v))", code);
        Assert.Contains("color = v;", code);
        Assert.Contains("Invalidate();", code);
        Assert.Contains("U1();", code);
        // 推送复用 §4.3.3 形态
        Assert.Contains("var v = (global::System.String)(color);", code);
        Assert.Contains("global::System.Collections.Generic.EqualityComparer<global::System.String>.Default.Equals(__cmp0.Color, v)", code);
        Assert.Contains("__cmp0.Color = v;", code);
    }

    [Fact]
    public void ComponentInBlockScope_AdoptsAndPushesWithLocalR()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel Gap="4">
                @foreach (var t in items)
                {
                    <Probe Count={t.N} Title={t.Name} />
                }
            </StackPanel>
            """;
        const string codeBehind = """
            using System;
            using System.Collections.Generic;
            using EclipseUI;

            public partial class Probe : Component
            {
                public List<Item> items = new();
                public void Store(int i) { }
                [UiParameter(Required = true)] public int Count { get; set; }
                [UiParameter] public string? Title { get; set; }
            }
            public class Item { public int N; public string? Name; }
            """;

        var result = RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind);
        Assert.Empty(result.Diagnostics);
        var code = Generated(result);

        Assert.Contains("Adopt(cmp", code);
        Assert.Contains(".AddDisposable(cmp", code);
        Assert.Contains("var v = (global::System.Int32)(t.N);", code);
        Assert.Contains("if (!global::System.Collections.Generic.EqualityComparer<global::System.Int32>.Default.Equals(cmp", code);
        Assert.Contains(".Count = v;", code);
        Assert.Contains(".AddRenderAction(R", code);
        Assert.Contains(".AddRoot(cmp", code);
    }

    [Fact]
    public void ComponentContent_TracksChildComponentsOnReceiver()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                <Group Count="1">
                    <StackPanel />
                    <Probe Count="2" />
                </Group>
            </StackPanel>
            """;

        var result = RunWithReferences("C:\\templates\\Probe.ui", ui, ComponentFixtures);
        Assert.Empty(result.Diagnostics);
        var code = Generated(result);

        Assert.Contains(".TrackContent(cmp", code);
        Assert.Contains(".Mount();", code);
        Assert.Contains(".Add(cmp", code);
        Assert.Contains(".Children.Add(cmp", code);
    }

    [Fact]
    public void ComponentDelegateParameter_WrapsHandlerAndInvalidatesParent()
    {
        const string ui = "@using EclipseUI.Elements\n<Probe Count={gap} Closed={() => Close()} />";

        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, ComponentFixtures));

        Assert.Contains("var h1 = (global::System.Action)(() => Close());", code);
        Assert.Contains("__cmp0.Closed = () => { h1(); Invalidate(); };", code);
    }

    [Fact]
    public void ComponentBindWithoutChanged_EUI040_BlocksGeneration()
    {
        var result = RunWithReferences("C:\\templates\\Probe.ui",
            "@using EclipseUI.Elements\n<StackPanel><Probe Count={gap} @bind-Title={color} /></StackPanel>", ComponentFixtures);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Contains("TitleChanged", diag.GetMessage());
    }

    [Fact]
    public void ComponentRequiredParameterMissing_EUI027_BlocksGeneration()
    {
        var result = RunWithReferences("C:\\templates\\Probe.ui",
            "@using EclipseUI.Elements\n<StackPanel><Probe Title=\"x\" /></StackPanel>", ComponentFixtures);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI027");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
    }

    [Fact]
    public void ComponentContentViaAttribute_EUI021_BlocksGeneration()
    {
        var result = RunWithReferences("C:\\templates\\Probe.ui",
            "@using EclipseUI.Elements\n<StackPanel><Probe Count={gap} Body={color} /></StackPanel>", ComponentFixtures);

        Assert.Empty(result.GeneratedSources);
        Assert.Contains(result.Diagnostics, d => d.Id == "EUI021");
    }

    [Fact]
    public void ComponentLiteralDelegateParameter_EUI004_BlocksGeneration()
    {
        var result = RunWithReferences("C:\\templates\\Probe.ui",
            "<StackPanel><Probe Count={gap} Closed=\"Close\" /></StackPanel>", ComponentFixtures);

        Assert.Empty(result.GeneratedSources);
        Assert.Contains(result.Diagnostics, d => d.Id == "EUI004");
    }

    [Fact]
    public void ComponentDynamicParameterContentContext_IsBuildOnceSnapshot()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                <Probe Count={gap}>
                    <StackPanel Gap={color} />
                </Probe>
            </StackPanel>
            """;

        var result = RunWithReferences("C:\\templates\\Probe.ui", ui, ComponentFixtures);
        // EUI049 预检警告（Gap 是 double 而 color 是 string）允许出现；Error 仍必须为零。
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var code = Generated(result);

        // 内容内动态属性是快照赋值：不出现 U/R 方法
        Assert.Contains(".Gap = color;", code);
        Assert.DoesNotContain(".AddRenderAction(", code);
        Assert.DoesNotContain(".Gap = $", code);
    }

    // ---- C5：Ref 生成形态与语义级 EUI029（spec/01 §4.3；spec/02 §4.14）----

    /// <summary>把第三方"带问题控件"库真正编译成独立程序集再被消费端引用（design/14 §14.5.4）。</summary>
    private static string EmitLibrary(string name, string source)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"{name}_{Guid.NewGuid():N}.dll");
        var emit = CreateCompilation(source).Emit(path);
        Assert.True(emit.Success, string.Join("; ", emit.Diagnostics
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).Select(d => d.ToString())));
        return path;
    }

    [Fact]
    public void Ref_OnElement_AssignsCodeBehindFieldAtBuildTime()
    {
        const string ui = "@using EclipseUI.Elements\n<StackPanel Ref={savePanel} />";
        var result = Run("C:\\templates\\Probe.ui", ui,
            "public partial class Probe { public global::EclipseUI.Elements.StackPanel savePanel = null!; }");

        Assert.Empty(result.Diagnostics);
        var code = Generated(result);
        Assert.Contains("var el0 = new global::EclipseUI.Elements.StackPanel();", code);
        Assert.Contains("savePanel = el0;", code);          // Build 语句，无 U/R（§4.3 不重算）
        Assert.DoesNotContain("__el", code);                // 无动态属性 → 不提升字段
    }

    [Fact]
    public void Ref_OnComponent_AssignsComponentInstance()
    {
        const string ui = "@using EclipseUI.Elements\n<ProbeCard Count=\"1\" Ref={card} />";
        const string codeBehind = """
            using EclipseUI;
            public partial class Probe { public ProbeCard card = null!; }
            public class ProbeCard : Component
            {
                [UiParameter] public int Count { get; set; }
            }
            """;

        var result = RunWithReferences("C:\\templates\\Probe.ui", ui, codeBehind);
        Assert.Empty(result.Diagnostics);
        var code = Generated(result);
        Assert.Contains("var cmp0 = new global::ProbeCard();", code);
        Assert.Contains("card = cmp0;", code);              //design/14 §14.6.4：组件按实例赋字段
        Assert.Contains("return cmp0.Root;", code);
    }

    [Fact]
    public void Ref_MissingField_EUI029_BlocksGeneration()
    {
        var result = Run("C:\\templates\\Probe.ui", "@using EclipseUI.Elements\n<StackPanel Ref={nowhere} />");

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI029");
        Assert.Contains("nowhere", diag.GetMessage());
        Assert.Contains("CS0103", diag.GetMessage());
    }

    [Fact]
    public void Ref_TypeMismatch_EUI029_BlocksGeneration()
    {
        var result = Run("C:\\templates\\Probe.ui", "@using EclipseUI.Elements\n<StackPanel Ref={size} />",
            "public partial class Probe { public int size = 4; }");

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI029");
        Assert.Contains("EclipseUI.Elements.StackPanel", diag.GetMessage());
    }

    [Fact]
    public void Ref_ReadonlyField_EUI029_BlocksGeneration()
    {
        var result = Run("C:\\templates\\Probe.ui", "@using EclipseUI.Elements\n<StackPanel Ref={locked} />",
            "public partial class Probe { public readonly global::EclipseUI.Elements.StackPanel locked = null!; }");

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI029");
        Assert.Contains("CS0191", diag.GetMessage());
    }

    [Fact]
    public void Ref_StaticField_EUI029_BlocksGeneration()
    {
        var result = Run("C:\\templates\\Probe.ui", "@using EclipseUI.Elements\n<StackPanel Ref={shared} />",
            "public partial class Probe { public static global::EclipseUI.Elements.StackPanel shared = null!; }");

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI029");
        Assert.Contains("static", diag.GetMessage());
    }    [Fact]
    public void Ref_NonIdentifier_EUI029_AnalyzerLevel()
    {
        var result = Run("C:\\templates\\Probe.ui", "@using EclipseUI.Elements\n<StackPanel Ref={handler.Do} />");

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI029");
        Assert.Contains("simple identifier", diag.GetMessage());
    }

    [Fact]
    public void Ref_QuotedLiteral_EUI029_AnalyzerLevel()
    {
        var result = Run("C:\\templates\\Probe.ui", "@using EclipseUI.Elements\n<StackPanel Ref=\"savePanel\" />");

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI029");
        Assert.Contains("simple identifier", diag.GetMessage());
    }

    // ---- C5：EUI019/EUI020/EUI021 使用点——引用程序集真正 Emit 后被消费（design/14 §14.5.4）----

    [Fact]
    public void ReferencedAssembly_BrokenProperty_IsEUI019AtUseSite()
    {
        var lib = EmitLibrary("ProbeBrokenProp", """
            using EclipseUI;
            namespace ProbeLib;
            public class BrokenBox : global::EclipseUI.Elements.StackPanel
            {
                public double Size { get => 0; set { } }
                public static readonly StyledProperty<int> SizeProperty = StyledProperty<int>.Register<BrokenBox>("Size", 0);
            }
            """);

        var result = Run("C:\\templates\\Probe.ui", "@using ProbeLib\n@using EclipseUI.Elements\n<BrokenBox Size=\"4\" />",
            referencedLibrary: lib);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI019");
        Assert.Contains("global::ProbeLib.BrokenBox", diag.GetMessage());
        Assert.Contains("referenced assembly", diag.GetMessage());
    }

    [Fact]
    public void ReferencedAssembly_BrokenEventField_IsEUI020AtUseSite()
    {
        var lib = EmitLibrary("ProbeBrokenEvent", """
            using EclipseUI;
            namespace ProbeLib;
            public class FaultyBox : global::EclipseUI.Elements.StackPanel
            {
                public static readonly StyledProperty<int> ClickEvent = StyledProperty<int>.Register<FaultyBox>("Click", 0);
            }
            """);

        var result = Run("C:\\templates\\Probe.ui", "@using ProbeLib\n@using EclipseUI.Elements\n<FaultyBox OnClick={Handle} />",
            referencedLibrary: lib);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI020");
        Assert.Contains("global::ProbeLib.FaultyBox", diag.GetMessage());
        Assert.Contains("ClickEvent", diag.GetMessage());
    }

    [Fact]
    public void ReferencedAssembly_BrokenContentParameter_IsEUI021AtUseSite()
    {
        var lib = EmitLibrary("ProbeBrokenContent", """
            using EclipseUI;
            namespace ProbeLib;
            public class BrokenCard : Component
            {
                [UiContent] public string Body { get; set; } = "";
                protected override global::EclipseUI.Element Build() => throw new global::System.NotImplementedException();
            }
            """);

        var result = Run("C:\\templates\\Probe.ui",
            "@using ProbeLib\n@using EclipseUI.Elements\n<StackPanel><BrokenCard><StackPanel /></BrokenCard></StackPanel>",
            referencedLibrary: lib);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI021");
        Assert.Contains("global::ProbeLib.BrokenCard", diag.GetMessage());
        Assert.Contains("referenced assembly", diag.GetMessage());
    }

    [Fact]
    public void ReferencedAssembly_BrokenCard_SelfClosingIsEmitted()
    {
        var lib = EmitLibrary("ProbeBrokenContentClean", """
            using EclipseUI;
            namespace ProbeLib;
            public class BrokenCard : Component
            {
                [UiContent] public string Body { get; set; } = "";
                protected override global::EclipseUI.Element Build() => throw new global::System.NotImplementedException();
            }
            """);

        var result = Run("C:\\templates\\Probe.ui",
            "@using ProbeLib\n@using EclipseUI.Elements\n<StackPanel><BrokenCard /></StackPanel>",
            referencedLibrary: lib);

        Assert.NotEmpty(result.GeneratedSources);            // 无内容 → 不触发 EUI021
        Assert.DoesNotContain(result.Diagnostics, d => d.Id is "EUI019" or "EUI020" or "EUI021" or "EUI029");
    }

    // ---- Tokenizer 切片：控件事件的处理器形态用 Roslyn 解析判定（替代正则，spec/02 §4.6.1 规则 2）----

    private const string TokenizerCodeBehind = "public partial class Probe { private void Say() { } }";

    /// <summary>括号 lambda 包 async/零参数：旧正则整串失败回落 Action；解析后仍正确判 Action。</summary>
    [Fact]
    public void Event_ParenthesizedAsyncLambda_NoParams_IsAction()
    {
        const string ui = "@using EclipseUI.Elements\n<StackPanel><Button OnClick={(async () => Say())} /></StackPanel>";

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, TokenizerCodeBehind));

        Assert.Contains("(global::System.Action)", code);
        Assert.DoesNotContain("RoutedEventArgs>", code);
    }

    /// <summary>括号内的带参 lambda `(e => ...)`：旧正则形态失败判成 Action（cast 编译错）；解析后正确判 RouteArgs。</summary>
    [Fact]
    public void Event_ParenthesizedLambda_SingleParam_IsRouteArgs()
    {
        const string ui = "@using EclipseUI.Elements\n<StackPanel><Button OnClick={(e => Say())} /></StackPanel>";

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, TokenizerCodeBehind));

        Assert.Contains("(global::System.Action<global::EclipseUI.RoutedEventArgs>)", code);
        Assert.Contains("AddHandler(global::EclipseUI.Elements.Button.ClickEvent, e => { h2(e); Invalidate(); });", code);
    }

    /// <summary>匿名方法：`delegate { }` → Action；`delegate (e) { }` → RouteArgs（旧正则整串失败）。</summary>
    [Fact]
    public void Event_AnonymousMethod_ParamsDecideHandlerType()
    {
        const string withoutParams = "@using EclipseUI.Elements\n<StackPanel><Button OnClick={delegate { Say(); }} /></StackPanel>";
        var code = Generated(Run("C:\\templates\\Probe.ui", withoutParams, TokenizerCodeBehind));
        Assert.Contains("(global::System.Action)", code);
        Assert.DoesNotContain("RoutedEventArgs>", code);

        const string withParams = "@using EclipseUI.Elements\n<StackPanel><Button OnClick={delegate (global::EclipseUI.RoutedEventArgs e) { Say(); }} /></StackPanel>";
        var code2 = Generated(Run("C:\\templates\\Probe.ui", withParams, TokenizerCodeBehind));
        Assert.Contains("(global::System.Action<global::EclipseUI.RoutedEventArgs>)", code2);
    }

    /// <summary>`new Action(...)` 顶层的嵌套 lambda 不是处理器形态本身：回落 Action（防未来误用 DescendantSearch）。</summary>
    [Fact]
    public void Event_NestedLambdaInsideObjectCreation_FallsBackToAction()
    {
        const string ui = "@using EclipseUI.Elements\n<StackPanel><Button OnClick={new global::System.Action(() => Say())} /></StackPanel>";

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, TokenizerCodeBehind));

        Assert.Contains("(global::System.Action)", code);
        Assert.DoesNotContain("RoutedEventArgs>", code);
    }

    // ---- C6：具名插槽（spec/02 §4.12.5，design/14 §14.6.3）----

    /// <summary>Probe 自带 [UiSlot]（宿主 <ContentSlot> 用）+ SlotChip 组件（消费 <Slot> 用）。</summary>
    private const string SlotFixtures = """
        using System;
        using System.Collections.Generic;
        using EclipseUI;

        public partial class Probe
        {
            [UiSlot("Header")] public Element? Header { get; set; }
            [UiSlot("Rows")] public System.Collections.Generic.IReadOnlyList<Element> Rows { get; set; } = [];
        }

        public sealed class SlotChip : Component
        {
            protected override Element Build() => new global::EclipseUI.Elements.StackPanel();
            [UiParameter] public string? Title { get; set; }
            [UiParameter] [UiContent] public Element? Body { get; set; }
            [UiSlot("Header")] public Element? Header { get; set; }
            [UiSlot("Rows")] public IReadOnlyList<Element> Rows { get; set; } = [];
        }
        """;

    /// <summary>
    /// 把生成结果真正编译一次：形态断言只能证明文本写了什么，证明不了它能不能编。
    /// 返回 C# 错误清单（含 #line 映射后的 .ui 位置）。
    /// </summary>
    private static List<string> CompileGenerated(string uiPath, string ui, string codeBehind)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiAdditionalText(uiPath, ui) });
        driver.RunGeneratorsAndUpdateCompilation(CreateCompilation(codeBehind), out var output, out _);
        return output.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} {d.Location.GetMappedLineSpan().Path}:{d.Location.GetMappedLineSpan().StartLinePosition.Line} :: {d.GetMessage()}")
            .ToList();
    }

    /// <summary>消费侧 Single：build-once 列表 + 单根取首个 + 先于 Mount() 赋值。</summary>
    [Fact]
    public void SlotFill_SingleSlot_EmitsListAndAssignsFirstRoot()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
              <SlotChip>
                <Slot Name="Header"><TextBlock FontSize="10">a</TextBlock></Slot>
              </SlotChip>
            </StackPanel>
            """;

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, SlotFixtures));

        Assert.Matches(@"var slot\d+ = new global::System\.Collections\.Generic\.List<global::EclipseUI\.Element>\(\);", code);
        Assert.Matches(@"cmp\d+\.Header = slot\d+\.Count == 0 \? null : slot\d+\[0\];", code);
        Assert.Matches(@"slot\d+\.Add\(el\d+\);", code);
        Assert.Matches(@"cmp\d+\.Mount\(\);", code);
    }

    /// <summary>消费侧 Multiple：多填充拼进同一列表；属性整表赋值，且只赋值一次；空填充为空列表 memberships。</summary>
    [Fact]
    public void SlotFill_MultipleSlot_ConcatenatesFills()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
              <SlotChip>
                <Slot Name="Rows"><TextBlock FontSize="10">r1</TextBlock></Slot>
                <Slot Name="Rows"><Button Label="r2" /></Slot>
              </SlotChip>
            </StackPanel>
            """;

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, SlotFixtures));

        var assignments = System.Text.RegularExpressions.Regex.Matches(code, @"cmp\d+\.Rows = slot\d+;");
        Assert.Single(assignments); // 同槽多填充共享一个列表，一次赋值
        Assert.Matches(@"slot\d+\.Add\(el\d+\);\r?\n\W*var el\d+ = new global::EclipseUI\.Elements\.Button", code);
    }

    /// <summary>宿主侧 Single：Build 内联 `if (Header is { } s) parent.Children.Add(s)`——无运行时宿主元素。</summary>
    [Fact]
    public void ContentSlot_HostTemplate_SplicesSingleProperty()
    {
        const string ui = "@using EclipseUI.Elements\n" + """<StackPanel><ContentSlot Name="Header" /></StackPanel>""";

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, SlotFixtures));

        Assert.Matches(@"var el\d+ = new global::EclipseUI\.Elements\.StackPanel\(\);", code);
        Assert.Matches(@"if \(Header is \{ \} slot\d+\)", code);
        Assert.Matches(@"el\d+\.Children\.Add\(slot\d+\);", code);
        Assert.DoesNotContain("ContentSlot", code);
    }

    /// <summary>宿主侧 Multiple：读一次属性逐项加入（no .Content 中转）。</summary>
    [Fact]
    public void ContentSlot_HostTemplate_SplicesMultipleProperty()
    {
        const string ui = "@using EclipseUI.Elements\n" + """<StackPanel><ContentSlot Name="Rows" /></StackPanel>""";

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, SlotFixtures));

        Assert.Matches(@"foreach \(var slot\d+ in Rows\)", code);
        Assert.Matches(@"el\d+\.Children\.Add\(slot\d+\);", code);
    }

    /// <summary>消费侧未知插槽：EUI022（含建议），出错不生成。</summary>
    [Fact]
    public void SlotFill_UnknownSlot_ReportsEUI022_AndGeneratesNothing()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
              <SlotChip>
                <Slot Name="Heade"><TextBlock FontSize="10">a</TextBlock></Slot>
              </SlotChip>
            </StackPanel>
            """;

        var result = Run("C:\\templates\\Probe.ui", ui, SlotFixtures);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI022");
        Assert.Contains("unknown slot 'Heade'", diag.GetMessage());
        Assert.Contains("Header", diag.GetMessage());
        Assert.Empty(result.GeneratedSources);
    }

    /// <summary>宿主侧未知插槽（code-behind 符号为准）：EUI022（含建议）+ 内容不生成。</summary>
    [Fact]
    public void ContentSlot_UnknownSlot_ReportsEUI022_AndGeneratesNothing()
    {
        const string ui = "@using EclipseUI.Elements\n" + """<StackPanel><ContentSlot Name="Heade" /></StackPanel>""";

        var result = Run("C:\\templates\\Probe.ui", ui, SlotFixtures);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI022");
        Assert.Contains("unknown slot 'Heade'", diag.GetMessage());
        Assert.Contains("Header", diag.GetMessage());
        Assert.Empty(result.GeneratedSources);
    }

    /// <summary>宿主缺 <c>Name</c> 属性：EUI022。</summary>
    [Fact]
    public void ContentSlot_MissingName_ReportsEUI022()
    {
        const string ui = "@using EclipseUI.Elements\n" + """<StackPanel><ContentSlot /></StackPanel>""";

        var result = Run("C:\\templates\\Probe.ui", ui, SlotFixtures);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI022");
        Assert.Contains("Name", diag.GetMessage());
        Assert.Empty(result.GeneratedSources);
    }

    // ---- C6：生成结果真正编译（形态断言证明不了可编译性）----

    [Theory]
    [InlineData("""
        @using EclipseUI.Elements
        <StackPanel>
          <SlotChip>
            <Slot Name="Header"><TextBlock FontSize="10">a</TextBlock></Slot>
            <Slot Name="Rows"><TextBlock FontSize="10">r1</TextBlock></Slot>
            <Slot Name="Rows"><Button Label="r2" /></Slot>
          </SlotChip>
        </StackPanel>
        """)]
    [InlineData("@using EclipseUI.Elements\n<StackPanel><ContentSlot Name=\"Header\" /></StackPanel>")]
    [InlineData("@using EclipseUI.Elements\n<StackPanel><ContentSlot Name=\"Rows\" /></StackPanel>")]
    [InlineData("""
        @using EclipseUI.Elements
        <StackPanel>
          <ContentSlot Name="Header" />
          <ContentSlot Name="Rows" />
          <SlotChip Title="t">
            <Button Label="body" />
            <Slot Name="Rows"><TextBlock FontSize="10">r</TextBlock></Slot>
          </SlotChip>
        </StackPanel>
        """)]
    public void SlotCode_GeneratedCompilation_HasNoCSharpErrors(string ui)
    {
        var errors = CompileGenerated("C:\\templates\\Probe.ui", ui, SlotFixtures);
        Assert.Empty(string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void SlotFill_ContentSlotInsideFill_IsEUI022_AndGeneratesNothing()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
              <SlotChip Title="t">
                <Slot Name="Rows"><ContentSlot Name="Rows" /></Slot>
              </SlotChip>
            </StackPanel>
            """;

        var result = Run("C:\\templates\\Probe.ui", ui, SlotFixtures);

        Assert.Contains(result.Diagnostics, d => d.Id == "EUI022");
        Assert.Empty(result.GeneratedSources);
    }

    /// <summary>spec/02 §4.12.5 规则 4：插槽赋值在参数之后、默认内容之后、Mount() 之前。</summary>
    [Fact]
    public void SlotFill_AssignsAfterParametersAndContent_BeforeMount()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
              <SlotChip Title="t">
                <Button Label="body" />
                <Slot Name="Header"><TextBlock FontSize="10">h</TextBlock></Slot>
              </SlotChip>
            </StackPanel>
            """;

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, SlotFixtures));

        // el0 = 根 StackPanel，组件实例取到 cmp1。
        var parameter = code.IndexOf("cmp1.Title = ", StringComparison.Ordinal);
        var content = code.IndexOf("cmp1.Body = ", StringComparison.Ordinal);
        var slot = code.IndexOf("cmp1.Header = ", StringComparison.Ordinal);
        var mount = code.IndexOf("cmp1.Mount();", StringComparison.Ordinal);
        Assert.True(parameter >= 0, "缺少参数赋值");
        Assert.True(content >= 0, "缺少默认内容赋值");
        Assert.True(slot >= 0, "缺少插槽赋值");
        Assert.True(mount >= 0, "缺少 Mount() 调用");
        Assert.True(parameter < content, "插槽切片不得改变参数先于内容的顺序");
        Assert.True(content < slot, "子组件首次 Build() 在 Mount() 内读取插槽属性，赋值必须先于 Mount");
        Assert.True(slot < mount, "插槽赋值必须先于 Mount()");
    }

    // ------------------------------------------------------------------
    // S6 语法对齐的生成形态（K21/K22/K23 ③，D85–D87）
    // ------------------------------------------------------------------

    private const string SwitchFixtures =
        "public partial class Probe { public int mode = 1; public int Mode => 3; }";

    [Fact]
    public void Switch_MultiSection_GeneratesSwitchBlockWithEveryCaseAndDefault()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                @switch (mode) {
                    case 1:
                        <TextBlock>A</TextBlock>
                        break;
                    case 2:
                        <TextBlock>B</TextBlock>
                        break;
                    default:
                        <TextBlock>D</TextBlock>
                        break;
                }
            </StackPanel>
            """;

        var result = Run("C:\\templates\\Probe.ui", ui, SwitchFixtures);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var code = Generated(result);

        Assert.Contains("new global::EclipseUI.SwitchBlock<int>()", code);
        Assert.Contains(".SetSelector(() => mode);", code);
        Assert.Contains(".AddCase(1, ", code);
        Assert.Contains(".AddCase(2, ", code);
        Assert.Contains(".SetDefault(", code);
        // 每个 case 的分支体各自 AddRoot（2 个 case + default = 3 次）
        Assert.Equal(3, CountOccurrences(code, ".AddRoot("));
    }

    [Fact]
    public void Switch_NonConstantMemberLabel_ReportsEUI047_AndGeneratesNothing()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel>
                @switch (mode) {
                    case Mode:
                        <TextBlock>A</TextBlock>
                        break;
                }
            </StackPanel>
            """;

        var result = Run("C:\\templates\\Probe.ui", ui, SwitchFixtures);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI047");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Using_Directives_AreEmittedInTemplateOrder_BetweenNullableAndNamespace()
    {
        const string ui = """
            @using System.Text
            @using EclipseUI.Elements
            @using System.Text

            <StackPanel>
                <TextBlock>x</TextBlock>
            </StackPanel>
            """;

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, "namespace Demo;\npublic partial class Probe { }"));

        var nullable = code.IndexOf("#nullable enable", StringComparison.Ordinal);
        var first = code.IndexOf("using System.Text;", StringComparison.Ordinal);
        var second = code.IndexOf("using EclipseUI.Elements;", StringComparison.Ordinal);
        var ns = code.IndexOf("namespace ", StringComparison.Ordinal);

        Assert.True(nullable >= 0 && first > nullable, "using 组必须紧跟在 #nullable 之后");
        Assert.True(second > first, "using 按模板顺序输出");
        Assert.True(ns > second, "using 组必须在 namespace 之前");
        Assert.Equal(1, CountOccurrences(code, "using System.Text;")); // 去重（K22）
    }

    [Fact]
    public void Using_OnlyTemplateDeclaredNamespaces_AreEmitted()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel><TextBlock>x</TextBlock></StackPanel>
            """;

        var code = Generated(Run("C:\\templates\\Probe.ui", ui));

        Assert.Contains("using EclipseUI.Elements;", code);
        Assert.DoesNotContain("using System;", code);
        Assert.DoesNotContain("global using", code);
        Assert.DoesNotContain("using static", code);
    }

    // ------------------------------------------------------------------
    // K31：@using Alias = Ns.Type（C# 别名 + 模板标签别名）
    // ------------------------------------------------------------------

    private const string AltButtonCodeBehind = """
        namespace Alt.Controls { public class Button : EclipseUI.Elements.StackPanel { } }
        public partial class Probe { }
        """;

    [Fact]
    public void UsingAlias_EmitsFullyQualifiedAlias_AndResolvesTagToTargetType()
    {
        const string ui = """
            @using EclipseUI.Elements
            @using Btn = Alt.Controls.Button

            <StackPanel><Btn /></StackPanel>
            """;

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, AltButtonCodeBehind));

        // 别名目标必须全限定：否则被生成类所在命名空间的同名类型劫持
        Assert.Contains("using Btn = global::Alt.Controls.Button;", code);
        Assert.Contains("new global::Alt.Controls.Button()", code);
        // Alt.Controls 未被 @using 导入，<Btn> 只可能来自别名
        Assert.DoesNotContain("using Alt.Controls;", code);
    }

    [Fact]
    public void UsingAlias_OverridesCatalogTag()
    {
        const string ui = """
            @using EclipseUI.Elements
            @using Button = Alt.Controls.Button

            <StackPanel><Button /></StackPanel>
            """;

        var code = Generated(Run("C:\\templates\\Probe.ui", ui, AltButtonCodeBehind));

        Assert.Contains("new global::Alt.Controls.Button()", code);
        Assert.DoesNotContain("new global::EclipseUI.Elements.Button()", code);
    }

    [Fact]
    public void UsingAlias_IsVisibleToExpressionProbe()
    {
        // spec/02 §4.18 规则 5：探针树必须写入别名行，否则表达式里的别名类型被判「不可判定」
        const string ui = """
            @using EclipseUI.Elements
            @using Btn = Alt.Controls.Button

            <StackPanel>
                <TextBlock>{(Btn?)null is null ? "none" : "some"}</TextBlock>
            </StackPanel>
            """;

        var result = Run("C:\\templates\\Probe.ui", ui, AltButtonCodeBehind);

        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("using Btn = global::Alt.Controls.Button;", Generated(result));
    }

    [Fact]
    public void UsingAlias_UnknownTarget_ReportsEUI048_AndGeneratesNothing()
    {
        const string ui = """
            @using EclipseUI.Elements
            @using Btn = Alt.Controls.Missing

            <StackPanel><Btn /></StackPanel>
            """;

        var result = Run("C:\\templates\\Probe.ui", ui, AltButtonCodeBehind);

        Assert.Contains(result.Diagnostics, d => d.Id == "EUI048");
        Assert.Empty(result.GeneratedSources); // Error 阻断生成，与其它诊断口径一致
    }

    /// <summary>K21/R1：模板在上、<c>@code</c> 在下时，前置表达式必须能看见后置成员（单文件形态）。</summary>
    [Fact]
    public void CodeBlock_BelowTemplate_MembersAreVisibleToLeadingExpressions()
    {
        const string ui = """
            @using EclipseUI.Elements

            <StackPanel Gap={gap}>
                <TextBlock>{message}</TextBlock>
            </StackPanel>

            @code {
                public string message = "hi";
                public double gap = 4;
            }
            """;

        var result = Run("C:\\templates\\Probe.ui", ui, codeBehind: "");

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var code = Generated(result);

        // @code 原文透传进同一个 partial 类（K25）
        Assert.Contains("public string message = \"hi\";", code);
        Assert.Contains("public double gap = 4;", code);
        Assert.Contains("__el0.Gap = gap;", code);
        Assert.Contains("{(message)}", code);
    }

    [Fact]
    public void CodeBlock_MemberCollidingWithGeneratedName_ReportsEUI045_ButStillGenerates()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel><TextBlock>x</TextBlock></StackPanel>

            @code {
                public void Build() { }
            }
            """;

        var result = Run("C:\\templates\\Probe.ui", ui, codeBehind: "");

        Assert.Contains(result.Diagnostics, d => d.Id == "EUI045");
        Assert.Single(result.GeneratedSources); // EUI045 不阻断输出（spec/02 §10）
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
