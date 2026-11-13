using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EclipseUI.Compiler;
using EclipseUI.Compiler.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

/// <summary>
/// <c>@bind-P</c> 的目标必须「支持绑定」（spec/01 §4.5）：
/// 公开可写属性通过具名 <c>{P}Property</c> 或同类型 <c>{P}Changed</c> 回调支持双向绑定。
/// 普通 CLR setter 可用于单向赋值，不要求声明 <c>[UiParameter]</c>。本文件盯的是不可绑目标的报法：
/// 名字存在 → EUI040 带原因；名字不存在 → 仍 EUI002。两者混为一谈会让人去改拼写。
/// </summary>
public class BindTargetTests
{
    private const string UiPath = "C:\\templates\\Probe.ui";

    private const string Controls =
        "public partial class Probe { public bool flag; public string text = \"\"; }";

    private const string WithControl = """
        using System;
        using EclipseUI;

        public partial class Probe { public bool flag; public string text = ""; }

        public class Card : global::EclipseUI.Elements.StackPanel
        {
            public string Plain { get; set; } = "";
            public string? Title { get; set; }
            public string? Color { get; set; }
            public Action<string?>? ColorChanged { get; set; }
        }
        """;

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

    private static CSharpCompilation Compile(string codeBehind, out UiFixture fixture)
    {
        fixture = NamespaceDeclarationFixture.Prepare(UiPath, codeBehind);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Element).Assembly.Location));

        return CSharpCompilation.Create(
            "BindTargetProbe",
            new[] { CSharpSyntaxTree.ParseText(fixture.CodeBehind, path: "Probe.cs") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static CSharpCompilation Compile(string codeBehind) => Compile(codeBehind, out _);

    private static GeneratorRunResult Run(string ui, string codeBehind = Controls)
    {
        var compilation = Compile(codeBehind, out var fixture);

        var driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiAdditionalText(UiPath, fixture.Ui(ui)) });
        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    private static string Ui(string attribute, string tag = "Button") =>
        "@using EclipseUI.Elements\n" + $"<StackPanel><{tag} {attribute} /></StackPanel>";

    // ---- 可绑目标：不报错、真的生成绑定 ----

    [Fact]
    public void BindableProperty_IsAcceptedAndEmitsTwoWayBinding()
    {
        var result = Run(Ui("@bind-Label={text}"));

        Assert.Empty(result.Diagnostics);
        var code = result.GeneratedSources.Single().SourceText.ToString();
        Assert.Contains("global::EclipseUI.UiBinding.TwoWay(", code);
        Assert.Contains("readTarget: () => ", code);
        Assert.Contains("writeSource: v => text = v", code);
    }

    [Fact]
    public void PlainPropertyWithChanged_IsAccepted()
    {
        var result = Run(Ui("@bind-Color={text}", "Card"), WithControl);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("ColorChanged", result.GeneratedSources.Single().SourceText.ToString());
    }

    // ---- 有 {P}Changed 但形状不对：仍是 EUI040，且说清期望形状（D110） ----

    /// <summary>
    /// 只判「是委托」会放过 <c>Action</c>：分析器放行，生成 <c>cmp0.CountChanged = v =&gt; …</c>
    /// 才由 C# 报 CS1593——「编辑器绿、build 红」。扫描期按符号判 arity，使用点就能说清。
    /// </summary>
    [Fact]
    public void ChangedCallbackWithNoPayload_ReportsEui040WithExpectedShape()
    {
        const string codeBehind = """
            public partial class Probe { public int count; }

            public class Counter : global::EclipseUI.Elements.StackPanel
            {
                [global::EclipseUI.UiParameter] public int Count { get; set; }
                [global::EclipseUI.UiParameter] public global::System.Action? CountChanged { get; set; }
            }
            """;

        var result = Run(Ui("@bind-Count={count}", "Counter"), codeBehind);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Contains("'CountChanged'", diag.GetMessage());
        Assert.Contains("Action<int>", diag.GetMessage());
        Assert.Contains("takes 0 argument(s)", diag.GetMessage());
        // 形状问题不是声明错误：参数本身合法，只是不能做双向目标。
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI028");
    }

    [Fact]
    public void ChangedCallbackWithDifferentPayloadType_ReportsEui040()
    {
        const string codeBehind = """
            public partial class Probe { public int count; }

            public class Counter : global::EclipseUI.Elements.StackPanel
            {
                [global::EclipseUI.UiParameter] public int Count { get; set; }
                [global::EclipseUI.UiParameter] public global::System.Action<string?>? CountChanged { get; set; }
            }
            """;

        var result = Run(Ui("@bind-Count={count}", "Counter"), codeBehind);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Contains("'CountChanged'", diag.GetMessage());
        Assert.Contains("but 'Count' is", diag.GetMessage());
        Assert.Contains("must be the same type", diag.GetMessage());
    }

    /// <summary>
    /// 形状不对的回调不进双向通道：这是补全与生成的共同输入（<c>ChangedCallbackName</c> 为 null）。
    /// 若这里回填了名字，补全会把不可绑的目标列出来——那正是 EUI040 与候选列表打架的老问题。
    /// </summary>
    [Fact]
    public void Catalog_RejectsChangedCallbackWithWrongShape()
    {
        const string codeBehind = """
            public partial class Probe { public int count; }

            public class Counter : global::EclipseUI.Elements.StackPanel
            {
                [global::EclipseUI.UiParameter] public int Count { get; set; }
                [global::EclipseUI.UiParameter] public global::System.Action? CountChanged { get; set; }

                [global::EclipseUI.UiParameter] public int Total { get; set; }
                [global::EclipseUI.UiParameter] public global::System.Action<int>? TotalChanged { get; set; }
            }
            """;

        var result = Run("@using EclipseUI.Elements\n<StackPanel><Counter /></StackPanel>", codeBehind);
        var catalog = EclipseUI.Compiler.Metadata.UiCatalogFactory.Build(Compile(codeBehind));
        var counter = catalog.Elements.Single(e => e.Tag == "Counter");

        Assert.Empty(result.Diagnostics);                              // 不声明 @bind 时一切正常
        Assert.Null(counter.Parameters["Count"].ChangedCallbackName);
        Assert.Contains("Action<int>", counter.Parameters["Count"].ChangedShapeProblem!);
        // 形状正确的照旧：回填名字、无形状问题——上面的收紧没有扩大化。
        Assert.Equal("TotalChanged", counter.Parameters["Total"].ChangedCallbackName);
        Assert.Null(counter.Parameters["Total"].ChangedShapeProblem);
    }

    // ---- 名字存在但不可绑：EUI040 + 原因 ----

    [Theory]
    // 普通 CLR setter 可赋值；缺少 Changed 回调时仍不可双向绑定。
    [InlineData("IsEnabled", "IsEnabledChanged")]
    // 只有 getter（Parent）/ internal setter（IsHovered）
    [InlineData("Parent", "no public setter")]
    [InlineData("IsHovered", "no public setter")]
    // 有 public get/set 但没有 StyledProperty 字段
    [InlineData("Classes", "ClassesChanged")]
    public void ExistingButUnbindableProperty_ReportsEui040WithReason(string name, string reason)
    {
        var result = Run(Ui($"@bind-{name}={{flag}}"));

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Contains($"@bind-{name}", diag.GetMessage());
        Assert.Contains(name, diag.GetMessage());
        Assert.Contains(reason, diag.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI002");
    }

    [Fact]
    public void EventTarget_ReportsEui040AsEvent()
    {
        var result = Run(Ui("@bind-OnClick={flag}"));

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Contains("is an event", diag.GetMessage());
    }

    [Fact]
    public void PlainPropertyWithoutChanged_ReportsEui040()
    {
        var result = Run(Ui("@bind-Plain={text}", "Card"), WithControl);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Contains("@bind-Plain", diag.GetMessage());
        Assert.Contains("PlainChanged", diag.GetMessage());
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI002");
    }

    // ---- 名字不存在：仍是 EUI002（拼写问题，不是可绑性问题） ----

    [Theory]
    [InlineData("Labell")]
    [InlineData("Checkd")]
    public void UnknownTarget_StillReportsEui002(string name)
    {
        var result = Run(Ui($"@bind-{name}={{flag}}"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Contains(result.Diagnostics, d => d.Id == "EUI002");
    }

    [Fact]
    public void UnknownComponentMember_StillReportsEui002()
    {
        var result = Run(Ui("@bind-Nope={text}", "Card"), WithControl);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Contains(result.Diagnostics, d => d.Id == "EUI002");
    }

    // ---- 赋值（非 @bind）也受益于同一份「不可绑」表 ----

    [Theory]
    [InlineData("Parent=\"x\"", "read-only")]
    public void AssigningToKnownButUnexposedProperty_ExplainsWhyInsteadOfTypoHint(string attribute, string reason)
    {
        // spec/01 §4.2：名字存在但没进表面时说原因，别再拿 "did you mean" 暗示拼写错了。
        var result = Run(Ui(attribute));

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI002");
        Assert.Contains(reason, diag.GetMessage());
        Assert.DoesNotContain("did you mean", diag.GetMessage());
    }

    [Fact]
    public void AssigningToUnknownProperty_StillSuggestsTheNearestName()
    {
        var result = Run(Ui("Lable=\"x\""));

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI002");
        Assert.Contains("did you mean 'Label'", diag.GetMessage());
    }

    [Fact]
    public void AssigningToPlainPublicSetter_IsAccepted()
    {
        var result = Run(Ui("IsEnabled=\"false\""));

        Assert.Empty(result.Diagnostics);
        Assert.Contains(".IsEnabled = false;", result.GeneratedSources.Single().SourceText.ToString());
    }

    [Fact]
    public void AssigningToUnannotatedControlProperty_IsAccepted()
    {
        var result = Run(Ui("Plain=\"value\"", "Card"), WithControl);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(".Plain = \"value\";", result.GeneratedSources.Single().SourceText.ToString());
    }

    // ---- 目录本身：可绑表与非可绑表互补 ----

    [Fact]
    public void Catalog_RecordsWhyKnownMembersAreNotBindTargets()
    {
        var button = EclipseUI.TestKit.UiTestCatalogs.BuiltIn().Elements.Single(e => e.Tag == "Button");

        Assert.Contains("Label", button.Properties.Keys);
        Assert.Null(button.BindRejection("Label"));
        Assert.DoesNotContain("Label", button.NonBindableProperties.Keys);

        Assert.True(button.Parameters["IsEnabled"].CanWrite);
        Assert.Null(button.Parameters["IsEnabled"].ChangedCallbackName);
        Assert.Null(button.BindRejection("IsEnabled"));
        Assert.Contains("no public setter", button.BindRejection("Parent")!);
        // 事件有专门的分支，不该混进「属性但不可绑」表
        Assert.Null(button.BindRejection("OnClick"));
        // 压根不存在的名字两边都不进——使用点才落 EUI002
        Assert.Null(button.BindRejection("Labell"));
        Assert.DoesNotContain(button.Properties.Keys, key => key == "Labell");
    }

    [Fact]
    public void MalformedStyledPropertyField_ExplainsTheMismatch()
    {
        const string codeBehind = """
            public partial class Probe { public bool flag; }

            public class Badge : global::EclipseUI.Element
            {
                public static readonly global::EclipseUI.StyledProperty<int> CountProperty =
                    global::EclipseUI.StyledProperty<int>.Register<Badge>("Count", 0);

                public string Count { get; set; } = "";
            }
            """;

        var result = Run(Ui("@bind-Count={text}", "Badge"), codeBehind);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Contains("StyledProperty<", diag.GetMessage());
        Assert.Contains("CountProperty", diag.GetMessage());
    }
}
