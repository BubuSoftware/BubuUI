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
/// 控件 = 具名 <c>{P}Property</c> 的公开可写属性；组件 = 可写且有 <c>{P}Changed</c> 的
/// <c>[UiParameter]</c>。本文件盯的是**不可绑目标**的报法：
/// 名字存在 → EUI040 带原因；名字不存在 → 仍 EUI002。两者混为一谈会让人去改拼写。
/// </summary>
public class BindTargetTests
{
    private const string UiPath = "C:\\templates\\Probe.ui";

    private const string Controls =
        "public partial class Probe { public bool flag; public string text = \"\"; }";

    private const string WithComponent = """
        using System;
        using EclipseUI;

        public partial class Probe { public bool flag; public string text = ""; }

        public class Card : Component
        {
            public string Plain { get; set; } = "";
            [UiParameter] public string? Title { get; set; }
            [UiParameter] public string? Color { get; set; }
            [UiParameter] public Action<string?>? ColorChanged { get; set; }
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

    private static GeneratorRunResult Run(string ui, string codeBehind = Controls)
    {
        var fixture = NamespaceDeclarationFixture.Prepare(UiPath, codeBehind);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Element).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "BindTargetProbe",
            new[] { CSharpSyntaxTree.ParseText(fixture.CodeBehind, path: "Probe.cs") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

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
    public void ComponentParameterWithChanged_IsAccepted()
    {
        var result = Run(Ui("@bind-Color={text}", "Card"), WithComponent);

        Assert.Empty(result.Diagnostics);
        Assert.Contains("ColorChanged", result.GeneratedSources.Single().SourceText.ToString());
    }

    // ---- 名字存在但不可绑：EUI040 + 原因 ----

    [Theory]
    // 有 public get/set 但没有 {P}Property（IsEnabled 是普通 CLR 属性）
    [InlineData("IsEnabled", "IsEnabledProperty")]
    // 只有 getter（Parent）/ internal setter（IsHovered）
    [InlineData("Parent", "no public setter")]
    [InlineData("IsHovered", "no public setter")]
    // 有 public get/set 但没有 StyledProperty 字段
    [InlineData("Classes", "ClassesProperty")]
    public void ExistingButUnbindableProperty_ReportsEui040WithReason(string name, string reason)
    {
        var result = Run(Ui($"@bind-{name}={{flag}}"));

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Contains($"@bind-{name}", diag.GetMessage());
        Assert.Contains($"'{name}'", diag.GetMessage());
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
    public void ComponentMemberThatIsNotAUiParameter_ReportsEui040()
    {
        var result = Run(Ui("@bind-Plain={text}", "Card"), WithComponent);

        Assert.Empty(result.GeneratedSources);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Contains("'Plain'", diag.GetMessage());
        Assert.Contains("[UiParameter]", diag.GetMessage());
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
        var result = Run(Ui("@bind-Nope={text}", "Card"), WithComponent);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI040");
        Assert.Contains(result.Diagnostics, d => d.Id == "EUI002");
    }

    // ---- 赋值（非 @bind）也受益于同一份「不可绑」表 ----

    [Theory]
    [InlineData("IsEnabled=\"true\"", "no 'IsEnabledProperty'")]
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

    // ---- 目录本身：可绑表与非可绑表互补 ----

    [Fact]
    public void Catalog_RecordsWhyKnownMembersAreNotBindTargets()
    {
        var button = EclipseUI.TestKit.UiTestCatalogs.BuiltIn().Elements.Single(e => e.Tag == "Button");

        Assert.Contains("Label", button.Properties.Keys);
        Assert.Null(button.BindRejection("Label"));
        Assert.DoesNotContain("Label", button.NonBindableProperties.Keys);

        Assert.Contains("IsEnabledProperty", button.BindRejection("IsEnabled")!);
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
