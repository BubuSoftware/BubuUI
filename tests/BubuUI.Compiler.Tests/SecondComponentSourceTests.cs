using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BubuUI.Compiler;
using BubuUI.Compiler.Generator;
using BubuUI.Compiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace BubuUI.Compiler.Tests;

/// <summary>
/// spec/02 §2.7「第二组件来源」：<c>.bui</c> 单独就能构成组件，code-behind 不强制。
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
        references.Add(MetadataReference.CreateFromFile(typeof(UiObject).Assembly.Location));
        return CSharpCompilation.Create(
            "SecondSourceProbe",
            files.Select(f => CSharpSyntaxTree.ParseText(f.Text, path: f.Path)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static UiElementDef? Find(UiCatalog catalog, string tag) =>
        catalog.Elements.FirstOrDefault(e => e.Tag == tag);

    /// <summary>用共享扫描路径构造单文件目录（K34：走的就是生产代码的 FromFile 提取）。</summary>
    private static UiCatalog CatalogOf(string path, string ui)
    {
        var declaration = UiTemplateScanner.FromFile(path, ui);
        Assert.NotNull(declaration);
        return UiCatalogFactory.Build(Compile(), new[] { declaration });
    }

    [Fact]
    public void WithoutDeclarations_NothingChanges()
    {
        // 没有清单时目录只看符号：无基类的 partial 与纯 .bui 都不在标签表里。
        // 这条同时是「加参数之前的行为」的基线——现有调用点不传清单，行为必须完全一致。
        var catalog = UiCatalogFactory.Build(Compile(
            ("Prefs.cs", "namespace Demo;\npublic partial class Prefs { }\n")));

        Assert.Null(Find(catalog, "Prefs"));
    }

    [Fact]
    public void Declaration_TemplateWithoutTypeBecomesComponent()
    {
        // 纯 .bui：输入编译里连符号都没有，目录合成一份最小形状。
        var catalog = UiCatalogFactory.Build(Compile(), new[]
        {
            new UiTemplateDeclaration("Counter", "Gallery") { RootTag = "BubuUI.Elements.StackPanel" },
        });

        var def = Assert.IsType<UiElementDef>(Find(catalog, "Counter"));
        Assert.True(def.IsEmbeddable);
        Assert.Equal(UiContentModel.Children, def.Content);
        Assert.Equal("global::Gallery.Counter", def.ClrTypeDisplayName);
        Assert.Contains("IsEnabled", def.Parameters.Keys);
        Assert.Contains("Gap", def.Properties.Keys);
        Assert.False(def.IsFromReferencedAssembly);
    }

    [Fact]
    public void Declaration_CodeBehindWithoutBaseClassBecomesComponent()
    {
        // 有 .cs 但没写基类（Gallery 现状）：认领后走正常 Describe，所以 .cs 里的
        // [UiParameter] 照常进参数表——这正是「想要参数就写 .cs」的依据。
        var catalog = UiCatalogFactory.Build(Compile(
                ("Prefs.cs", "using BubuUI;\nnamespace Demo;\npublic partial class Prefs " +
                             "{ [UiParameter] public string? Title { get; set; } }\n")),
            new[] { new UiTemplateDeclaration("Prefs", "Demo") { RootTag = "BubuUI.Elements.StackPanel" } });

        var def = Assert.IsType<UiElementDef>(Find(catalog, "Prefs"));
        Assert.True(def.IsEmbeddable);
        Assert.True(def.Parameters.ContainsKey("Title"));
    }

    [Fact]
    public void Declaration_NamespaceMismatchDoesNotClaimAndDoesNotSynthesize()
    {
        // @namespace 写错时既不认领真实类型、也不合成一个同名假标签——合成出来的名字
        // 一定指向不存在的类型（CS0246）。命名空间不符由生成器的 EUI056 报，使用点保持 EUI001。
        var catalog = UiCatalogFactory.Build(Compile(
                ("Prefs.cs", "namespace Demo;\npublic partial class Prefs { }\n")),
            new[] { new UiTemplateDeclaration("Prefs", "Other") });

        Assert.Null(Find(catalog, "Prefs"));
    }

    [Fact]
    public void Declaration_DoesNotOverrideExplicitBaseClass()
    {
        // 显式派生自 Element 的类型即使配了同 .bui 也仍是控件：清单只补「没写基类」这一条。
        var catalog = UiCatalogFactory.Build(Compile(
                ("Widget.cs", "namespace Demo;\npublic class Widget : BubuUI.Elements.StackPanel { }\n")),
            new[] { new UiTemplateDeclaration("Widget", "Demo") });

        var def = Assert.IsType<UiElementDef>(Find(catalog, "Widget"));
        Assert.True(def.IsEmbeddable);
    }

    [Fact]
    public void Declaration_IsIdempotentForExplicitComponent()
    {
        // 显式 : global::BubuUI.Elements.StackPanel + 清单 → 与只靠基类时同形，不因为清单多出一份重复项。
        var catalog = UiCatalogFactory.Build(Compile(
                ("Card.cs", "namespace Demo;\npublic partial class Card : BubuUI.Elements.StackPanel { }\n")),
            new[] { new UiTemplateDeclaration("Card", "Demo") });

        var def = Assert.IsType<UiElementDef>(Find(catalog, "Card"));
        Assert.True(def.IsEmbeddable);
        Assert.Single(catalog.Elements, e => e.Tag == "Card");
    }

    [Fact]
    public void Declaration_WithoutNamespaceDoesNotCreateAnUnusableType()
    {
        // @namespace 缺失时（EUI055 会报），清单只带标签名；合成名字不能拼出 "global::.Counter"。
        var catalog = UiCatalogFactory.Build(Compile(), new[]
        {
            new UiTemplateDeclaration("Counter", null),
        });

        Assert.Null(Find(catalog, "Counter"));
    }

    // ---------- 端到端：生成器真的把 .bui 清单喂进目录 ----------

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
    /// 整个项目（code-behind + 全部 .bui）跑一遍生成器。`@namespace` 由用例自己写在模板里，
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
        @using BubuUI.Elements
        <StackPanel>
            <TextBlock FontSize="12">0</TextBlock>
        </StackPanel>
        @namespace Gallery
        """;

    private const string ShowcaseCodeBehind = "namespace Gallery;\npublic partial class Showcase { }";

    // 标签可见性：K33 起「@namespace 链隐式可见」——Showcase 与 Counter 同在 Gallery，
    // 所以 Showcase 不需要 `@using Gallery`（C# 里也没这条规矩）。
    private const string ShowcaseUi = """
        @using BubuUI.Elements
        <StackPanel>
            <Counter />
        </StackPanel>
        @namespace Gallery
        """;

    [Fact]
    public void TemplateOnlyComponent_IsUsableFromAnotherTemplate_AndTheWholeProjectCompiles()
    {
        // 纯 .bui 组件（无 .cs、无 @code）：被别的模板当标签用、生成 new global::Gallery.Counter()，
        // 且「全部生成产物 + code-behind」合成一个真编译单元后零错误——这是 P2 的验收口径。
        var (result, output) = RunProject(ShowcaseCodeBehind,
            ("Counter.bui", CounterUi), ("Showcase.bui", ShowcaseUi));

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));

        var counter = GeneratedSource(result, "Counter");
        Assert.Contains("namespace Gallery", counter);
        Assert.Contains("partial class Counter : global::BubuUI.Elements.StackPanel", counter);

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
            ("Counter.bui", CounterUi));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI005");
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }

    [Fact]
    public void CodeBlockParameterAttribute_IsTheComponentSurface()
    {
        // K34（D112）：纯 .bui 组件的 @code [UiParameter] 由目录语法级提取——这是对外表面，
        // 不再警告（EUI057 收窄为「有同名 .cs 才报」，那时符号扫描真的看不到它）。
        const string ui = """
            @using BubuUI
            @using BubuUI.Elements
            <StackPanel />
            @namespace Gallery
            @code {
                [UiParameter] public string? Title { get; set; }
            }
            """;

        var (result, _) = RunProject("// no code-behind", ("Card.bui", ui));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI057");
        Assert.Single(result.GeneratedSources);

        var def = Assert.IsType<UiElementDef>(Find(CatalogOf("/templates/Card.bui", ui), "Card"));
        var title = Assert.IsType<UiParameterDef>(def.Parameters.GetValueOrDefault("Title"));
        Assert.Equal("global::Gallery.Card", def.ClrTypeDisplayName);
        Assert.True(title.CanWrite);
        Assert.False(title.IsDelegate);
    }

    [Fact]
    public void CodeBlockParameterAttribute_WithCodeBehind_IsSharedSurface()
    {
        // 有同名 .cs 时目录符号扫描只看 .cs 半边，@code 的模板特性确实到不了别的模板——
        // EUI057 的原始理由仍然成立。
        const string ui = """
            @using BubuUI
            @using BubuUI.Elements
            <StackPanel />
            @namespace Gallery
            @code {
                [UiParameter] public string? Title { get; set; }
            }
            """;

        var (result, _) = RunProject("namespace Gallery;\npublic partial class Card { }", ("Card.bui", ui));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI057");
        Assert.Single(result.GeneratedSources);
        var catalog = UiCatalogFactory.Build(Compile(("Card.cs", "namespace Gallery; public partial class Card { }")),
            new[] { UiTemplateScanner.FromFile("Card.bui", ui)! });
        Assert.Contains("Title", Assert.Single(catalog.Elements, e => e.Tag == "Card").Parameters.Keys);
    }

    // ---------- K34（D112）：@code 模板特性的目录级提取 ----------

    private const string CodeMemberUi = """
        @using System
        @using BubuUI
        @using BubuUI.Elements
        <StackPanel />
        @namespace Gallery
        @code {
            public int unused;                     // 字段不进表面（Ref 目标走探针）

            public string? NotAParameter { get; set; }   // 公开属性 → NonBindable 原因

            [UiParameter] public string? Title { get; set; }
            [UiParameter] public int Count { get; set; }
            [UiParameter] public Action<int>? CountChanged { get; set; }
            [UiParameter] public string? Bad2 { get; set; }
            [UiParameter] public Action<int>? Bad2Changed { get; set; }   // 形状不配：int vs string?
            [UiParameter] public int Bad0 { get; set; }
            [UiParameter] public Action Bad0Changed { get; set; }          // 无参回调：配对失败
            [UiParameter(Required = true)] public string? Name { get; set; }
            [UiContent] public Element? Body { get; set; }
            [UiSlot("Header")] public Element? HeaderSlot { get; set; }
            string? PrivateProp { get; set; }      // 非 public：不进表面也不进原因表
        }
        """;

    [Fact]
    public void CodeBlockMembers_ParametersSlotsContentAndRejections()
    {
        var def = Assert.IsType<UiElementDef>(Find(CatalogOf("/templates/Card.bui", CodeMemberUi), "Card"));

        var title = def.Parameters["Title"];
        Assert.Equal("global::System.String", title.ClrTypeDisplayName);
        Assert.True(title.CanWrite);
        Assert.False(title.Required);

        Assert.True(def.Parameters["Name"].Required);

        // 绑定通道：形状正确的配对回填，形状不对的记原因（D110 规则的文本级版本）
        Assert.Equal("CountChanged", def.Parameters["Count"].ChangedCallbackName);
        Assert.Null(def.Parameters["Bad2"].ChangedCallbackName);
        Assert.Contains("takes int but 'Bad2' is string", def.Parameters["Bad2"].ChangedShapeProblem, StringComparison.Ordinal);
        Assert.Null(def.Parameters["Bad0"].ChangedCallbackName);
        Assert.Contains("0 argument(s)", def.Parameters["Bad0"].ChangedShapeProblem, StringComparison.Ordinal);
        Assert.True(def.Parameters["Bad0Changed"].IsDelegate);

        // 内容与插槽
        Assert.True(def.Parameters["Body"].IsContent);
        Assert.False(def.Parameters["Body"].ContentIsMultiple);
        Assert.Equal("HeaderSlot", Assert.Single(def.Slots.Values).PropertyName);

        // 公开非参数属性给出「为什么不可用」，私有属性不进表面
        Assert.Contains("NotAParameter", def.Parameters.Keys);
        Assert.Null(def.BindRejection("NotAParameter"));
        Assert.Null(def.BindRejection("PrivateProp"));
    }

    [Fact]
    public void CodeBlockMembers_ReadOnlyOrStaticAreReported()
    {
        const string ui = """
            @using BubuUI
            @using BubuUI.Elements
            <StackPanel />
            @namespace Gallery
            @code {
                [UiParameter] public string? ro { get; }
                [UiParameter] public static string? stat { get; set; }
                [UiParameter] public int NotProperty;
            }
            """;

        var def = Assert.IsType<UiElementDef>(Find(CatalogOf("/templates/Card.bui", ui), "Card"));

        // 只读参数照进目录（CanWrite=false，使用点由 EUI040 解释）——与符号路径一致；
        // static 与字段不进参数表，声明问题记 EUI028。
        var ro = Assert.IsType<UiParameterDef>(def.Parameters.GetValueOrDefault("ro"));
        Assert.False(ro.CanWrite);
        Assert.DoesNotContain("stat", def.Parameters);
        Assert.Contains(def.Problems, p => p.MemberName == "ro" && p.Code == "EUI028");
        Assert.Contains(def.Problems, p => p.MemberName == "stat");
        Assert.Contains(def.Problems, p => p.Message.Contains("must be on a property", StringComparison.Ordinal));
        // 问题定位在 .bui 文件里（纯数据 Source，构建期由目录级出口报告）
        Assert.All(def.Problems, p => Assert.Equal("/templates/Card.bui", p.Source!.Path));
    }

    [Fact]
    public void CodeBlockMembers_BadSlotAndContentTypeAreReported()
    {
        const string ui = """
            @using BubuUI
            @using BubuUI.Elements
            <StackPanel />
            @namespace Gallery
            @code {
                [UiSlot] public Element? noName { get; set; }
                [UiSlot("A")] public string? wrongType { get; set; }
                [UiContent] public string? wrongContent { get; set; }
            }
            """;

        var def = Assert.IsType<UiElementDef>(Find(CatalogOf("/templates/Card.bui", ui), "Card"));

        Assert.Empty(def.Slots);
        Assert.DoesNotContain(def.Parameters.Keys, name => name is "noName" or "wrongType" or "wrongContent");
        Assert.Contains(def.Problems, p => p.Message.Contains("[UiSlot] must name the slot", StringComparison.Ordinal));
        Assert.Contains(def.Problems, p => p.Message.Contains("[UiSlot] properties must be", StringComparison.Ordinal));
        Assert.Contains(def.Problems, p => p.Message.Contains("[UiContent] parameters must be", StringComparison.Ordinal));
    }

    [Fact]
    public void TemplateOnlyParameters_AreBindableFromAnotherTemplate_AndTheWholeProjectCompiles()
    {
        // K34 验收口径：.bui 的 @code 参数就是对外表面——别的模板写 Title=/@bind-Count= 生成
        // 正确赋值与回调，且「全部生成产物 + code-behind」合成一个真编译单元后零错误。
        const string cardUi = """
            @using System
            @using BubuUI
            @using BubuUI.Elements
            <StackPanel>
                <TextBlock>{Title}</TextBlock>
            </StackPanel>
            @namespace Gallery
            @code {
                [UiParameter] public string? Title { get; set; }
                [UiParameter] public int Count { get; set; }
                [UiParameter] public Action<int>? CountChanged { get; set; }
            }
            """;
        const string showcaseUi = """
            @using BubuUI.Elements
            <StackPanel>
                <Card Title="hello" @bind-Count={n} />
            </StackPanel>
            @namespace Gallery
            @code {
                public int n;
            }
            """;

        var (result, output) = RunProject("// code-behind optional now",
            ("Card.bui", cardUi), ("Showcase.bui", showcaseUi));

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));

        var showcase = GeneratedSource(result, "Showcase");
        Assert.Contains(".Title = \"hello\"", showcase);
        Assert.Contains(".CountChanged = value =>", showcase);

        var errors = output.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => $"{d.Id} :: {d.GetMessage()}")
            .ToList();
        Assert.True(errors.Count == 0, "generated project does not compile: " + string.Join(" | ", errors));
    }

    [Fact]
    public void CodeBlockOrdinaryMembers_DoNotWarn()
    {
        const string ui = """
            @using BubuUI.Elements
            <StackPanel />
            @namespace Gallery
            @code {
                public int count;
                private void Bump() { count++; }
            }
            """;

        var (result, _) = RunProject("// no code-behind", ("Counter.bui", ui));

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "EUI057");
    }

    [Fact]
    public void TemplateComponents_ChangingBodyOnly_KeepsCatalogValueStable()
    {
        // 清单只含 (Tag, @namespace)：改正文不该让目录重建，否则每敲一个字都要重算整个标签表。
        var file = new TextFile("/templates/Counter.bui", CounterUi);
        var compilation = Compile(("CodeBehind.cs", ShowcaseCodeBehind));
        var driver = (CSharpGeneratorDriver)CSharpGeneratorDriver.Create(
            new[] { new TemplateGenerator().AsSourceGenerator() },
            new AdditionalText[] { file }).RunGenerators(compilation);

        var edited = new TextFile(file.Path, CounterUi.Replace("FontSize=\"12\"", "FontSize=\"14\"", StringComparison.Ordinal));
        var result = driver.ReplaceAdditionalText(file, edited)
            .RunGenerators(compilation).GetRunResult().Results.Single();

        // 清单步骤确实重跑了（AdditionalText 变了），但产出值相等 → Unchanged，不传播为 Modified/New。
        Assert.All(ReasonsOf(result, "TemplateTypes"),
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
