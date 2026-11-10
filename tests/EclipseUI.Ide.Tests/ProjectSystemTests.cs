using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Workspace;
using Xunit;

namespace EclipseUI.Ide.Tests;

/// <summary>
/// §6.9.7 五条要求的回归夹具：用**真实仓库**跑 L1–L4（MSBuild 求值 → 项目 Compilation →
/// 真生成器 → 目录），不再用内存态/dll 目录假装有项目。
///
/// 文档 uri 借用 samples/Gallery 里已存在、且被 AdditionalFiles 收进去的 .ui/.eustyle 路径，
/// 文本通过 DocumentStore 缓冲区注入——不写盘、不污染工作树，同时保证归属判据真实命中。
/// </summary>
public class ProjectSystemTests
{
    private sealed class Fixture
    {
        public required string Root { get; init; }
        public required ProjectWorkspace Workspace { get; init; }
        public required LanguageService Service { get; init; }

        public string Gallery(string fileName) => Path.Combine(Root, "samples", "Gallery", fileName);
    }

    // 求值 + 构建全部项目快照只做一次；只读夹具共用，改缓冲区的用例另建实例。
    private static readonly Lazy<Fixture> Shared = new(Create);

    private static Fixture Create()
    {
        var root = FindRepositoryRoot();
        var projects = new ProjectWorkspace(new CatalogOptions { ProbeRoot = root });
        var legacy = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "EclipseUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
        });
        return new Fixture
        {
            Root = root,
            Workspace = projects,
            Service = new LanguageService(legacy, new DocumentStore(), projects),
        };
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "EclipseUI.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("cannot locate EclipseUI.slnx from the test output directory");
    }

    /// <summary>P2/P3 的地基：本机 MSBuildLocator 求值可用，且能派生出非空目录。</summary>
    [Fact]
    public void ProjectSystem_EvaluatesWorkspaceAndBuildsCatalog()
    {
        var fixture = Shared.Value;
        Assert.True(fixture.Workspace.HasProjects);

        var counter = fixture.Gallery("Counter.ui");
        var snapshot = Assert.Single(fixture.Workspace.ResolveAll(counter));
        Assert.NotEmpty(snapshot.Catalog.Elements);
        Assert.NotEmpty(snapshot.Evaluation.AdditionalFiles);
        Assert.Contains(snapshot.Evaluation.AdditionalFiles, f => f.EndsWith("Counter.ui", StringComparison.Ordinal));
    }

    /// <summary>
    /// 扩展传的是打开的文件夹：只开 samples/Gallery 时，发现阶段也要向上扩到仓库根，
    /// 否则框架类型的源码工程不在集合里，L6 会错误地退回反编译。归属判据不因此放宽。
    /// </summary>
    [Fact]
    public void ProjectSystem_SubfolderWorkspaceStillSeesFrameworkSource()
    {
        var gallery = Path.Combine(FindRepositoryRoot(), "samples", "Gallery");
        var projects = new ProjectWorkspace(new CatalogOptions { ProbeRoot = gallery });
        Assert.True(projects.HasProjects);
        Assert.Contains(projects.FindSourceTypes("EclipseUI.Elements.Button"),
            t => t.Locations.Any(l => l.IsInSource));

        var counter = Path.Combine(gallery, "Counter.ui");
        var owners = projects.ResolveAll(counter);
        Assert.NotEmpty(owners);
        Assert.Contains(owners, s => string.Equals(s.Evaluation.Directory, gallery,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>要求 2/3 的根因：未 build 的源码控件与 .ui 派生组件都要进目录。</summary>
    [Fact]
    public void Fixture2_UnbuiltSourceControlsAndUiComponentsAreInTheCatalog()
    {
        var fixture = Shared.Value;
        var snapshot = Assert.Single(fixture.Workspace.ResolveAll(fixture.Gallery("Counter.ui")));

        Assert.Contains(snapshot.Catalog.Elements, e => e.Tag == "MyControl");
        Assert.Contains(snapshot.Catalog.Elements, e => e.Tag == "Counter");
        Assert.Contains(snapshot.Catalog.Elements, e => e.Tag == "Button");
    }

    /// <summary>要求 1：@using 别名既进标签补全，也能解析（悬停有内容）。</summary>
    [Fact]
    public void Fixture1_UsingAliasIsCompletedAndResolved()
    {
        var fixture = Shared.Value;
        var uri = DocumentStore.PathToUri(fixture.Gallery("Counter.ui"));
        const string completing = "@using EclipseUI.Elements\n@using Alt = Gallery.MyControl\n\n<Al";
        fixture.Service.Documents.Open(uri, completing, 1);
        Assert.Contains(fixture.Service.Complete(uri, completing.Length).Items, i => i.Label == "Alt");

        const string hovering = "@using EclipseUI.Elements\n@using Alt = Gallery.MyControl\n\n<Alt />\n";
        fixture.Service.Documents.Open(uri, hovering, 2);
        var offset = hovering.IndexOf("Alt />", StringComparison.Ordinal) + 1;
        Assert.NotNull(fixture.Service.Hover(uri, offset));
    }

    /// <summary>要求 3：属性补全 == 目录里该元素的属性/事件/参数全集，框架内部名不得混进来。</summary>
    [Fact]
    public void Fixture3_AttributeCompletionMatchesTheCatalog()
    {
        var fixture = Shared.Value;
        var uri = DocumentStore.PathToUri(fixture.Gallery("TaskList.ui"));
        const string text = "@using EclipseUI.Elements\n\n<Button ";
        fixture.Service.Documents.Open(uri, text, 1);
        var items = fixture.Service.Complete(uri, text.Length).Items;
        var labels = items.Select(i => i.Label).ToHashSet(StringComparer.Ordinal);

        var button = Assert.Single(fixture.Workspace.ResolveAll(fixture.Gallery("TaskList.ui")))
            .Catalog.Elements.Where(e => e.Tag == "Button");
        var element = Assert.Single(button);
        foreach (var name in element.Properties.Keys) Assert.Contains(name, labels);
        foreach (var name in element.Events.Keys) Assert.Contains(name, labels);
        foreach (var name in element.Parameters.Keys) Assert.Contains(name, labels);
        Assert.DoesNotContain("ItemFactory", labels);
        Assert.DoesNotContain("AvailableSize", labels);
    }

    /// <summary>
    /// 要求 4：框架属性与框架标签的 F12 都要落在仓库源码 .cs 上，而不是反编译虚拟文档。
    /// 当前项目把它们当元数据引用，靠 L6 的工作区源码符号回退找声明。
    /// </summary>
    [Fact]
    public void Fixture4_FrameworkAttributeAndTagGoToRepositorySource()
    {
        var fixture = Shared.Value;
        var path = fixture.Gallery("Showcase.ui");
        var uri = DocumentStore.PathToUri(path);
        var text = File.ReadAllText(path);
        fixture.Service.Documents.Open(uri, text, 1);

        var attribute = fixture.Service.Definition(uri, text.IndexOf("Gap=", StringComparison.Ordinal) + 1);
        Assert.NotNull(attribute);
        AssertSourceFile(attribute!, path);

        var tag = fixture.Service.Definition(uri, text.IndexOf("<Button", StringComparison.Ordinal) + 2);
        Assert.NotNull(tag);
        AssertSourceFile(tag!, path);
    }

    private static void AssertSourceFile(EclipseUI.LanguageServer.Analysis.UiDefinition definition, string sourcePath)
    {
        Assert.False(definition.Uri is null, "definition has no target uri");
        Assert.False(definition.Uri!.StartsWith("eui-decompiled:", StringComparison.Ordinal),
            "fell back to decompilation: " + definition.Uri);
        Assert.EndsWith(".cs", definition.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(definition.Uri!), "definition target must be a real file on disk");
        // 框架源码在仓库根下，不是当前 samples/Gallery 项目目录。
        Assert.NotEqual(Path.GetDirectoryName(sourcePath), Path.GetDirectoryName(definition.Uri));
        Assert.Contains("src", definition.Uri!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 特殊属性（Analyzer.IsReservedName，目录外）的 F12：Ref= 跳 code-behind 字段声明；
    /// Class= 类名跳类规则定义（本文件 @style 块优先，其次项目 .eustyle 全局样式表）；
    /// Style= 属性名跳目录 StylePropertyDef 记的 StyledProperty 字段注册处。三路都不走
    /// 目录属性通道，各有唯一落点，且都是符号/解析器事实源（不扫文本）。
    /// </summary>
    [Fact]
    public void Fixture6_SpecialAttributeJumpsRefClassStyle()
    {
        var fixture = Shared.Value;
        var path = fixture.Gallery("Showcase.ui");
        var uri = DocumentStore.PathToUri(path);
        var text = File.ReadAllText(path);
        fixture.Service.Documents.Open(uri, text, 1);

        // Ref={virtualList} → Showcase.cs 的字段声明（属性名光标与 {} 内光标同落点）。
        var refJump = fixture.Service.Definition(uri, text.IndexOf("Ref={", StringComparison.Ordinal) + 1);
        Assert.NotNull(refJump);
        Assert.EndsWith("Showcase.cs", refJump!.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(refJump.Uri!), "Ref target must be the code-behind on disk");

        // Class="demo-scope" → 本文档 @style 块的 .demo-scope 规则（Uri 为空 = 跳本文档）。
        var localClass = fixture.Service.Definition(uri, text.IndexOf("\"demo-scope\"", StringComparison.Ordinal) + 2);
        Assert.NotNull(localClass);
        Assert.Null(localClass!.Uri);
        Assert.Equal("demo-scope", localClass.Name);
        // NameSpan 含选择器前导 '.'（EustyleRule.NameSpan = .name 原文 span）。
        Assert.True(localClass.Span.Start == text.IndexOf(".demo-scope {", StringComparison.Ordinal),
            "class jump must land on the @style selector");

        // Class="primary" → 项目 app.eustyle 的 .primary 规则。
        var globalClass = fixture.Service.Definition(uri, text.IndexOf("\"primary\"", StringComparison.Ordinal) + 2);
        Assert.NotNull(globalClass);
        Assert.EndsWith("app.eustyle", globalClass!.Uri, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(globalClass.Uri!), "global class target must be the stylesheet on disk");

        // Style="CornerRadius: 18" 的属性名 → StyledProperty 字段注册（src/EclipseUI.Rendering）。
        var styleJump = fixture.Service.Definition(uri, text.IndexOf("CornerRadius: 18", StringComparison.Ordinal));
        Assert.NotNull(styleJump);
        AssertSourceFile(styleJump!, path);
    }

    /// <summary>
    /// 要求 5 / P6：.ui 的表达式类型错误与 .eustyle 的样式诊断都由 Tier 2 真生成器给出，
    /// 且 (code, span) 落在原始文档上。缓冲区覆盖磁盘，验证未保存也生效。
    /// </summary>
    [Fact]
    public void Fixture5_Tier2DiagnosticsCoverUiAndEustyle()
    {
        var fixture = Create();
        var uiPath = fixture.Gallery("Prefs.ui");
        var uiUri = DocumentStore.PathToUri(uiPath);
        const string uiText = "@using EclipseUI.Elements\n<TextBlock FontSize={notDeclaredAnywhere} />\n";
        fixture.Service.Documents.Open(uiUri, uiText, 1);
        fixture.Service.ApplyBuffer(uiUri, uiText);
        Assert.Contains(fixture.Service.GetDiagnostics(uiUri), d => d.Code == "CS0103");

        var stylePath = fixture.Gallery("app.eustyle");
        var styleUri = DocumentStore.PathToUri(stylePath);
        const string styleText = ".broken {\n    NotARealStyleProperty: 3;\n}\n";
        fixture.Service.Documents.Open(styleUri, styleText, 1);
        fixture.Service.ApplyBuffer(styleUri, styleText);
        Assert.Contains(fixture.Service.GetDiagnostics(styleUri), d => d.Code == "EUI051");
    }

    /// <summary>
    /// 编辑器发来的 URI 形如 <c>file:///d%3A/repo/x.ui</c>（盘符冒号百分号编码、盘符小写）。
    /// 这种写法下 <see cref="Uri.LocalPath"/> 返回 <c>/d:/repo/x.ui</c>，经 GetFullPath 后
    /// 永远匹配不到项目 AdditionalFiles——2026-11-11 的现象就是「脚本全绿、编辑器全废」。
    /// 这里直接钉住转换结果，任何回归都会在这里炸。
    /// </summary>
    [Theory]
    [InlineData("file:///d%3A/workspace/repo/Sample.ui", @"d:\workspace\repo\Sample.ui")]
    [InlineData("file:///D:/workspace/repo/Sample.ui", @"D:\workspace\repo\Sample.ui")]
    [InlineData("file:///d%3A/workspace/my%20repo/Sample.ui", @"d:\workspace\my repo\Sample.ui")]
    [InlineData("file:///d%3A/%E4%B8%AD%E6%96%87/Sample.ui", @"d:\中文\Sample.ui")]
    [InlineData("file://server/share/Sample.ui", @"\\server\share\Sample.ui")]
    [InlineData("untitled:Untitled-1", "untitled:Untitled-1")]
    public void UriConversion_HandlesEditorEncodedUris(string uri, string expected)
    {
        Assert.Equal(expected, DocumentStore.UriToPath(uri));
    }

    /// <summary>
    /// 夹具 7：**按编辑器的真实 URI 形态**走一遍——归属、标签跳转、属性跳转、诊断都得活。
    /// 之前所有用例都用 PathToUri 自己拼 URI，恰好绕开了编码分支，所以 119 例全绿而用户在
    /// 编辑器里一个跳转都没有。
    /// </summary>
    [Fact]
    public void Fixture7_EditorEncodedUriKeepsTheProjectAlive()
    {
        var fixture = Create();
        var path = fixture.Gallery("Showcase.ui");
        var text = File.ReadAllText(path);
        var uri = EditorUri(path);

        fixture.Service.Documents.Open(uri, text, 1);
        fixture.Service.ApplyBuffer(uri, text);

        // 归属：编码 URI 必须仍然解析到同一个项目。
        Assert.NotEmpty(fixture.Workspace.ResolveAll(DocumentStore.UriToPath(uri)));

        // 标签跳转（框架控件 → 仓库源码）。
        var tag = text.IndexOf("<Button", StringComparison.Ordinal) + 2;
        AssertSourceFile(Assert.IsType<UiDefinition>(fixture.Service.Definition(uri, tag)), path);

        // 属性跳转（框架属性 → 仓库源码）。
        var attribute = text.IndexOf("ItemCount=", StringComparison.Ordinal);
        AssertSourceFile(Assert.IsType<UiDefinition>(fixture.Service.Definition(uri, attribute + 2)), path);

        // 能力不是「无声空」：目录非空，补全有候选。
        Assert.NotEmpty(fixture.Service.CatalogForUri(uri).Elements);
        Assert.NotEmpty(fixture.Service.Complete(uri, text.IndexOf("<Button", StringComparison.Ordinal) + 1).Items);
    }

    /// <summary>与 VS Code <c>Uri.toString()</c> 同形：小写盘符 + <c>%3A</c>。</summary>
    private static string EditorUri(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        if (full.Length > 2 && full[1] == ':')
            full = char.ToLowerInvariant(full[0]) + "%3A" + full[2..];
        return "file:///" + full;
    }
}
