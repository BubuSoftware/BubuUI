using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EclipseUI.Compiler.Generator;
using EclipseUI.Compiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EclipseUI.Compiler.Tests;

/// <summary>
/// spec/02 §2.7「第二组件来源」：<c>.ui</c> 单独就能构成组件，code-behind 不强制。
/// 目录在输入编译上构建、看不到生成产物，所以组件身份由调用方喂入的声明清单补齐。
/// 这些用例钉的是目录侧判定（把清单喂进去会发生什么）；端到端的「生成器真的喂清单」
/// 在 ProjectSystemTests / 生成器契约测试里。
/// </summary>
public class SecondComponentSourceTests
{
    private static CSharpCompilation Compile(params (string Path, string Text)[] files)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(System.IO.Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(Component).Assembly.Location));
        return CSharpCompilation.Create(
            "SecondSourceProbe",
            files.Select(f => CSharpSyntaxTree.ParseText(f.Text, path: f.Path)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static UiElementDef? Find(UiCatalog catalog, string tag) =>
        catalog.Elements.FirstOrDefault(e => e.Tag == tag);

    [Fact]
    public void WithoutDeclarations_NothingChanges()
    {
        // 没有清单时目录只看符号：无基类的 partial 与纯 .ui 都不在标签表里。
        // 这条同时是「加参数之前的行为」的基线——现有调用点不传清单，行为必须完全一致。
        var catalog = UiCatalogFactory.Build(Compile(
            ("Prefs.cs", "namespace Demo;\npublic partial class Prefs { }\n")));

        Assert.Null(Find(catalog, "Prefs"));
    }

    [Fact]
    public void Declaration_TemplateWithoutTypeBecomesComponent()
    {
        // 纯 .ui：输入编译里连符号都没有，目录合成一份最小形状。
        var catalog = UiCatalogFactory.Build(Compile(), new[]
        {
            new UiComponentDeclaration("Counter", "Gallery"),
        });

        var def = Assert.IsType<UiElementDef>(Find(catalog, "Counter"));
        Assert.Equal(UiElementOrigin.Component, def.Origin);
        Assert.Equal(UiContentModel.None, def.Content);
        Assert.Equal("global::Gallery.Counter", def.ClrTypeDisplayName);
        Assert.Empty(def.Parameters);
        Assert.Empty(def.Properties);
        Assert.False(def.IsFromReferencedAssembly);
    }

    [Fact]
    public void Declaration_CodeBehindWithoutBaseClassBecomesComponent()
    {
        // 有 .cs 但没写基类（Gallery 现状）：认领后走正常 Describe，所以 .cs 里的
        // [UiParameter] 照常进参数表——这正是「想要参数就写 .cs」的依据。
        var catalog = UiCatalogFactory.Build(Compile(
                ("Prefs.cs", "using EclipseUI;\nnamespace Demo;\npublic partial class Prefs " +
                             "{ [UiParameter] public string? Title { get; set; } }\n")),
            new[] { new UiComponentDeclaration("Prefs", "Demo") });

        var def = Assert.IsType<UiElementDef>(Find(catalog, "Prefs"));
        Assert.Equal(UiElementOrigin.Component, def.Origin);
        Assert.True(def.Parameters.ContainsKey("Title"));
    }

    [Fact]
    public void Declaration_NamespaceMismatchDoesNotClaimAndDoesNotSynthesize()
    {
        // @namespace 写错时既不认领真实类型、也不合成一个同名假标签——合成出来的名字
        // 一定指向不存在的类型（CS0246）。命名空间不符由生成器的 EUI056 报，使用点保持 EUI001。
        var catalog = UiCatalogFactory.Build(Compile(
                ("Prefs.cs", "namespace Demo;\npublic partial class Prefs { }\n")),
            new[] { new UiComponentDeclaration("Prefs", "Other") });

        Assert.Null(Find(catalog, "Prefs"));
    }

    [Fact]
    public void Declaration_DoesNotOverrideExplicitBaseClass()
    {
        // 显式派生自 Element 的类型即使配了同 .ui 也仍是控件：清单只补「没写基类」这一条。
        var catalog = UiCatalogFactory.Build(Compile(
                ("Widget.cs", "namespace Demo;\npublic class Widget : EclipseUI.Elements.StackPanel { }\n")),
            new[] { new UiComponentDeclaration("Widget", "Demo") });

        var def = Assert.IsType<UiElementDef>(Find(catalog, "Widget"));
        Assert.Equal(UiElementOrigin.Control, def.Origin);
    }

    [Fact]
    public void Declaration_IsIdempotentForExplicitComponent()
    {
        // 显式 : Component + 清单 → 与只靠基类时同形，不因为清单多出一份重复项。
        var catalog = UiCatalogFactory.Build(Compile(
                ("Card.cs", "namespace Demo;\npublic partial class Card : EclipseUI.Component { }\n")),
            new[] { new UiComponentDeclaration("Card", "Demo") });

        var def = Assert.IsType<UiElementDef>(Find(catalog, "Card"));
        Assert.Equal(UiElementOrigin.Component, def.Origin);
        Assert.Single(catalog.Elements, e => e.Tag == "Card");
    }

    [Fact]
    public void Declaration_GlobalNamespaceTemplateUsesBareName()
    {
        // @namespace 缺失时（EUI055 会报），清单只带标签名；合成名字不能拼出 "global::.Counter"。
        var catalog = UiCatalogFactory.Build(Compile(), new[]
        {
            new UiComponentDeclaration("Counter", null),
        });

        var def = Assert.IsType<UiElementDef>(Find(catalog, "Counter"));
        Assert.Equal("global::Counter", def.ClrTypeDisplayName);
        Assert.Equal(UiElementOrigin.Component, def.Origin);
    }

    // ---------- 端到端：生成器真的把 .ui 清单喂进目录 ----------

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

    /// <summary>
    /// 整个项目（code-behind + 全部 .ui）跑一遍生成器。`@namespace` 由用例自己写在模板里，
    /// 不经夹具改写——这里钉的正是「文件名 + @namespace 就是组件声明」这条路径。
    /// </summary>
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

    private const string CounterUi = """
        @using EclipseUI.Elements
        <StackPanel>
            <TextBlock FontSize="12">0</TextBlock>
        </StackPanel>
        @namespace Gallery
        """;

    private const string ShowcaseCodeBehind = "namespace Gallery;\npublic partial class Showcase { }";

    // 标签可见性按 @using 判定（与 code-behind 组件同口径）：同命名空间也要显式 @using。
    private const string ShowcaseUi = """
        @using EclipseUI.Elements
        @using Gallery
        <StackPanel>
            <Counter />
        </StackPanel>
        @namespace Gallery
        """;

    [Fact]
    public void TemplateOnlyComponent_IsUsableFromAnotherTemplate_AndTheWholeProjectCompiles()
    {
        // 纯 .ui 组件（无 .cs、无 @code）：被别的模板当标签用、生成 new global::Gallery.Counter()，
        // 且「全部生成产物 + code-behind」合成一个真编译单元后零错误——这是 P2 的验收口径。
        var (result, output) = RunProject(ShowcaseCodeBehind,
            ("Counter.ui", CounterUi), ("Showcase.ui", ShowcaseUi));

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));

        var counter = GeneratedSource(result, "Counter");
        Assert.Contains("namespace Gallery", counter);
        Assert.Contains("partial class Counter : global::EclipseUI.Component", counter);

        Assert.Contains("new global::Gallery.Counter()", GeneratedSource(result, "Showcase"));

        var errors = output.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} :: {d.GetMessage()}")
            .ToList();
        Assert.True(errors.Count == 0, "generated project does not compile: " + string.Join(" | ", errors));
    }

    [Fact]
    public void TemplateOnlyComponent_NeedsNoCodeBehindAndNoCodeBlock()
    {
        // EUI005 停用：以前「无 .cs 且无 @code」是错误，现在这是最普通的纯展示组件。
        var (result, _) = RunProject("// no code-behind at all",
            ("Counter.ui", CounterUi));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI005");
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }

    [Fact]
    public void CodeBlockParameterAttribute_WarnsItStaysInsideTheTemplate()
    {
        // spec/02 §2.7：参数必须写在 .cs——@code 里的 [UiParameter] 进不了目录，
        // 别的模板用它只会得到 EUI002。给一条 Warning 把这件事说清楚（不短路产物）。
        const string ui = """
            @using EclipseUI
            @using EclipseUI.Elements
            <StackPanel />
            @namespace Gallery
            @code {
                [UiParameter] public string? Title { get; set; }
            }
            """;

        var (result, _) = RunProject("// no code-behind", ("Card.ui", ui));

        var warning = Assert.Single(result.Diagnostics, d => d.Id == "EUI057");
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("UiParameter", warning.GetMessage(), StringComparison.Ordinal);
        Assert.Single(result.GeneratedSources);               // 警告不短路
    }

    [Fact]
    public void CodeBlockOrdinaryMembers_DoNotWarn()
    {
        const string ui = """
            @using EclipseUI.Elements
            <StackPanel />
            @namespace Gallery
            @code {
                public int count;
                private void Bump() { count++; }
            }
            """;

        var (result, _) = RunProject("// no code-behind", ("Counter.ui", ui));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI057");
    }

    [Fact]
    public void TemplateComponents_ChangingBodyOnly_KeepsCatalogValueStable()
    {
        // 清单只含 (Tag, @namespace)：改正文不该让目录重建，否则每敲一个字都要重算整个标签表。
        var file = new TextFile("C:\\templates\\Counter.ui", CounterUi);
        var compilation = Compile(("CodeBehind.cs", ShowcaseCodeBehind));
        var driver = (CSharpGeneratorDriver)CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { file }).RunGenerators(compilation);

        var edited = new TextFile(file.Path, CounterUi.Replace("FontSize=\"12\"", "FontSize=\"14\"", StringComparison.Ordinal));
        var result = driver.ReplaceAdditionalText(file, edited)
            .RunGenerators(compilation).GetRunResult().Results.Single();

        // 清单步骤确实重跑了（AdditionalText 变了），但产出值相等 → Unchanged，不传播为 Modified/New。
        Assert.All(ReasonsOf(result, "TemplateComponents"),
            reason => Assert.Equal(IncrementalStepRunReason.Unchanged, reason));
        // 目录因此不重建：下游拿到缓存值。
        Assert.All(ReasonsOf(result, "TemplateCatalog"),
            reason => Assert.True(reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));

        static IEnumerable<IncrementalStepRunReason> ReasonsOf(GeneratorRunResult result, string step) =>
            result.TrackedSteps.TryGetValue(step, out var steps)
                ? steps.SelectMany(s => s.Outputs).Select(o => o.Reason)
                : Enumerable.Empty<IncrementalStepRunReason>();
    }
}
