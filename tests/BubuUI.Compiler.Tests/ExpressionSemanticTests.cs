using System;
using System.Linq;
using System.Threading;
using BubuUI.Compiler.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace BubuUI.Compiler.Tests;

/// <summary>
/// EUI049 契约（表达式语义分析最小闭环）：属性 {expr}、@if 条件、插值文本段做探针编译，
/// 句内 CS 错误在使用点报 EUI049。边界：bind/事件/Ref 各有专属探针不重复；@foreach 项类型
/// 不可推断仍由 EUI007 负责；生成代码仍经 #line 由 C# 兜底。
/// </summary>
public class ExpressionSemanticTests
{
    private const string UiPath = "/templates/Probe.bui";

    private const string CodeBehind = """
        using BubuUI;

        public partial class Probe : global::BubuUI.Elements.StackPanel
        {
            public int count;
            public string name = "";
            public static int StaticValue = 5;
            public System.Collections.Generic.List<string> tasks = new();
        }
        """;

    private sealed class UiAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public UiAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(NamespaceDeclarationFixture.Ensure(path, content));
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }

    private static GeneratorRunResult Run(string ui)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(global::BubuUI.UiObject).Assembly.Location));

        var fixture = NamespaceDeclarationFixture.Prepare(UiPath, CodeBehind);

        var compilation = CSharpCompilation.Create(
            "TemplateProbe",
            new[] { CSharpSyntaxTree.ParseText(fixture.CodeBehind, path: "Probe.cs") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { new UiAdditionalText(UiPath, fixture.Ui(ui)) });
        return driver.RunGenerators(compilation).GetRunResult().Results.Single();
    }

    [Fact]
    public void UnknownMember_InAttribute_ReportsEUI049()
    {
        var result = Run("@using BubuUI.Elements\n<TextBlock FontSize={conut} />");

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI049");
        Assert.Contains("conut", diag.GetMessage());
        Assert.Contains("CS0103", diag.GetMessage());
    }

    [Fact]
    public void TypeMismatch_InAttribute_ReportsEUI049()
    {
        var result = Run("@using BubuUI.Elements\n<TextBlock FontSize={name} />");

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI049");
        Assert.Contains("CS0029", diag.GetMessage());
    }

    [Fact]
    public void UnknownMember_InIfCondition_ReportsEUI049()
    {
        var result = Run("@using BubuUI.Elements\n@if (shoButton) { <TextBlock>x</TextBlock> }");

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI049");
        Assert.Contains("shoButton", diag.GetMessage());
    }

    [Fact]
    public void UnknownMember_InTextSegment_ReportsEUI049()
    {
        var result = Run("@using BubuUI.Elements\n<TextBlock>count is {naem}</TextBlock>");

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI049");
        Assert.Contains("naem", diag.GetMessage());
    }

    [Fact]
    public void UnknownItemMember_InForeach_ReportsEUI049()
    {
        var result = Run("""
            @using BubuUI.Elements
            <StackPanel>
                @foreach (var t in tasks) {
                    <TextBlock>{t.Nmae}</TextBlock>
                }
            </StackPanel>
            """);

        var diag = Assert.Single(result.Diagnostics, d => d.Id == "EUI049");
        Assert.Contains("t.Nmae", diag.GetMessage());
    }

    [Fact]
    public void ValidExpressions_ProduceNoEUI049()
    {
        var result = Run("""
            @using BubuUI.Elements
            <StackPanel>
                @if (count > 0) {
                    <TextBlock FontSize={StaticValue}>{name} has {count}</TextBlock>
                }
                @foreach (var t in tasks) {
                    <TextBlock>{t}</TextBlock>
                }
            </StackPanel>
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI049");
    }

    [Fact]
    public void Bind_Event_Ref_Attributes_AreNotDuplicated()
    {
        // bind 源错误归 EUI037、事件归 EUI024（不可判定则 C# 兜底）、Ref 归 EUI029——
        // EUI049 对这三类属性必须沉默，避免同一错误两条诊断。
        var result = Run("""
            @using BubuUI.Elements
            <StackPanel>
                <CheckBox @bind-Checked={nope} />
                <Button OnClick={nopeHandler}>x</Button>
                <StackPanel Ref={nopeField} />
            </StackPanel>
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI049");
    }
}
