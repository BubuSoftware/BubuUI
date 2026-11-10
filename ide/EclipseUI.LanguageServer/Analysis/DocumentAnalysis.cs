using EclipseUI.Compiler;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// 打开文档的全文镜像。v1 只做**全文同步**（<c>TextDocumentSyncKind.Full</c>）：
/// .ui 文件普遍在几 KB，全量重解析比维护增量编辑映射可靠得多，
/// 也不会出现"编辑器增量与解析器位置错位"这类最难查的 bug。
/// </summary>
public sealed class DocumentStore
{
    public sealed record Document(string Uri, string FileName, string Text, int Version);

    private readonly Dictionary<string, Document> _documents = new(StringComparer.Ordinal);

    public void Open(string uri, string text, int version) =>
        _documents[uri] = Create(uri, text, version);

    public bool Change(string uri, string text, int version)
    {
        if (!_documents.ContainsKey(uri)) return false;
        _documents[uri] = Create(uri, text, version);
        return true;
    }

    public void Close(string uri) => _documents.Remove(uri);

    public bool TryGet(string uri, out Document document) => _documents.TryGetValue(uri, out document!);

    public IReadOnlyCollection<Document> All => _documents.Values;

    private static Document Create(string uri, string text, int version)
    {
        var path = UriToPath(uri);
        var fileName = Path.GetFileName(path);
        return new Document(uri, string.IsNullOrEmpty(fileName) ? "Template.ui" : fileName, text, version);
    }

    /// <summary><c>file:///d%3A/…</c> → 本地路径；非 file 协议原样返回（测试里常用 <c>untitled:</c>）。</summary>
    public static string UriToPath(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile)
            return parsed.LocalPath;
        return uri;
    }

    public static string PathToUri(string path) =>
        new Uri(Path.GetFullPath(path)).AbsoluteUri;
}

/// <summary>
/// 一个 <c>.ui</c> 文档的分析结果：AST + 诊断 + 本文件的 <c>@using</c> 可见性信息。
/// 诊断走与编译期同一条前端（<see cref="FrontEnd"/>），所以 IDE 红波浪线和
/// <c>dotnet build</c> 报的是同一批 EUI 码、同一批 span。
/// </summary>
public sealed class DocumentAnalysis
{
    private DocumentAnalysis(
        string text,
        PositionMap positions,
        UiDocument? ast,
        IReadOnlyList<UiDiagnostic> diagnostics,
        IReadOnlyList<string> imports,
        IReadOnlyDictionary<string, string?> aliases,
        string? internalError)
    {
        Text = text;
        Positions = positions;
        Ast = ast;
        Diagnostics = diagnostics;
        Imports = imports;
        Aliases = aliases;
        InternalError = internalError;
    }

    public string Text { get; }

    public PositionMap Positions { get; }

    public UiDocument? Ast { get; }

    public IReadOnlyList<UiDiagnostic> Diagnostics { get; }

    /// <summary>本文件 <c>@using</c> 导入的命名空间（标签可见性过滤，K22）。</summary>
    public IReadOnlyList<string> Imports { get; }

    /// <summary><c>@using Alias = Ns.Type</c> 的别名表（K31）；非法别名值为 null。</summary>
    public IReadOnlyDictionary<string, string?> Aliases { get; }

    /// <summary>前端自身抛异常时的消息（正常路径恒为 null）。</summary>
    public string? InternalError { get; }

    public static DocumentAnalysis Create(string text, string fileName, UiCatalog catalog)
    {
        var positions = new PositionMap(text);
        var diagnostics = new List<UiDiagnostic>();

        UiDocument? ast;
        try
        {
            ast = FrontEnd.Parse(text, fileName, diagnostics);
        }
        catch (Exception ex)
        {
            return new DocumentAnalysis(text, positions, null,
                diagnostics.ToArray(), Array.Empty<string>(),
                new Dictionary<string, string?>(), ex.Message);
        }

        var imports = new List<string>();
        var aliases = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var node in ast.Nodes)
        {
            if (node is not UiUsingDirective directive) continue;
            if (directive.Alias is { Length: > 0 } alias)
            {
                aliases[alias] = directive.AliasTarget.Length == 0
                    ? null
                    : ToDisplayName(directive.AliasTarget);
            }
            else if (directive.Namespace.Length > 0 && !imports.Contains(directive.Namespace, StringComparer.Ordinal))
            {
                imports.Add(directive.Namespace);
            }
        }

