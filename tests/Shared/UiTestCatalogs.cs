using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EclipseUI.Compiler;
using EclipseUI.Compiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EclipseUI.TestKit;

/// <summary>
/// 测试用目录：一律扫描已编译的 <c>EclipseUI.Runtime.dll</c> 得到（design/14 §14.11.1）。
/// 不得再手写一份固定目录——那会制造 T1 禁止的第二事实源。
/// 本项目（net10.0）可以引用 Roslyn，生产侧的 <c>EclipseUI.Compiler</c> 保持零 Roslyn 依赖。
/// </summary>
public static class UiTestCatalogs
{
    /// <summary>内置控件所在程序集；扫描锚点。</summary>
    public static readonly Assembly RuntimeAssembly = typeof(EclipseUI.Elements.Button).Assembly;

    private static readonly Lazy<UiCatalog> Built = new(() => ScanRuntime());

    /// <summary>扫描真实运行时得到的内置目录（每个测试进程一次）。</summary>
    public static UiCatalog BuiltIn() => Built.Value;

    /// <summary>
    /// 构造一个引用了运行时的探针编译：源里可以直接写 <c>StackPanel</c>/<c>Button</c> 等
    /// （已在 preamble 里 using），用于验证扫描规则。
    /// </summary>
    public static CSharpCompilation ProbeCompilation(string source = "", string assemblyName = "CatalogProbe")
    {
        const string preamble = "using System;\nusing EclipseUI;\nusing EclipseUI.Elements;\n";
        return CSharpCompilation.Create(assemblyName,
            new[] { CSharpSyntaxTree.ParseText(preamble + source) },
            ReferenceFiles(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    public static UiCatalog Scan(string source) => UiCatalogFactory.Build(ProbeCompilation(source));

    public static UiCatalog ScanRuntime() => UiCatalogFactory.Build(ProbeCompilation());

    /// <summary>
    /// 探针编译的引用集：共享框架程序集 + <c>EclipseUI.Runtime.dll</c>（扫描锚点）。
    /// 刻意排除与测试同目录的应用程序集（xunit、样例、测试自身）：扫描器按 §14.4.6 会把
    /// 直接引用运行时的程序集里的类型也算成标签，include 它们会让「内置表面」随测试项目而变。
    /// </summary>
    private static List<MetadataReference> ReferenceFiles()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(RuntimeAssembly.Location));
        return references;
    }

    /// <summary>把目录画成可评审的文本表面，用于仓库内 golden 快照文件（design/14 §14.11.1）。</summary>
    public static string RenderSurface(UiCatalog catalog)
    {
        var lines = new List<string>
        {
            "# EclipseUI 内置模板表面（design/14 §14.11.1）。",
            "# 由 CatalogScannerTests.Surface_MatchesGoldenSnapshotFile 生成/比对；控件 API 变化时必须连同变更记录一起更新。",
            $"# 标签 {catalog.Elements.Count} / 属性项 {catalog.Elements.Sum(e => e.Properties.Count)} / 事件项 {catalog.Elements.Sum(e => e.Events.Count)}",
            "",
        };

        foreach (var element in catalog.Elements.OrderBy(e => e.Tag, StringComparer.Ordinal))
        {
            lines.Add($"[{element.Tag}] {element.ClrTypeDisplayName} origin={element.Origin} " +
                $"content={element.Content}{(element.ContentTarget is null ? "" : $":{element.ContentTarget}")}");
            foreach (var property in element.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
                lines.Add($"    prop   {property.Key} {property.Value.TypeShape.CanonicalKey} " +
                    $"declared={property.Value.DeclaringTypeName} field={property.Value.PropertyFieldName} " +
                    $"writable={property.Value.CanWrite}");
            foreach (var evt in element.Events.OrderBy(e => e.Key, StringComparer.Ordinal))
                lines.Add($"    event  {evt.Key} {evt.Value.EventName} declared={evt.Value.DeclaringTypeName} " +
                    $"field={evt.Value.FieldName}");
            foreach (var parameter in element.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
                lines.Add($"    param  {parameter.Key} {parameter.Value.ClrTypeDisplayName} " +
                    $"required={parameter.Value.Required} delegate={parameter.Value.IsDelegate}");
            foreach (var slot in element.Slots.OrderBy(s => s.Key, StringComparer.Ordinal))
                lines.Add($"    slot   {slot.Key} -> {slot.Value.PropertyName}");
            lines.Add("");
        }

        foreach (var hidden in catalog.HiddenTags.OrderBy(h => h.Tag, StringComparer.Ordinal))
            lines.Add($"[hidden] {hidden.Tag} {hidden.ClrTypeName} {hidden.ReasonCode} {hidden.Reason}");
        foreach (var problem in catalog.Problems)
            lines.Add($"[problem] {problem.Code} {problem.DeclaringTypeName} {problem.MemberName} {problem.Message}");

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
