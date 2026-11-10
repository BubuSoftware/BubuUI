using System.Collections.Concurrent;
using EclipseUI.Compiler;
using EclipseUI.Compiler.Probe;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;

namespace EclipseUI.LanguageServer.Analysis;

/// <summary>
/// 反编译跳转（design/06 §6.3 L3 第 4 路）：源码三路（兄弟 code-behind / 工作区 .cs /
/// 仓库根）都够不着时——dll / nuget 引用的控件只有元数据——用 ICSharpCode.Decompiler
/// （ILSpy 引擎）把类型反编译成 C#，挂到 <see cref="Scheme"/> 虚拟文档上。VS Code 客户端
/// 注册 <c>TextDocumentContentProvider</c>，经 <c>eclipseui/decompiledContent</c> 自定义
/// 请求取回文本；Rider 同理（lsp4ij 虚拟文件）。
///
/// 程序集来源：探针编译引用了目录扫描器加载的全部 PE（TPA + Runtime/Rendering + 应用
/// 程序集），用类型符号反查所在的 <c>PortableExecutableReference.FilePath</c>。
/// 缓存两级：程序集 → <see cref="CSharpDecompiler"/>（构造较贵）；uri → 反编译文本
/// （definition 给出的 Range 必须与之后取回的文本一致，不允许二次反编译漂移）。
/// </summary>
public static class DecompileProvider
{
    /// <summary>反编译虚拟文档 scheme（客户端内容提供器按它注册）。</summary>
    public const string Scheme = "eui-decompiled";

    private static readonly ConcurrentDictionary<string, Lazy<CSharpDecompiler?>> Decompilers =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, string> Texts = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, (string AssemblyPath, string FullTypeName)> Sources =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 反编译定义：目录 CLR 全名 → 探针编译绑符号（拿不到符号/PE 就诚实返回 null，
    /// 不装腔）→ 反编译类型文本 → 声明名字的 <see cref="TextSpan"/>。
    /// </summary>
    /// <param name="memberName">
    /// L6 成员级转定义：dll/nuget 控件的**属性**也要能跳——反编译同一类型，但落点定位到
    /// 成员声明而不是类型名。传 null 时为原来的类型级跳转（§6.9.0 第 4 条）。
    /// </param>
    public static UiDefinition? Definition(DocumentAnalysis analysis, string displayName, string? memberName = null)
    {
        var fullTypeName = displayName.StartsWith("global::", StringComparison.Ordinal)
            ? displayName["global::".Length..]
            : displayName;
        if (fullTypeName.Length == 0) return null;

        var probe = analysis.Probe;
        if (probe is null) return null;

        var symbol = probe.Compilation.GetTypeByMetadataName(fullTypeName);
        if (symbol is null || symbol.ContainingAssembly is null) return null;

        var assemblyPath = FindAssemblyPath(probe.Compilation, symbol.ContainingAssembly);
        if (assemblyPath is null) return null;

        var name = symbol.Name;
        var uri = BuildUri(symbol.ContainingAssembly.Name, fullTypeName, memberName ?? name);
        var text = Decompile(assemblyPath, fullTypeName, uri);
        if (text is null) return null;

        var span = (memberName is null ? null : FindMemberDeclarationSpan(text, memberName))
                   ?? DefinitionProvider.FindTypeDeclarationSpan(text, name)
                   ?? TextSpan.Zero;
        return new UiDefinition(uri, span, name, text);
    }

    /// <summary>
    /// 反编译文本里的成员声明名字区间。ILSpy 输出的是重建 C#（与原始源码不完全一致），
    /// 所以按「访问修饰符 → 成员名 → 声明定界符」这一稳定形态定位，找不到就退回类型名，
    /// 绝不给一个随机同名引用的位置。
    /// </summary>
    internal static TextSpan? FindMemberDeclarationSpan(string text, string memberName)
    {
        if (memberName.Length == 0) return null;
        var pattern = @"\b(?:public|protected|internal|private)\b[^;{}]{0,300}?\b(?<name>" +
                      System.Text.RegularExpressions.Regex.Escape(memberName) +
                      @")\b\s*(?:\{|=>|=|;|\()";
        var match = System.Text.RegularExpressions.Regex.Match(
            text, pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success
            ? new TextSpan(match.Groups["name"].Index, match.Groups["name"].Length)
            : null;
    }

    /// <summary>虚拟文档内容（客户端内容提供器经 eclipseui/decompiledContent 取回）。</summary>
    public static string? Content(string uri)
    {
        if (Texts.TryGetValue(uri, out var cached)) return cached;

        // 缓存未命中（理论上只在异常时序出现）：有源记录就重编译并回填。
        if (!Sources.TryGetValue(uri, out var source)) return null;
        return Decompile(source.AssemblyPath, source.FullTypeName, uri);
    }

    private static string? Decompile(string assemblyPath, string fullTypeName, string uri)
    {
        var lazy = Decompilers.GetOrAdd(assemblyPath, static path =>
            new Lazy<CSharpDecompiler?>(() => CreateDecompiler(path), LazyThreadSafetyMode.ExecutionAndPublication));
        if (lazy.Value is null) return null;

        try
        {
            var text = lazy.Value.DecompileTypeAsString(new FullTypeName(fullTypeName));
            Texts[uri] = text;
            Sources[uri] = (assemblyPath, fullTypeName);
            return text;
        }
        catch (Exception)
        {
            // 类型不可反编译（泛型实例化形态 / 元数据残缺）：诚实 null。
            return null;
        }
    }

    private static CSharpDecompiler? CreateDecompiler(string path)
    {
        try
        {
            var module = new PEFile(path);
            var resolver = new UniversalAssemblyResolver(path, throwOnError: false,
                module.DetectTargetFrameworkId());
            resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
            return new CSharpDecompiler(path, resolver, new DecompilerSettings());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 在探针编译的 PE 引用里反查类型所在程序集文件：按 AssemblyIdentity 全等
    /// （名字 + 版本 + 文化 + 公钥）匹配，避免同名不同版本拿错文件。
    /// </summary>
    private static string? FindAssemblyPath(Compilation compilation, IAssemblySymbol assembly)
    {
        foreach (var reference in compilation.References)
        {
            if (reference is not PortableExecutableReference pe || pe.FilePath is null) continue;
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol compiled) continue;
            if (!compiled.Identity.Equals(assembly.Identity)) continue;
            return pe.FilePath;
        }
        return null;
    }

    /// <summary>
    /// 虚拟 uri：<c>eui-decompiled:///TypeName.cs?assembly=...&amp;fullname=...</c>。
    /// 只用点/字母数字/转义，保证 VS Code Uri 往返（definition → 内容请求）后逐字符一致，
    /// 服务端缓存键才对得上。
    /// </summary>
    private static string BuildUri(string assemblyName, string fullTypeName, string simpleName) =>
        $"{Scheme}:///{Uri.EscapeDataString(simpleName)}.cs" +
        $"?assembly={Uri.EscapeDataString(assemblyName)}&fullname={Uri.EscapeDataString(fullTypeName)}";
}
