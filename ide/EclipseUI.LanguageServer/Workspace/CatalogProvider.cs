using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using EclipseUI.Compiler;
using EclipseUI.Compiler.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EclipseUI.LanguageServer.Workspace;

/// <summary>目录发现参数：全部来自 CLI 与 VS Code 设置（design/06 §6.4 的 server 启动参数）。</summary>
public sealed class CatalogOptions
{
    /// <summary>显式的 <c>EclipseUI.Runtime.dll</c> 或其所在目录。</summary>
    public string? RuntimePath { get; init; }

    /// <summary>额外程序集（用户自己编译出的 app.dll）：其中派生自 Element/Component 的类型会成为标签。</summary>
    public IReadOnlyList<string> Assemblies { get; init; } = Array.Empty<string>();

    /// <summary>探测器起点：工作区根目录（用于无显式路径时向上/向下找仓库内构建产物）。</summary>
    public string? ProbeRoot { get; init; }

    /// <summary>
    /// 把 runtime 同目录下**引用了 EclipseUI.Runtime** 的程序集一并扫描：应用输出目录里
    /// 天然躺着 <c>MyApp.dll</c>，不额外配置也能补全用户组件。
    /// </summary>
    public bool IncludeSiblingAssemblies { get; init; } = true;
}

/// <summary>
/// 模板目录的提供者。目录本身**不手写**：和编译器走同一条
/// <see cref="UiCatalogFactory"/> 符号派生路径（design/14 §14.10.2），
/// 所以 IDE 里的补全/悬停与编译期诊断看到的是同一份表面。
///
/// 构造一次探针 <see cref="Compilation"/>（TPA + Runtime + 应用程序集），
/// 之后只读缓存；<see cref="Reload"/> 供 <c>workspace/didChangeWatchedFiles</c> 或
/// 手动命令触发（编译产物变了，标签表要跟着变）。
/// </summary>
public sealed class CatalogProvider
{
    public const string RuntimeAssemblyFileName = "EclipseUI.Runtime.dll";

    private readonly CatalogOptions _options;
    private readonly List<string> _notes = new();
    private UiCatalog? _catalog;

    public CatalogProvider(CatalogOptions options) => _options = options;

    /// <summary>命中扫描的 <c>EclipseUI.Runtime.dll</c> 绝对路径；null = 没找到（目录为空）。</summary>
    public string? RuntimePath { get; private set; }

    /// <summary>启动期问题（找不到 runtime / 程序集读不了）。宿主转成 window/showMessage。</summary>
    public IReadOnlyList<string> Notes => _notes;

    public UiCatalog Catalog => _catalog ??= Build();

    public void Reload() => _catalog = null;

    private UiCatalog Build()
    {
        _notes.Clear();
        RuntimePath = DiscoverRuntime();

        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            if (!File.Exists(path) || !seen.Add(Path.GetFullPath(path))) return;
            try
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch (Exception ex)
            {
                _notes.Add($"cannot reference '{path}': {ex.Message}");
            }
        }

        // 共享框架：目录扫描要通过 Roslyn 解析 Element/Component/StyledProperty 等元数据。
        foreach (var path in TrustedPlatformAssemblies()) Add(path);

        if (RuntimePath is null)
        {
            _notes.Add($"'{RuntimeAssemblyFileName}' not found — tag completion is disabled. " +
                       "Pass --runtime <path> or set eclipseui.server.runtime.");
            return EmptyCatalog();
        }

        Add(RuntimePath);

        // 样式属性目录从 StyleBuilder/Visual 的符号派生（design/17 §4.3），需要渲染程序集在场；
        // 缺了不会崩，但 `Style="CornerRadius: 8"` 会被误判成未知属性（EUI051），所以尽量找。
        var rendering = FindRenderingAssembly(RuntimePath);
        if (rendering is not null) Add(rendering);

        // 应用自己的输出目录：用户组件（Element/Component 派生）就在这里。
        var runtimeDirectory = Path.GetDirectoryName(RuntimePath)!;

        foreach (var assembly in _options.Assemblies) Add(assembly);

