using EclipseUI.Compiler;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Workspace;

namespace EclipseUI.Ide.Tests;

/// <summary>
/// 测试宿主：目录同样**不手写**——扫真实 <c>EclipseUI.Runtime.dll</c>（随测试输出复制），
/// 与编译器、与线上 server 走同一条派生路径（design/14 §14.11.1）。
/// </summary>
internal static class TestHost
{
    private static readonly Lazy<CatalogProvider> Shared = new(() =>
    {
        var provider = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "EclipseUI.Runtime.dll"),
            // 测试输出目录里的"兄弟程序集"，比如测试自身——不想让它们变成标签。
            IncludeSiblingAssemblies = false,
        });
        _ = provider.Catalog; // 预热，避免每个测试各算一次
        return provider;
    });

    public static UiCatalog Catalog => Shared.Value.Catalog;

    public static LanguageService Service() => new(Shared.Value, new DocumentStore());

    /// <summary>打开一个 <c>.ui</c> 文档并返回服务；<paramref name="marker"/> 处的 <c>|</c> 会被移除，偏移原样返回。</summary>
    public static (LanguageService Service, string Uri, int Offset) Open(string text, string marker = "|")
    {
        var service = Service();
        var offset = text.IndexOf(marker, StringComparison.Ordinal);
        var content = offset >= 0 ? text.Remove(offset, marker.Length) : text;
        // 文件名必须是合法 C# 标识符（spec/02 §2.2 文件契约）：Ast.ClassName 由它推导，
        // @code 合成类名非法会导致成员树解析失败、探针绑定全部落空。
        var uri = DocumentStore.PathToUri(Path.Combine(Path.GetTempPath(), "eclipseui-ide-memory",
            Guid.NewGuid().ToString("N"), "ProbeDoc.ui"));
        service.Documents.Open(uri, content, 1);
        return (service, uri, offset < 0 ? content.Length : offset);
    }

    /// <summary>
    /// 真实磁盘形态：<c>.ui</c> 与同名 <c>.cs</c> 落临时目录——LanguageService 的
    /// 兄弟文件 code-behind 加载（spec/02 §2.2 文件契约）走的就是这条路径。
    /// 临时目录同时是工作区根（重命名的工作区 .cs 扫描用），所以这里用**专属**
    /// CatalogProvider 而不是共享实例。
    /// </summary>
    public static (LanguageService Service, string Uri, int Offset) OpenWithCodeBehind(
        string text, string codeBehind, string marker = "|")
    {
        var offset = text.IndexOf(marker, StringComparison.Ordinal);
        var content = offset >= 0 ? text.Remove(offset, marker.Length) : text;

        var directory = Path.Combine(Path.GetTempPath(), "eclipseui-ide-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ProbeDoc.ui");
        File.WriteAllText(path, content);
        File.WriteAllText(Path.ChangeExtension(path, ".cs"), codeBehind);

        var provider = new CatalogProvider(new CatalogOptions
        {
            RuntimePath = Path.Combine(AppContext.BaseDirectory, "EclipseUI.Runtime.dll"),
            IncludeSiblingAssemblies = false,
            ProbeRoot = directory,
        });
        _ = provider.Catalog; // 预热
        var service = new LanguageService(provider, new DocumentStore());

        var uri = DocumentStore.PathToUri(path);
        service.Documents.Open(uri, content, 1);
        return (service, uri, offset < 0 ? content.Length : offset);
    }

    /// <summary>不落盘的直连分析：code-behind 由参数注入（测 Provider 层，不经文件系统）。</summary>
    public static (DocumentAnalysis Analysis, int Offset) Analyze(
        string text, string? codeBehind = null, string marker = "|", UiCatalog? catalog = null)
    {
        var offset = text.IndexOf(marker, StringComparison.Ordinal);
        var content = offset >= 0 ? text.Remove(offset, marker.Length) : text;
        var analysis = DocumentAnalysis.Create(content, "ProbeDoc.ui", catalog ?? Catalog,
            Shared.Value.Compilation, codeBehind);
        return (analysis, offset < 0 ? content.Length : offset);
    }

    public static string FileName(string text) => "Probe.ui";

    /// <summary>合成一个只含枚举属性的控件定义：内置表面里没有枚举属性，用它验证枚举值补全。</summary>
    public static UiCatalog EnumCatalog(out UiElementDef element)
    {
        var shape = new UiTypeShape(UiTypeShapeKind.Enum, "global::Probe.Align",
            new[] { "Center", "Left", "Right" });
        var property = new UiPropDef("Align", "global::Probe.Align", "global::Probe.Box", "AlignProperty",
            CanWrite: true, shape);

        element = new UiElementDef(
            "Box",
            "global::Probe.Box",
            UiContentModel.Children,
            null,
            new Dictionary<string, UiPropDef>(StringComparer.Ordinal) { ["Align"] = property },
            new Dictionary<string, UiEventDef>(StringComparer.Ordinal),
            new Dictionary<string, UiParameterDef>(StringComparer.Ordinal),
            new Dictionary<string, UiSlotDef>(StringComparer.Ordinal),
            Array.Empty<UiMetadataProblem>());

        return new UiCatalog(new[] { element }, Array.Empty<UiHiddenTag>(), Array.Empty<UiMetadataProblem>());
    }
}
