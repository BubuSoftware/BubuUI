using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BubuUI.Compiler;
using BubuUI.Compiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BubuUI.TestKit;

/// <summary>
/// 测试用目录：一律扫描已编译的 <c>BubuUI.Runtime.dll</c> 得到（design/14 §14.11.1）。
/// 不得再手写一份固定目录——那会制造 T1 禁止的第二事实源。
/// 本项目（net11.0）可以引用 Roslyn，生产侧的 <c>BubuUI.Compiler</c> 保持零 Roslyn 依赖。
/// </summary>
public static class UiTestCatalogs
{
    /// <summary>内置控件所在程序集；扫描锚点。</summary>
    public static readonly Assembly RuntimeAssembly = typeof(BubuUI.Elements.Button).Assembly;

    private static readonly Lazy<UiCatalog> Built = new(() => ScanRuntime());

    /// <summary>扫描真实运行时得到的内置目录（每个测试进程一次）。</summary>
    public static UiCatalog BuiltIn() => Built.Value;

    /// <summary>
    /// 构造一个引用了运行时的探针编译：源里可以直接写 <c>StackPanel</c>/<c>Button</c> 等
    /// （已在 preamble 里 using），用于验证扫描规则。
    /// </summary>
    public static CSharpCompilation ProbeCompilation(string source = "", string assemblyName = "CatalogProbe")
    {
        const string preamble = "using System;\nusing BubuUI;\nusing BubuUI.Elements;\n";
        return CSharpCompilation.Create(assemblyName,
            new[] { CSharpSyntaxTree.ParseText(preamble + source) },
            ReferenceFiles(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    public static UiCatalog Scan(string source) => UiCatalogFactory.Build(ProbeCompilation(source));

    public static UiCatalog ScanRuntime() => UiCatalogFactory.Build(ProbeCompilation());

    /// <summary>
    /// 探针编译的引用集：共享框架程序集 + <c>BubuUI.Runtime.dll</c>（扫描锚点）。
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
        // design/17 §4.3：样式属性目录从 StyleBuilder setter + StyledProperty 字段符号派生，
        // 探针编译必须像真实宿主一样引用渲染程序集，否则得到空目录。
        // 按路径取而非 typeof(...)：本文件同时被两种测试项目编译，不强加编译期引用。
        var rendering = Path.Combine(Path.GetDirectoryName(RuntimeAssembly.Location)!,
            "BubuUI.Rendering.dll");
        if (File.Exists(rendering)) references.Add(MetadataReference.CreateFromFile(rendering));
        return references;
    }

    /// <summary>扫描真实运行时 + 渲染程序集得到的可样式属性目录（design/17 §4.3）。</summary>
    public static StylePropertyCatalog StyleProperties() => BuiltIn().StyleProperties;

    /// <summary>把目录画成可评审的文本表面，用于仓库内 golden 快照文件（design/14 §14.11.1）。</summary>
    public static string RenderSurface(UiCatalog catalog)
    {
        var lines = new List<string>
        {
            "# BubuUI 内置模板表面（design/14 §14.11.1）。",
            "# 由 CatalogScannerTests.Surface_MatchesGoldenSnapshotFile 生成/比对；控件 API 变化时必须连同变更记录一起更新。",
            $"# 标签 {catalog.Elements.Count} / 属性项 {catalog.Elements.Sum(e => e.Properties.Count)} / 事件项 {catalog.Elements.Sum(e => e.Events.Count)}" +
            $" / 附加属性项 {catalog.AttachedProperties.Count}",
            "",
        };

        foreach (var element in catalog.Elements.OrderBy(e => e.Tag, StringComparer.Ordinal))
        {
            lines.Add($"[{element.Tag}] {element.ClrTypeDisplayName} " +
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
                    $"delegate={parameter.Value.IsDelegate}");
            foreach (var slot in element.Slots.OrderBy(s => s.Key, StringComparer.Ordinal))
                lines.Add($"    slot   {slot.Key} -> {slot.Value.PropertyName}");
            lines.Add("");
        }

        // G4：附加属性表面（design/14 §14.5.5）——宿主不进标签列表，单独成块。
        foreach (var attached in catalog.AttachedProperties)
            lines.Add($"[attached] {attached.OwnerTag}.{attached.Name} {attached.TypeShape.CanonicalKey} " +
                $"declared={attached.OwnerClrName} setter={attached.SetterMethod}");
        if (catalog.AttachedProperties.Count > 0)
            lines.Add("");

        foreach (var hidden in catalog.HiddenTags.OrderBy(h => h.Tag, StringComparer.Ordinal))
            lines.Add($"[hidden] {hidden.Tag} {hidden.ClrTypeName} {hidden.ReasonCode} {hidden.Reason}");
        foreach (var problem in catalog.Problems)
            lines.Add($"[problem] {problem.Code} {problem.DeclaringTypeName} {problem.MemberName} {problem.Message}");

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
