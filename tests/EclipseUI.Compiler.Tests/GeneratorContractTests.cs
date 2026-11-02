using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    private static GeneratorRunResult Run(string uiPath, string ui, string? codeBehind = null)
        => RunWithReferences(uiPath, ui, codeBehind ??
            "public partial class Probe { public int gap = 4; public string message = \"start\"; }");

    private static CSharpCompilation CreateCompilation(string codeBehind)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Component).Assembly.Location));
        return CSharpCompilation.Create(
            "TemplateProbe",
            new[] { CSharpSyntaxTree.ParseText(codeBehind) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static GeneratorRunResult RunWithReferences(string uiPath, string ui, string codeBehind)
    {
        var driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiAdditionalText(uiPath, ui) });
        var compilation = CreateCompilation(codeBehind);
        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    private static string Generated(GeneratorRunResult result)
    {
        Assert.Null(result.Exception);
        return result.GeneratedSources.Single().SourceText.ToString();
    }

    [Fact]
    public void Generation_IsDeterministic_WithStableHint()
    {
        const string ui = """
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
        Assert.Contains("#line 1 \"C:\\templates\\Probe.ui\"", code);
        Assert.Contains("#line 2 \"C:\\templates\\Probe.ui\"", code);
        Assert.Contains("__el0.Gap = gap;", code);
        Assert.Contains("__run2.Text = $\"{(message)}\";", code);
    }

    [Fact]
    public void TypeError_InDynamicBinding_MapsBackToUiLine()
    {
        const string uiPath = "C:\\templates\\Probe.ui";
        const string ui = """
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
        var details = string.Join(Environment.NewLine,
            errors.Select(e => $"{e.Id} {e.Location.GetMappedLineSpan()} :: {e.GetMessage()}"));
        var typeError = Assert.Single(errors, d => d.Id == "CS0029");
        var lineSpan = typeError.Location.GetMappedLineSpan();
        Assert.Equal(uiPath, lineSpan.Path);
        Assert.Equal(0, lineSpan.StartLinePosition.Line);
    }

    [Fact]
    public void ControlEvent_EmitsHandlerLocal_AndInvalidateWrapper()
    {
        const string ui = "<StackPanel><Button OnClick={() => Bump()}>go</Button></StackPanel>";
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
        const string ui = "<StackPanel>@if (show) { }</StackPanel>";
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
        const string ui = "<StackPanel><CheckBox Checked:bind={flag}>x</CheckBox></StackPanel>";
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
            <StackPanel>
                @if (show) {
                    <CheckBox Checked:bind={flag}>x</CheckBox>
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
        const string ui = "<StackPanel>@foreach (var x in count) { <TextBlock>{message}</TextBlock> }</StackPanel>";
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
        var ui = $"<StackPanel><CheckBox @bind-Checked={{{expression}}}>x</CheckBox></StackPanel>";
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
        var ui = $"<StackPanel><CheckBox @bind-Checked={{{expression}}}>x</CheckBox></StackPanel>";
        var code = Generated(RunWithReferences("C:\\templates\\Probe.ui", ui, BindFixtures));

        Assert.Contains($"writeSource: v => {expression} = v", code);
    }

    [Fact]
    public void BindNewSyntax_EmitsIdenticalCodeToLegacy_AndIsDiagnosticFree()
    {
        var legacy = RunWithReferences("C:\\templates\\Probe.ui",
            "<StackPanel><CheckBox Checked:bind={flag}>x</CheckBox></StackPanel>", BindFixtures);
        var modern = RunWithReferences("C:\\templates\\Probe.ui",
            "<StackPanel><CheckBox @bind-Checked={flag}>x</CheckBox></StackPanel>", BindFixtures);

        Assert.Equal(Generated(legacy), Generated(modern));
        Assert.Empty(modern.Diagnostics);
    }

    [Fact]
    public void LegacyBind_ReportsEUI043Warning_AndStillGenerates()
    {
        var result = RunWithReferences("C:\\templates\\Probe.ui",
            "<StackPanel><CheckBox Checked:bind={flag}>x</CheckBox></StackPanel>", BindFixtures);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI043");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, diag.Severity);
        Assert.Contains("@bind-Checked", diag.GetMessage());
        Assert.Contains("UiBinding.TwoWay", Generated(result));
    }

    [Fact]
    public void LegacyValue_ReportsEUI043Warning_AndEmitsPlainAssignment()
    {
        var result = RunWithReferences("C:\\templates\\Probe.ui",
            "<StackPanel Gap:value=\"8\" />", BindFixtures);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI043");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, diag.Severity);
        Assert.Contains("Gap=", diag.GetMessage());

        var plain = RunWithReferences("C:\\templates\\Probe.ui", "<StackPanel Gap=\"8\" />", BindFixtures);
        Assert.Empty(plain.Diagnostics);
        Assert.Equal(Generated(result), Generated(plain)); // 旧别名与新形态生成文本相同
    }

    [Fact]
    public void Bind_InvalidTarget_ReportsEUI040_AndGeneratesNothing()
    {
        var result = RunWithReferences("C:\\templates\\Probe.ui",
            "<StackPanel><Button @bind-OnClick={() => flag}>x</Button></StackPanel>", BindFixtures);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diag.Severity);
    }

    [Fact]
    public void Bind_OnItemMember_IsEntryScopedAndWritesThroughItem()
    {
        const string ui = """
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
        var ui = $"<{tag} {attribute}={{() => Bump()}} />";
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
    /// 偏差（台账）：Roslyn 4.9.2 公开的 <c>IncrementalGeneratorRunStep</c> 只有
    /// Name/Inputs/Outputs/ElapsedTime，跟踪结果里没有 step reason，
    /// 因此 <c>IncrementalStepRunReason.Cached/Unchanged</c> 的直接断言在公开 API 下写不出来；
    /// 这里的替代证据是「管道分步 + 目录按值相等 + 每轮扫描耗时」。
    /// </summary>
    [Fact]
    public void Incremental_TemplateOnlyChange_KeepsCatalogInput_AndUpdatesSource()
    {
        var uiPath = Path.Combine(AppContext.BaseDirectory, "Probe.ui");
        var initial = new UiAdditionalText(uiPath, "<StackPanel Gap=\"4\" />");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { initial },
            driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));
        var compilation = CreateCompilation("public partial class Probe { public int a = 1; }");

        var first = driver.RunGenerators(compilation).GetRunResult().Results.Single();
        driver = driver.ReplaceAdditionalText(initial, new UiAdditionalText(uiPath, "<StackPanel Gap=\"9\" />"));
        var second = driver.RunGenerators(compilation).GetRunResult().Results.Single();

        Assert.Empty(first.Diagnostics);
        Assert.Empty(second.Diagnostics);
        Assert.Contains(".Gap = 4d;", Generated(first));
        Assert.Contains(".Gap = 9d;", Generated(second));

        // 目录扫描的上游是 Compilation；附加文本走另一条根
        Assert.Contains("Compilation", second.TrackedSteps.Keys);
        Assert.Contains("AdditionalTexts", second.TrackedSteps.Keys);
        Assert.Contains("SourceOutput", second.TrackedSteps.Keys);
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
}