        string? internalError = null;
        // 目录为空（没找到 Runtime）时不做语义检查：否则每个标签都会报 EUI001，
        // 把"没配好 IDE"伪装成"代码全错"。语法诊断照报。
        if (catalog.Elements.Count > 0)
        {
            try
            {
                FrontEnd.Analyze(ast, catalog, diagnostics);
            }
            catch (Exception ex)
            {
                internalError = ex.Message;
            }
        }

        return new DocumentAnalysis(text, positions, ast, diagnostics.ToArray(), imports, aliases, internalError);
    }

    /// <summary><c>@using</c> 别名目标归一化为目录键（<c>global::</c> 前缀，无则为原样全名）。</summary>
    private static string ToDisplayName(string aliasTarget) =>
        aliasTarget.StartsWith("global::", StringComparison.Ordinal) ? aliasTarget : "global::" + aliasTarget;

    /// <summary>按可见性解析标签（别名 → 目录 tag，@using 过滤）。</summary>
    public UiElementDef? ResolveVisible(UiCatalog catalog, string tag)
    {
        if (tag.Length == 0) return null;
        if (Aliases.TryGetValue(tag, out var target) && target is not null)
            return catalog.FindByTypeDisplayName(target);
        return catalog.Resolve(tag, Imports).Element;
    }

    /// <summary>不看可见性（悬停用：名字写错了也要能解释它是什么，补全则按可见性给提示）。</summary>
    public UiElementDef? ResolveAny(UiCatalog catalog, string tag)
    {
        if (tag.Length == 0) return null;
        if (Aliases.TryGetValue(tag, out var target) && target is not null)
            return catalog.FindByTypeDisplayName(target);
        return catalog.Resolve(tag).Element;
    }

    public UiElementDef? AttributeOwner(UiCatalog catalog, CursorContext context) =>
        ResolveVisible(catalog, context.TagName) ?? ResolveAny(catalog, context.TagName);

    /// <summary>把目录里的属性/事件/参数/附加属性名摊平，供"当前元素可写什么"的补全与悬停查询。</summary>
    public CandidateAttribute? FindAttribute(UiCatalog catalog, CursorContext context)
    {
        var element = AttributeOwner(catalog, context);
        var name = context.AttributeName;
        if (element is null || name.Length == 0) return null;

        var bare = name.StartsWith("@bind-", StringComparison.Ordinal)
            ? name.Substring("@bind-".Length)
            : name;

        if (element.Properties.TryGetValue(bare, out var property))
            return new CandidateAttribute(property.Name, CandidateAttributeKind.Property,
                TypeNames.Short(property.ClrTypeDisplayName), property.TypeShape,
                property.DeclaringTypeName, property.CanWrite, Context: property);

        if (element.Parameters.TryGetValue(bare, out var parameter))
            return new CandidateAttribute(parameter.Name, CandidateAttributeKind.Parameter,
                TypeNames.Short(parameter.ClrTypeDisplayName), parameter.TypeShape,
                element.ClrTypeDisplayName, parameter.CanWrite, Context: parameter);

        if (element.Events.TryGetValue(bare, out var @event))
            return new CandidateAttribute(@event.AttributeName, CandidateAttributeKind.Event,
                @event.ArgsTypeName, null, @event.DeclaringTypeName, CanWrite: false, Context: @event);

        int dot = bare.IndexOf('.');
        if (dot > 0)
        {
            var attached = catalog.FindAttached(bare.Substring(0, dot), bare.Substring(dot + 1));
            if (attached is not null)
                return new CandidateAttribute($"{attached.OwnerTag}.{attached.Name}",
                    CandidateAttributeKind.AttachedProperty, TypeNames.Short(attached.ClrTypeDisplayName),
                    attached.TypeShape, attached.OwnerClrName, CanWrite: true, Context: attached);
        }

        return null;
    }
}

public enum CandidateAttributeKind
{
    Property,
    Event,
    Parameter,
    AttachedProperty,
}

public sealed record CandidateAttribute(
    string Name,
    CandidateAttributeKind Kind,
    string TypeDisplay,
    UiTypeShape? Shape,
    string DeclaringTypeName,
    bool CanWrite,
    object? Context);
