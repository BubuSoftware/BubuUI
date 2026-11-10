using EclipseUI.Compiler;
using EclipseUI.Compiler.Probe;
using EclipseUI.LanguageServer.Analysis;
using Microsoft.CodeAnalysis;
using Xunit;

namespace EclipseUI.Ide.Tests;

/// <summary>
/// 表达式投影（design/06 §6.3）的 IDE 侧用例：探针原语、code-behind 兄弟文件加载、
/// @foreach 变量环境、成员补全与悬停。探针核心与生成器共享（单一实现），编译期行为的
/// 回归由 Compiler.Tests 的 EUI029/037/047/049 用例兜底。
/// </summary>
public class ExpressionProbeTests
{
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
            @using EclipseUI.Elements
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
            @using EclipseUI.Elements
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
            @using EclipseUI.Elements
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
            "<TextBlock Text={tasks.Cou} />", "ProbeDoc.ui", TestHost.Catalog,
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
        const string doc = "@using EclipseUI.Elements\n<Button OnClick={Toggle|} />\n";
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
            @using EclipseUI.Elements
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
            @using EclipseUI.Elements
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

    [Fact]
    public void Definition_DotAndUnknownIdentifiersReturnNull()
    {
        var dot = TestHost.Analyze("<TextBlock Text={tasks.|Count} />", CodeBehind);
        Assert.Null(DefinitionProvider.Definition(dot.Analysis, dot.Offset));

        var unknown = TestHost.Analyze("<TextBlock Text={noth|ere.Else} />", CodeBehind);
        Assert.Null(DefinitionProvider.Definition(unknown.Analysis, unknown.Offset));
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
        const string doc = "@using EclipseUI.Elements\n<Button OnClick={Tog|gle} />\n";
        var (service, uri, offset) = TestHost.OpenWithCodeBehind(doc, CodeBehind);
        var definition = service.Definition(uri, offset);

        Assert.NotNull(definition);
        Assert.Equal("Toggle", definition!.Name);
        Assert.EndsWith("ProbeDoc.cs", definition.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_CodeBehindEditInvalidatesProbe()
    {
        var (service, uri, offset) = TestHost.OpenWithCodeBehind("<TextBlock Text={T|} />", CodeBehind);
        var before = service.Complete(uri, offset).Items;
        Assert.Contains(before, static i => i.Label == "Total");
        Assert.DoesNotContain(before, static i => i.Label == "Total2");

        // code-behind 改了（.ui 文本与版本都没动）：缓存按 code-behind 内容失效，新成员必须出现。
        File.WriteAllText(
            Path.ChangeExtension(DocumentStore.UriToPath(uri), ".cs"),
            CodeBehind.Replace("public int Total", "public int Total2;\n        public int Total"));
        Assert.Contains(service.Complete(uri, offset).Items, static i => i.Label == "Total2");
    }
}