        if (_options.IncludeSiblingAssemblies)
        {
            foreach (var candidate in SiblingAssemblies(runtimeDirectory))
                Add(candidate);
        }

        var compilation = CSharpCompilation.Create(
            "EclipseUICatalogProbe",
            Array.Empty<SyntaxTree>(),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return UiCatalogFactory.Build(compilation);
    }

    private static UiCatalog EmptyCatalog() =>
        new(Array.Empty<UiElementDef>(), Array.Empty<UiHiddenTag>(), Array.Empty<UiMetadataProblem>());

    /// <summary>
    /// 发现顺序：显式参数 → 环境变量 → server 自身目录（发布版随包带一份）→
    /// 从工作区向上找仓库布局 <c>src/EclipseUI.Runtime/bin/{Release,Debug}/net*/</c>。
    /// 开发机上最后一条最好用：不用配任何东西，打开仓库即生效。
    /// </summary>
    private string? DiscoverRuntime()
    {
        foreach (var candidate in new[] { _options.RuntimePath, Environment.GetEnvironmentVariable("ECLIPSEUI_RUNTIME") })
        {
            var resolved = Resolve(candidate);
            if (resolved is not null) return resolved;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, RuntimeAssemblyFileName);
        if (File.Exists(beside)) return Path.GetFullPath(beside);

        foreach (var start in new[] { _options.ProbeRoot, AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(start)) continue;
            var found = ProbeRepositoryLayout(start);
            if (found is not null) return found;
        }

        return null;
    }

    private static string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Directory.Exists(path))
        {
            var inDirectory = Path.Combine(path, RuntimeAssemblyFileName);
            return File.Exists(inDirectory) ? Path.GetFullPath(inDirectory) : null;
        }
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private static string? ProbeRepositoryLayout(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            var bin = Path.Combine(directory.FullName, "src", "EclipseUI.Runtime", "bin");
            if (!Directory.Exists(bin)) continue;
            var found = NewestRuntimeBinary(bin);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// 渲染程序集：应用输出目录里天然与 runtime 同级；仓库开发布局下在
    /// <c>src/EclipseUI.Rendering/bin/…</c>（runtime 的同级项目，不会出现在它的 bin 里）。
    /// </summary>
    private static string? FindRenderingAssembly(string runtimePath)
    {
        var directory = Path.GetDirectoryName(runtimePath);
        if (directory is null) return null;

        var beside = Path.Combine(directory, "EclipseUI.Rendering.dll");
        if (File.Exists(beside)) return beside;

        var root = FindRepositoryRoot(directory);
        if (root is null) return null;
        var bin = Path.Combine(root, "src", "EclipseUI.Rendering", "bin");
        if (!Directory.Exists(bin)) return null;

        try
        {
            return Directory
                .EnumerateFiles(bin, "EclipseUI.Rendering.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "EclipseUI.Runtime")))
                return directory.FullName;
        }
        return null;
    }

    private static string? NewestRuntimeBinary(string binRoot)
    {
        try
        {
            return Directory
                .EnumerateFiles(binRoot, RuntimeAssemblyFileName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 应用输出目录里引用了 EclipseUI.Runtime 的程序集（用户组件就在这里）。
    /// 只收**显式引用 runtime** 的：框架同伴程序集混进来会制造重复类型，
    /// 而扫描器本来也只认"直接引用 runtime"的程序集（design/14 §14.4.6）。
    /// </summary>
    private IEnumerable<string> SiblingAssemblies(string runtimeDirectory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(runtimeDirectory, "*.dll", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, RuntimeAssemblyFileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, "EclipseUI.Rendering.dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (ReferencesAssembly(file, "EclipseUI.Runtime")) yield return file;
        }
    }

    /// <summary>读 PE 元数据的 AssemblyRef 表判断引用关系——不加载程序集（不执行用户代码）。</summary>
    private static bool ReferencesAssembly(string path, string assemblyName)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return false;
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.AssemblyReferences)
            {
                if (string.Equals(reader.GetString(reader.GetAssemblyReference(handle).Name), assemblyName,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> TrustedPlatformAssemblies()
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(tpa)) yield break;
        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }
}
