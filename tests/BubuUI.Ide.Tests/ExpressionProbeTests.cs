using BubuUI.Compiler;
using BubuUI.Compiler.Probe;
using BubuUI.LanguageServer.Analysis;
using Microsoft.CodeAnalysis;
using Xunit;

namespace BubuUI.Ide.Tests;

/// <summary>
/// 表达式投影（design/06 §6.3）的 IDE 侧用例：探针原语、code-behind 兄弟文件加载、
/// @foreach 变量环境、成员补全与悬停。探针核心与生成器共享（单一实现），编译期行为的
/// 回归由 Compiler.Tests 的 EUI029/037/047/049 用例兜底。
/// </summary>
public class ExpressionProbeTests
{
    [Fact]
    public void StandaloneTemplate_InheritsTheRootTypeInItsUnifiedSymbolView()
    {
        var (analysis, offset) = TestHost.Analyze("""
            @namespace Demo
            @using BubuUI.Elements
            <StackPanel Gap={Wid|}/>
            """);
        var probe = Assert.IsType<ExpressionProbe>(analysis.Probe);

        Assert.Equal("BubuUI.Elements.StackPanel", probe.ThisType!.BaseType!.ToDisplayString());
        Assert.Equal("double", probe.InferType("Width", Array.Empty<(ITypeSymbol Type, string Name)>())!.ToDisplayString());
        Assert.Contains(CompletionProvider.Complete(analysis, TestHost.Catalog, offset).Items,
            item => item.Label == "Width");
    }

    [Fact]
    public void StandaloneTemplate_CodeAndInheritedMembersShareOneCompilation()
    {
        var (analysis, offset) = TestHost.Analyze("""
            @namespace Demo
            <CodePanel Gap={total}/>
            @code { public int total = 4; public string Describe() => Caption; }
            """, """
            namespace Demo;
            public class CodePanel : BubuUI.Elements.StackPanel
            {
                public string Caption { get; set; } = "from base";
            }
            public partial class ProbeDoc { }
            """);
        var probe = Assert.IsType<ExpressionProbe>(analysis.Probe);
        var members = Assert.IsAssignableFrom<SyntaxTree>(analysis.MemberTree);

        Assert.True(probe.Compilation.ContainsSyntaxTree(members));
        Assert.Equal("Demo.CodePanel", probe.ThisType!.BaseType!.ToDisplayString());
        Assert.Equal("int", probe.InferType("total", Array.Empty<(ITypeSymbol Type, string Name)>())!.ToDisplayString());
        Assert.Equal("string", probe.InferType("Describe()", Array.Empty<(ITypeSymbol Type, string Name)>())!.ToDisplayString());
        var member = Assert.Single(probe.ThisType.GetMembers("total"));
        var span = member.Locations.Single().SourceSpan;
        Assert.True(analysis.TryMapMemberSpanToUi(new TextSpan(span.Start, span.Length), out var original));
        Assert.Equal("total", analysis.Text.Substring(original.Start, original.Length));
        Assert.Single(probe.Compilation.SyntaxTrees,
            t => t.FilePath.EndsWith(".catalog.cs", StringComparison.Ordinal));
    }

    /// <summary>与 spec/02 §2.2 同形态的 code-behind：partial 类 + 字段/属性/方法/嵌套类型。</summary>
    private const string CodeBehind = """
        #nullable enable
        using System.Collections.Generic;

        namespace Gallery;

        public sealed class TaskItem
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public bool Done { get; set; }
        }

        public partial class ProbeDoc
        {
            public List<TaskItem> tasks = new();
            public int Total => tasks.Count;
            public void Toggle(TaskItem item) { item.Done = !item.Done; }
        }
        """;

    // ---- 探针原语 ----

