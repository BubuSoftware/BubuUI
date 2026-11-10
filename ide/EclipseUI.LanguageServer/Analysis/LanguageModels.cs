using EclipseUI.Compiler;

namespace EclipseUI.LanguageServer.Analysis;

public enum UiCompletionKind
{
    Tag,
    Property,
    Event,
    Parameter,
    AttachedProperty,
    Directive,
    EnumMember,
    Keyword,
    StyleProperty,
    Namespace,

    /// <summary>表达式投影：C# 字段（code-behind/@code）。</summary>
    Field,

    /// <summary>表达式投影：C# 方法。</summary>
    Method,

    /// <summary>表达式投影：<c>@foreach</c> 迭代变量等局部量。</summary>
    Variable,
}

/// <param name="InsertText">写入文本；原生 LSP 补全项恒为纯文本，片段形态由 <paramref name="IsSnippet"/> 决定。</param>
/// <param name="SortText">排序键（LSP <c>sortText</c>）：默认按标签字母序会打乱"已导入优先"，
/// 所以用前缀数字分组——0 可见标签 / 1 属性 / 2 保留 / 3 附加属性。</param>
public sealed record UiCompletionItem(
    string Label,
    UiCompletionKind Kind,
    string? Detail = null,
    string? Documentation = null,
    string? InsertText = null,
    bool IsSnippet = false,
    string? SortText = null)
{
    public string Text => InsertText ?? Label;
}

/// <summary>一次补全的全部候选共享同一个替换区间（光标处只有一个 token）。</summary>
public sealed record UiCompletionList(TextSpan Replacement, IReadOnlyList<UiCompletionItem> Items)
{
    public static readonly UiCompletionList Empty = new(TextSpan.Zero, Array.Empty<UiCompletionItem>());
}

public sealed record UiHoverResult(TextSpan Span, string Markdown);

/// <summary>
/// 跳转定义目标（L3 第一片）。<paramref name="Uri"/> 为 null 表示**本文档**内
/// （<paramref name="Span"/> 是 .ui 偏移，如 <c>@foreach</c> 迭代变量）；否则是目标
/// 位置：磁盘路径（code-behind 兄弟文件 / 工作区 .cs / 仓库源码）或反编译虚拟文档
/// （<c>eui-decompiled:</c> scheme，design/06 §6.3）。<paramref name="Span"/> 是该
/// 文本内的偏移；<paramref name="Text"/> 是目标原文——协议层用它换算行位，与偏移
/// 同源不会错位（null 时协议层按旧约定取 code-behind / 本文档文本）。
/// </summary>
public sealed record UiDefinition(string? Uri, TextSpan Span, string Name, string? Text = null);

/// <summary>重命名的单处编辑（L3 第二片）：<paramref name="Span"/> 是所在文件内的字符区间。</summary>
public sealed record UiTextEdit(TextSpan Span, string NewText);

/// <summary>
/// 一个文件的重命名编辑。<paramref name="Path"/> 为 null 表示当前 .ui 文档；
/// 否则是磁盘路径（兄弟 code-behind / 工作区其他 .cs）。<paramref name="Text"/> 是
/// 该文件当前原文——协议层用它换算行位，与编辑偏移同源，不会错位。
/// </summary>
public sealed record UiRenameFile(string? Path, string Text, IReadOnlyList<UiTextEdit> Edits);

/// <summary><c>textDocument/rename</c> 的结果：按文件分组的编辑集。</summary>
public sealed record UiRenameResult(IReadOnlyList<UiRenameFile> Files);

/// <summary><c>textDocument/prepareRename</c> 的结果：可重命名的标识符区间 + 展示名。</summary>
public sealed record UiPrepareRename(TextSpan Span, string Placeholder);

public enum UiSymbolKind
{
    /// <summary><c>Element</c> 派生：标签是控件。</summary>
    Control,

    /// <summary><c>Component</c> 派生：标签是组件。</summary>
    Component,

    /// <summary><c>@if</c>/<c>@foreach</c>/<c>@switch</c> 块。</summary>
    Block,

    /// <summary><c>@code</c> 块。</summary>
    CodeBlock,
}

public sealed record UiSymbol(
    string Name,
    string Detail,
    UiSymbolKind Kind,
    TextSpan Span,
    TextSpan SelectionSpan,
    IReadOnlyList<UiSymbol> Children);

/// <summary>
/// 类型名的展示形态：目录里存的是 <c>global::System.Double</c> 这种生成用全名，
/// 悬停/补全要给人看的是 <c>double</c> / <c>EclipseUI.Brush</c>。
/// </summary>
public static class TypeNames
{
    private static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["System.Double"] = "double",
        ["System.Single"] = "float",
        ["System.Decimal"] = "decimal",
        ["System.Int64"] = "long",
        ["System.Int32"] = "int",
        ["System.Int16"] = "short",
        ["System.Byte"] = "byte",
        ["System.Boolean"] = "bool",
        ["System.String"] = "string",
        ["System.Char"] = "char",
        ["System.Object"] = "object",
        ["System.TimeSpan"] = "TimeSpan",
    };

    public static string Short(string clrDisplayName)
    {
        var name = clrDisplayName.StartsWith("global::", StringComparison.Ordinal)
            ? clrDisplayName.Substring("global::".Length)
            : clrDisplayName;
        return Keywords.TryGetValue(name, out var keyword) ? keyword : name;
    }

    /// <summary>命名空间（<c>global::EclipseUI.Elements.Button</c> → <c>EclipseUI.Elements</c>）。</summary>
    public static string? NamespaceOf(string clrDisplayName)
    {
        var name = clrDisplayName.StartsWith("global::", StringComparison.Ordinal)
            ? clrDisplayName.Substring("global::".Length)
            : clrDisplayName;
        int dot = name.LastIndexOf('.');
        return dot <= 0 ? null : name.Substring(0, dot);
    }

    /// <summary>短名（去掉命名空间），用于 <c>&lt;/</c> 与列表展示。</summary>
    public static string Bare(string clrDisplayName)
    {
        var name = Short(clrDisplayName);
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name.Substring(dot + 1);
    }
}