    [Fact]
    public void Probe_ResolvesExpressionTypeThroughCodeBehind()
    {
        var (analysis, _) = TestHost.Analyze("<TextBlock Text={tasks.Count|} />", CodeBehind);
        var probe = analysis.Probe;

        Assert.NotNull(probe);
        var env = ExpressionEnvironment.Build(analysis.Ast!, Array.Empty<ForeachScope>(), probe!, probe!.Compilation);
        var type = probe!.InferType("tasks.Count", env);

        Assert.NotNull(type);
        Assert.Equal("int", type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
    }

    [Fact]
    public void Probe_ResolvesForeachItemEnvironment()
    {
        const string doc = """
            @using BubuUI.Elements
            @foreach (var task in tasks)
            {
                <TextBlock Text={task.Name|} />
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc, CodeBehind);
        var probe = analysis.Probe;

        Assert.NotNull(probe);
        var context = CursorScanner.Scan(analysis.Text, offset);
        Assert.Equal(CursorKind.Expression, context.Kind);
        var scope = Assert.Single(context.OpenForeaches);
        Assert.Equal("task", scope.VarName);
        Assert.Equal("tasks", scope.Collection);

        var env = ExpressionEnvironment.Build(analysis.Ast!, context.OpenForeaches, probe!, probe!.Compilation);
        var item = Assert.Single(env);
        Assert.Equal("task", item.Name);
        Assert.Equal("TaskItem", item.Type.Name);

        var type = probe!.InferType("task.Name", env);
        Assert.Equal("string", type!.ToDisplayString());
    }

    // ---- 补全 ----

    [Fact]
    public void Completion_AfterDotListsReceiverMembersWithExactRange()
    {
        var (analysis, offset) = TestHost.Analyze("<TextBlock Text={tasks.Cou|} />", CodeBehind);
        var list = CompletionProvider.Complete(analysis, TestHost.Catalog, offset);

        Assert.Contains(list.Items, static i => i.Label == "Count" && i.Kind == UiCompletionKind.Property);
        Assert.Contains(list.Items, static i => i.Label == "Add" && i.Kind == UiCompletionKind.Method);
        Assert.Contains(list.Items, static i => i.Label == "RemoveAt" && i.Kind == UiCompletionKind.Method);

        // 替换区间只覆盖点号后的 "Cou"，不是整段 "tasks.Cou"。
        Assert.Equal("Cou", analysis.Text.Substring(list.Replacement.Start, list.Replacement.Length));
    }

    [Fact]
    public void Completion_BarePrefixListsForeachVariablesAndSelfMembers()
    {
        const string doc = """
            @using BubuUI.Elements
            @foreach (var task in tasks)
            {
                <TextBlock Text={ta|} />
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc, CodeBehind);
        var list = CompletionProvider.Complete(analysis, TestHost.Catalog, offset);

        Assert.Contains(list.Items, static i => i.Label == "task" && i.Kind == UiCompletionKind.Variable);
        Assert.Contains(list.Items, static i => i.Label == "tasks" && i.Kind == UiCompletionKind.Field);
        Assert.Contains(list.Items, static i => i.Label == "Total" && i.Kind == UiCompletionKind.Property);
        Assert.Contains(list.Items, static i => i.Label == "Toggle" && i.Kind == UiCompletionKind.Method);

        var item = list.Items.Single(static i => i.Label == "task");
        Assert.Contains("TaskItem", item.Detail);
        Assert.Equal("ta", analysis.Text.Substring(list.Replacement.Start, list.Replacement.Length));
    }

    [Fact]
    public void Completion_NestedForeachSeesOuterVariable()
    {
        const string doc = """
            @using BubuUI.Elements
            @foreach (var task in tasks)
            {
                @foreach (var tag in task.Name.Split(','))
                {
                    <TextBlock Text={ta|} />
                }
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc, CodeBehind);
        var list = CompletionProvider.Complete(analysis, TestHost.Catalog, offset);

        Assert.Contains(list.Items, static i => i.Label == "task");
        Assert.Contains(list.Items, static i => i.Label == "tag");
        var tag = list.Items.Single(static i => i.Label == "tag");
        Assert.Contains("string", tag.Detail);
    }

    [Fact]
    public void Completion_WithoutCompilationReturnsEmptyInsteadOfThrowing()
    {
        var analysis = DocumentAnalysis.Create(
            "<TextBlock Text={tasks.Cou} />", "ProbeDoc.bui", TestHost.Catalog,
            baseCompilation: null, codeBehindText: CodeBehind);
        Assert.Null(analysis.Probe);

        var offset = analysis.Text.IndexOf("Cou}", StringComparison.Ordinal);
        var list = CompletionProvider.Complete(analysis, TestHost.Catalog, offset);
        Assert.Empty(list.Items);
    }

    // ---- 悬停 ----

    [Fact]
    public void Hover_ExpressionShowsInferredTypeAndTarget()
    {
        var (analysis, offset) = TestHost.Analyze("<TextBlock Text={tasks.Count|} />", CodeBehind);
        var hover = HoverProvider.Hover(analysis, TestHost.Catalog, offset);

        Assert.NotNull(hover);
        Assert.Contains("int", hover!.Markdown);
        Assert.Contains("inferred C# type", hover.Markdown);
    }

    [Fact]
    public void Hover_HandlerExpressionShowsMethodSignature()
    {
        const string doc = "@using BubuUI.Elements\n<Button OnClick={Toggle|} />\n";
        var (analysis, offset) = TestHost.Analyze(doc, CodeBehind);
        var hover = HoverProvider.Hover(analysis, TestHost.Catalog, offset);

        Assert.NotNull(hover);
        Assert.Contains("Toggle", hover!.Markdown);
        Assert.Contains("TaskItem", hover.Markdown);
        Assert.Contains("handler method", hover.Markdown);
    }

    // ---- 跳转定义（L3 第一片） ----

    [Fact]
    public void Definition_ForeachVariableNavigatesInsideDocument()
    {
        const string doc = """
            @using BubuUI.Elements
            @foreach (var task in tasks)
            {
                <TextBlock Text={tas|k.Name} />
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc, CodeBehind);
        var definition = DefinitionProvider.Definition(analysis, offset);

        Assert.NotNull(definition);
        Assert.Null(definition!.Uri); // 本文档内：foreach 头
        Assert.Equal("task", definition.Name);

        var expected = analysis.Text.IndexOf("var task", StringComparison.Ordinal) + "var ".Length;
        Assert.Equal(expected, definition.Span.Start);
        Assert.Equal(4, definition.Span.Length);
    }

    /// <summary>光标落在 token 中间（Na|me）也要给出完整表达式的符号——这正是 ExpressionEnd 存在的理由。</summary>
    [Fact]
    public void Definition_CursorMidTokenResolvesWithFullExpression()
    {
        const string doc = """
            @using BubuUI.Elements
            @foreach (var task in tasks)
            {
                <TextBlock Text={task.Na|me} />
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc, CodeBehind);
        var probe = analysis.Probe;
        Assert.NotNull(probe);

        var context = CursorScanner.Scan(analysis.Text, offset);
        Assert.Equal(CursorKind.Expression, context.Kind);
        Assert.Equal(analysis.Text.IndexOf('}', context.ExpressionStart), context.ExpressionEnd);

        var env = ExpressionEnvironment.Build(analysis.Ast!, context.OpenForeaches, probe!, probe!.Compilation);
        var expression = analysis.Text.Substring(context.ExpressionStart, context.ExpressionEnd - context.ExpressionStart);
        var symbol = probe!.Locate(expression, offset - context.ExpressionStart, env);

        Assert.NotNull(symbol);
        Assert.Equal("Name", symbol!.Name);
    }

    /// <summary>内存态分析没有 code-behind 磁盘路径：符号能解析但无处可跳——返回 null 而不是错位置。</summary>
    [Fact]
    public void Definition_InMemoryCodeBehindHasNoPathSoReturnsNull()
    {
        var (analysis, offset) = TestHost.Analyze("<TextBlock Text={To|tal} />", CodeBehind);
        Assert.Null(DefinitionProvider.Definition(analysis, offset));
    }

    /// <summary>
    /// 未知标识符无处可跳 → null（不猜）。BCL 成员（<c>tasks.Count</c> 的 <c>Count</c>）源码不可达，
    /// 接通统一三路后走反编译：落点是 <c>List&lt;T&gt;</c> 的 <c>Count</c> 声明（与框架属性跳转同一口径，
    /// 2026-11-11 前 {expr} 只走 FromLocation，元数据成员一律 null，现已接通）。
    /// </summary>
    [Fact]
    public void Definition_UnknownIdentifierReturnsNull_BclMemberDecompiles()
    {
        var unknown = TestHost.Analyze("<TextBlock Text={noth|ere.Else} />", CodeBehind);
        Assert.Null(DefinitionProvider.Definition(unknown.Analysis, unknown.Offset));

        var count = TestHost.Analyze("<TextBlock Text={tasks.|Count} />", CodeBehind);
        var definition = DefinitionProvider.Definition(count.Analysis, count.Offset);
        Assert.NotNull(definition);
        Assert.StartsWith("eui-decompiled:", definition!.Uri, StringComparison.Ordinal);
        Assert.Equal("Count", definition.Text!.Substring(definition.Span.Start, definition.Span.Length));
    }

    /// <summary>
    /// <c>@code</c> 里的 BCL 类型关键字/泛型名转定义（用户实测「string, List, int, bool 找不到实现」）：
    /// <c>int/bool/string</c> 是 PredefinedTypeSyntax、<c>List</c> 是 GenericNameSyntax，都不是
    /// IdentifierNameSyntax——旧 switch 全落 <c>_ =&gt; null</c>。GetSymbolInfo 给底层 BCL 类型
    /// （Int32/Boolean/String/List`1），走统一三路反编译；内容请求按客户端实际回传形态
    /// （vscode-uri 实测：三斜杠折叠成一斜杠 + query 解码）也命中缓存，不再跳空文件。
    /// </summary>
    [Theory]
    [InlineData("public in|t x;", "Int32")]
    [InlineData("public bo|ol x;", "Boolean")]
    [InlineData("public stri|ng x;", "String")]
    [InlineData("public Stri|ng x;", "String")]
    [InlineData("public Li|st<int> x;", "List")]
    public void Definition_CodeBlockBclTypeDecompilesWithContent(string declaration, string typeName)
    {
        var (analysis, offset) = TestHost.Analyze(
            "@using System\n@using System.Collections.Generic\n@using BubuUI.Elements\n" +
            "<TextBlock />\n@code {\n    " + declaration + "\n}\n");
        var definition = DefinitionProvider.Definition(analysis, offset);

        Assert.NotNull(definition);
        Assert.StartsWith("eui-decompiled:", definition!.Uri, StringComparison.Ordinal);
        Assert.Equal(typeName, definition.Name);
        Assert.False(string.IsNullOrEmpty(definition.Text));
        Assert.Equal(typeName, definition.Text!.Substring(definition.Span.Start, definition.Span.Length));

        var raw = definition.Uri!;
        Assert.Equal(definition.Text, DecompileProvider.Content(raw));
        // 客户端实际回传形态（真实 vscode-uri 包实测 URI.parse(raw).toString(true)）：
        // 空 authority 三斜杠折叠成一斜杠 + query %XX 解码。这一形态也必须命中缓存。
        var clientForm = Uri.UnescapeDataString(
            raw.Replace(DecompileProvider.Scheme + ":///", DecompileProvider.Scheme + ":/"));
        Assert.Equal(definition.Text, DecompileProvider.Content(clientForm));
    }

    // ---- LanguageService 全链路（真实磁盘兄弟文件） ----

    [Fact]
    public void Service_CompletionReadsSiblingCodeBehindFile()
    {
        var (service, uri, offset) = TestHost.OpenWithCodeBehind("<TextBlock Text={tasks.Cou|} />", CodeBehind);
        var list = service.Complete(uri, offset);

        Assert.Contains(list.Items, static i => i.Label == "Count");
    }

    [Fact]
    public void Service_DefinitionJumpsToSiblingCodeBehindProperty()
    {
        var (service, uri, offset) = TestHost.OpenWithCodeBehind("<TextBlock Text={To|tal} />", CodeBehind);
        var definition = service.Definition(uri, offset);

        Assert.NotNull(definition);
        Assert.Equal("Total", definition!.Name);
        Assert.EndsWith("ProbeDoc.cs", definition.Uri, StringComparison.Ordinal);

        // span 精确覆盖 .cs 里 "Total" 属性声明的名字（导航落在标识符上）。
        var cs = File.ReadAllText(Path.ChangeExtension(DocumentStore.UriToPath(uri), ".cs"));
        var expected = cs.IndexOf("public int Total", StringComparison.Ordinal) + "public int ".Length;
        Assert.Equal(expected, definition.Span.Start);
        Assert.Equal("Total".Length, definition.Span.Length);
    }

    [Fact]
    public void Service_DefinitionResolvesEventHandlerMethod()
    {
        const string doc = "@using BubuUI.Elements\n<Button OnClick={Tog|gle} />\n";
        var (service, uri, offset) = TestHost.OpenWithCodeBehind(doc, CodeBehind);
        var definition = service.Definition(uri, offset);

        Assert.NotNull(definition);
        Assert.Equal("Toggle", definition!.Name);
        Assert.EndsWith("ProbeDoc.cs", definition.Uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// @code 成员（无 code-behind 场景）：声明就写在 .bui 自己的 @code 块里——真实文本，
    /// 跳转必须落进去（旧口径返回 null，用户侧表现为"找不到定义"）。
    /// </summary>
    [Fact]
    public void Definition_CodeBlockMemberNavigatesInsideDocument()
    {
        const string doc = """
            @using BubuUI.Elements
            <TextBlock Text={mo|de} />
            @code {
                public int mode;
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc);
        var definition = DefinitionProvider.Definition(analysis, offset);

        Assert.NotNull(definition);
        Assert.Null(definition!.Uri); // 本文档：@code 块
        Assert.Equal("mode", definition.Name);

        var expected = analysis.Text.IndexOf("public int mode", StringComparison.Ordinal) + "public int ".Length;
        Assert.Equal(expected, definition.Span.Start);
        Assert.Equal("mode".Length, definition.Span.Length);
    }

    /// <summary>
    /// foreach 头整体不是合法 C#——光标落在集合段时只探集合（"rows"），
    /// 集合在 @code 里声明 → 回映到块内声明名。
    /// </summary>
    [Fact]
    public void Definition_ForeachHeaderCollectionNavigatesToDeclaration()
    {
        const string doc = """
            @using BubuUI.Elements
            @foreach (var row in ro|ws)
            {
                <TextBlock Text={row} />
            }
            @code {
                public string[] rows = { "row A" };
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc);
        var definition = DefinitionProvider.Definition(analysis, offset);

        Assert.NotNull(definition);
        Assert.Null(definition!.Uri);
        Assert.Equal("rows", definition.Name);

        var expected = analysis.Text.IndexOf("public string[] rows", StringComparison.Ordinal)
            + "public string[] ".Length;
        Assert.Equal(expected, definition.Span.Start);
        Assert.Equal("rows".Length, definition.Span.Length);
    }

    /// <summary>foreach 头里光标落在**变量名**上：它就是定义本身，没有可跳的目标。</summary>
    [Fact]
    public void Definition_ForeachHeaderVariableNameReturnsNull()
    {
        const string doc = """
            @using BubuUI.Elements
            @foreach (var ro|w in rows)
            {
                <TextBlock Text={row} />
            }
            @code {
                public string[] rows = { "row A" };
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc);
        Assert.Null(DefinitionProvider.Definition(analysis, offset));
    }

    /// <summary>Service 层全链路：@code 声明跳转的 Uri=null 分支按文档文本换算行位。</summary>
    [Fact]
    public void Service_DefinitionJumpsIntoCodeBlockInsideUiDocument()
    {
        const string doc = """
            @using BubuUI.Elements
            <TextBlock Text={mo|de} />
            @code {
                public int mode;
            }
            """;
        var (service, uri, offset) = TestHost.Open(doc);
        var definition = service.Definition(uri, offset);

        Assert.NotNull(definition);
        Assert.Null(definition!.Uri);
        Assert.Equal("mode", definition.Name);

        var text = service.Documents.TryGet(uri, out var document) ? document.Text : "";
        var expected = text.IndexOf("public int mode", StringComparison.Ordinal) + "public int ".Length;
        Assert.Equal(expected, definition.Span.Start);
    }

    [Fact]
    public void Service_CodeBehindEditInvalidatesProbe()
    {
        var (service, uri, offset) = TestHost.OpenWithCodeBehind("<TextBlock Text={T|} />", CodeBehind);
        var before = service.Complete(uri, offset).Items;
        Assert.Contains(before, static i => i.Label == "Total");
        Assert.DoesNotContain(before, static i => i.Label == "Total2");

        // code-behind 改了（.bui 文本与版本都没动）：缓存按 code-behind 内容失效，新成员必须出现。
        File.WriteAllText(
            Path.ChangeExtension(DocumentStore.UriToPath(uri), ".cs"),
            CodeBehind.Replace("public int Total", "public int Total2;\n        public int Total"));
        Assert.Contains(service.Complete(uri, offset).Items, static i => i.Label == "Total2");
    }

    // ---- 重命名（L3 第二片） ----

    [Fact]
    public void Rename_ForeachVariableUpdatesHeaderAndBodyUses()
    {
        const string doc = """
            @using BubuUI.Elements
            @foreach (var task in tasks)
            {
                <TextBlock Text={ta|sk.Name} />
                <Button OnClick={() => Toggle(task)} />
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc, CodeBehind);
        var result = RenameProvider.Rename(analysis, offset, "item", workspaceRoot: null);

        Assert.NotNull(result);
        var file = Assert.Single(result!.Files);
        Assert.Null(file.Path); // 变量作用域就是本文档：编辑全部落在 .bui

        Assert.Equal(3, file.Edits.Count); // 头声明 + 正文引用 + lambda 引用
        foreach (var edit in file.Edits)
        {
            Assert.Equal("task", analysis.Text.Substring(edit.Span.Start, edit.Span.Length));
            Assert.Equal("item", edit.NewText);
        }
        Assert.Equal(analysis.Text.IndexOf("var task", StringComparison.Ordinal) + 4, file.Edits[0].Span.Start);
    }

    /// <summary>内外层同名（遮蔽）：从外层改名只动外层的声明与外层体内的引用，内层一律不碰。</summary>
    [Fact]
    public void Rename_ForeachVariableRespectsInnerShadowing()
    {
        const string doc = """
            @using BubuUI.Elements
            @foreach (var task| in tasks)
            {
                @foreach (var task in tasks)
                {
                    <TextBlock Text={task.Name} />
                }
                <TextBlock Text={task.Id} />
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc, CodeBehind);
        var result = RenameProvider.Rename(analysis, offset, "item", workspaceRoot: null);

        Assert.NotNull(result);
        var file = Assert.Single(result!.Files);
        Assert.Equal(2, file.Edits.Count); // 外层头 + 外层体内的 task.Id；内层 body 不动

        Assert.Equal(analysis.Text.IndexOf("var task", StringComparison.Ordinal) + 4, file.Edits[0].Span.Start);
        var outerUse = file.Edits[1];
        Assert.StartsWith("{task.Id}", analysis.Text.Substring(outerUse.Span.Start - 1));
    }

    [Fact]
    public void Rename_MemberUpdatesUiCodeBehindAndWorkspaceReferences()
    {
        var (service, uri, offset) = TestHost.OpenWithCodeBehind("<TextBlock Text={To|tal} />", CodeBehind);
        var directory = Path.GetDirectoryName(DocumentStore.UriToPath(uri))!;
        File.WriteAllText(Path.Combine(directory, "Helper.cs"), """
            namespace Gallery;
            public class Helper
            {
                public int Read(ProbeDoc doc) => doc.Total;
            }
            """);

        var result = service.Rename(uri, offset, "Count");

        Assert.NotNull(result);
        Assert.Equal(3, result!.Files.Count); // .bui + 兄弟 ProbeDoc.cs + 工作区 Helper.cs

        var ui = result.Files[0];
        Assert.Null(ui.Path);
        var uiEdit = Assert.Single(ui.Edits);
        Assert.Equal("Total", ui.Text.Substring(uiEdit.Span.Start, uiEdit.Span.Length));
        Assert.Equal("Count", uiEdit.NewText);

        var codeBehind = result.Files[1];
        Assert.EndsWith("ProbeDoc.cs", codeBehind.Path, StringComparison.Ordinal);
        var declaration = Assert.Single(codeBehind.Edits);
        Assert.Equal("Total", codeBehind.Text.Substring(declaration.Span.Start, declaration.Span.Length));
        Assert.Equal(codeBehind.Text.IndexOf("public int Total", StringComparison.Ordinal) + "public int ".Length,
            declaration.Span.Start);

        var helper = result.Files[2];
        Assert.EndsWith("Helper.cs", helper.Path, StringComparison.Ordinal);
        var usage = Assert.Single(helper.Edits);
        Assert.Equal("Total", helper.Text.Substring(usage.Span.Start, usage.Span.Length));
        Assert.Equal("Count", usage.NewText);
    }

    [Fact]
    public void Rename_RejectsForeignMemberAndInvalidNames()
    {
        // TaskItem.Name：声明在其他类型上，本 server 的文件集合改不全 → 拒绝。
        const string doc = """
            @using BubuUI.Elements
            @foreach (var task in tasks)
            {
                <TextBlock Text={task.Na|me} />
            }
            """;
        var (service, uri, offset) = TestHost.OpenWithCodeBehind(doc, CodeBehind);
        Assert.Null(service.Rename(uri, offset, "Name"));

        // 新名字必须是合法标识符（保留关键字同样拒绝）。
        var (member, memberUri, memberOffset) = TestHost.OpenWithCodeBehind("<TextBlock Text={To|tal} />", CodeBehind);
        Assert.Null(member.Rename(memberUri, memberOffset, "1bad"));
        Assert.Null(member.Rename(memberUri, memberOffset, "class"));
        Assert.NotNull(member.Rename(memberUri, memberOffset, "Count"));
    }

    /// <summary>内存态文档：变量改名不需要磁盘/工作区，成员改名则需要——缺了就整体拒绝。</summary>
    [Fact]
    public void Rename_InMemoryDocumentAllowsVariablesButNotMembers()
    {
        const string doc = """
            @using BubuUI.Elements
            @foreach (var task in tasks)
            {
                <TextBlock Text={ta|sk.Id} />
            }
            """;
        var variable = TestHost.Analyze(doc, CodeBehind);
        Assert.NotNull(RenameProvider.Rename(variable.Analysis, variable.Offset, "item", workspaceRoot: null));

        var member = TestHost.Analyze("<TextBlock Text={To|tal} />", CodeBehind);
        Assert.Null(RenameProvider.Rename(member.Analysis, member.Offset, "Count", workspaceRoot: null));
    }

    [Fact]
    public void Prepare_GivesIdentifierSpanAndPlaceholder()
    {
        var (analysis, offset) = TestHost.Analyze("<TextBlock Text={To|tal} />", CodeBehind);
        var prepare = RenameProvider.Prepare(analysis, offset);

        Assert.NotNull(prepare);
        Assert.Equal("Total", prepare!.Placeholder);
        Assert.Equal("Total", analysis.Text.Substring(prepare.Span.Start, prepare.Span.Length));

        var dot = TestHost.Analyze("<TextBlock Text={tasks.|Count} />", CodeBehind);
        Assert.Null(RenameProvider.Prepare(dot.Analysis, dot.Offset));
    }

    // ---- @code 块内跳转（L3 第三片） ----

    /// <summary>诊断：服务层全链路（磁盘 code-behind + @code 声明点击）。</summary>
    [Fact]
    public void Service_CodeBlockDeclarationClickResolves()
    {
        const string doc = """
            @using BubuUI.Elements
            <TextBlock />
            @code {
                public int mo|de;
            }
            """;
        var (service, uri, offset) = TestHost.OpenWithCodeBehind(doc, CodeBehind);
        var definition = service.Definition(uri, offset);
        Assert.NotNull(definition);
        Assert.Equal("mode", definition!.Name);
        Assert.Null(definition.Uri);
    }

    /// <summary>诊断：内存态声明点击。</summary>
    [Fact]
    public void CodeBlockDeclarationClickResolvesInMemory()
    {
        const string doc = """
            @using BubuUI.Elements
            <TextBlock />
            @code {
                public int mode;
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc);
        var atDecl = analysis.Text.IndexOf("public int mode", StringComparison.Ordinal) + "public int ".Length;
        Assert.NotNull(DefinitionProvider.Definition(analysis, atDecl));
    }

    /// <summary>块内引用（mode = 1）→ 同块声明（public int mode）——回映到 .bui 真实文本。</summary>
    [Fact]
    public void Definition_CodeBlockReferenceNavigatesToDeclaration()
    {
        const string doc = """
            @using BubuUI.Elements
            <TextBlock Text="x" />
            @code {
                public int mode;
                void Tick()
                {
                    mo|de = 1;
                }
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc);
        var definition = DefinitionProvider.Definition(analysis, offset);

        Assert.NotNull(definition);
        Assert.Null(definition!.Uri); // 本文档：@code 块
        Assert.Equal("mode", definition.Name);

        var expected = analysis.Text.IndexOf("public int mode", StringComparison.Ordinal) + "public int ".Length;
        Assert.Equal(expected, definition.Span.Start);
        Assert.Equal("mode".Length, definition.Span.Length);
    }

    /// <summary>块内局部变量（C# 全语义：声明→引用）→ 同块声明。</summary>
    [Fact]
    public void Definition_CodeBlockLocalVariableNavigatesInsideBlock()
    {
        const string doc = """
            @code {
                void Tick()
                {
                    int count = 1;
                    cou|nt++;
                }
            }
            """;
        var (analysis, offset) = TestHost.Analyze(doc);
        var definition = DefinitionProvider.Definition(analysis, offset);

        Assert.NotNull(definition);
        Assert.Null(definition!.Uri);
        Assert.Equal("count", definition.Name);
        Assert.Equal("count", analysis.Text.Substring(definition.Span.Start, definition.Span.Length));
    }

    // ---- 标签名跳转（L3 第四片） ----

    /// <summary>
    /// 标签 → 目录 CLR 类型 → 工作区源码声明。两个文件都定义了同名类：
    /// "文件名 == 类型名" 的那个优先（BubuUI 组件惯例）。
    /// </summary>
    [Fact]
    public void Definition_TagNameNavigatesToWorkspaceSource()
    {
        var element = new UiElementDef(
            "Widget", "global::Probe.Widget", UiContentModel.Children, null,
            new Dictionary<string, UiPropDef>(StringComparer.Ordinal),
            new Dictionary<string, UiEventDef>(StringComparer.Ordinal),
            new Dictionary<string, UiParameterDef>(StringComparer.Ordinal),
            new Dictionary<string, UiSlotDef>(StringComparer.Ordinal),
            Array.Empty<UiMetadataProblem>());
        var catalog = new UiCatalog(new[] { element }, Array.Empty<UiHiddenTag>(), Array.Empty<UiMetadataProblem>());

        var directory = Path.Combine(Path.GetTempPath(), "bubuui-ide-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Widget.cs"),
                "namespace Probe;\n\npublic class Widget\n{\n}\n");
            File.WriteAllText(Path.Combine(directory, "Other.cs"),
                "namespace Probe;\n\npublic class Widget\n{\n}\n");

            var (analysis, offset) = TestHost.Analyze("<Wi|dget />", catalog: catalog);
            var definition = DefinitionProvider.Definition(analysis, offset,
                workspaceRoot: directory, catalog: catalog);

            Assert.NotNull(definition);
            Assert.EndsWith("Widget.cs", definition!.Uri, StringComparison.Ordinal);
            Assert.Equal("Widget", definition.Name);
            var text = File.ReadAllText(definition.Uri!);
            Assert.Equal("Widget", text.Substring(definition.Span.Start, definition.Span.Length));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>目录不认识的标签（用户拼错）：不猜，返回 null。</summary>
    [Fact]
    public void Definition_UnknownTagNameReturnsNull()
    {
        var (analysis, offset) = TestHost.Analyze("<NotA|Tag />");
        Assert.Null(DefinitionProvider.Definition(analysis, offset));
    }

    /// <summary>
    /// 内置标签（dll/nuget 引用形态）：源码三路都够不着 → 反编译兜底——
    /// eui-decompiled: 虚拟文档 + 声明 span，内容请求回传 C# 文本。
    /// </summary>
    [Fact]
    public void Definition_BuiltInTagDecompilesWithoutSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bubuui-ide-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (analysis, offset) = TestHost.Analyze("<TextBl|ock />");
            var definition = DefinitionProvider.Definition(analysis, offset,
                workspaceRoot: directory, catalog: TestHost.Catalog);

            Assert.NotNull(definition);
            Assert.StartsWith("eui-decompiled:", definition!.Uri, StringComparison.Ordinal);
            Assert.Equal("TextBlock", definition.Name);
            Assert.False(definition.Text is null);

            // span 落在反编译文本的声明名字上；内容请求回传同源文本。
            Assert.Equal("TextBlock", definition.Text.Substring(definition.Span.Start, definition.Span.Length));
            Assert.Contains("TextBlock", DecompileProvider.Content(definition.Uri!), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>反编译虚拟文档的服务级链路：definition → decompiledContent 同一 uri 键。</summary>
    [Fact]
    public void Service_DecompiledContentRoundTrips()
    {
        var (service, uri, offset) = TestHost.Open("<TextBl|ock />");
        var definition = service.Definition(uri, offset);

        Assert.NotNull(definition);
        Assert.StartsWith("eui-decompiled:", definition!.Uri, StringComparison.Ordinal);

        var content = service.DecompiledContent(definition.Uri!);
        Assert.False(content is null);
        Assert.Contains("TextBlock", content!, StringComparison.Ordinal);
    }

    /// <summary>
    /// 内置标签的工作区不在仓库根（samples/Gallery 这类子目录）：框架类型兜底——
    /// 从工作区向上找仓库根（.git 标记），在仓库源码里跳到元素类声明。
    /// </summary>
    [Fact]
    public void Definition_BuiltInTagJumpsToRepositorySource()
    {
        var repository = Path.Combine(Path.GetTempPath(), "bubuui-ide-tests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(repository, "samples", "Gallery");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(Path.Combine(repository, "src", "BubuUI.Runtime"));
        Directory.CreateDirectory(workspace);
        try
        {
            File.WriteAllText(
                Path.Combine(repository, "src", "BubuUI.Runtime", "TextBlock.cs"),
                "namespace BubuUI.Elements;\n\npublic class TextBlock\n{\n}\n");

            // 默认 catalog 扫真实 Runtime.dll——TextBlock 的 ClrTypeDisplayName 就是
            // global::BubuUI.Elements.TextBlock，与文件里的声明一一对应。
            var (analysis, offset) = TestHost.Analyze("<TextBl|ock />");
            var definition = DefinitionProvider.Definition(analysis, offset,
                workspaceRoot: workspace, catalog: TestHost.Catalog);

            Assert.NotNull(definition);
            Assert.Equal("TextBlock", definition!.Name);
            Assert.EndsWith(
                Path.Combine("src", "BubuUI.Runtime", "TextBlock.cs"),
                definition.Uri, StringComparison.Ordinal);
            var text = File.ReadAllText(definition.Uri!);
            Assert.Equal("TextBlock", text.Substring(definition.Span.Start, definition.Span.Length));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }
}
