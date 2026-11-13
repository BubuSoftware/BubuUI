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

    /// <summary>额外程序集（用户自己编译出的 app.dll）：其中派生自 UiObject 的类型会成为标签。</summary>
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
    private readonly object _gate = new();
    private Lazy<UiCatalog> _catalog;

    public CatalogProvider(CatalogOptions options)
    {
        _options = options;
        _catalog = new Lazy<UiCatalog>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>命中扫描的 <c>EclipseUI.Runtime.dll</c> 绝对路径；null = 没找到（目录为空）。</summary>
    public string? RuntimePath { get; private set; }

    /// <summary>
    /// 工作区根（VS Code/Rider 启动时传的 <c>--workspace</c>）。重命名扫工程内其余
    /// <c>.cs</c> 引用用它；null = 宿主没传，重命名的成员分支整体停用（不给不完备的编辑）。
    /// </summary>
    public string? WorkspaceRoot => _options.ProbeRoot;

    /// <summary>
    /// 目录构建用的探针 Compilation（TPA + Runtime + Rendering + 应用程序集，无源码树）。
    /// 表达式投影在它之上派生（加 code-behind 与 @code 合成树）；没找到 runtime 时为 null，
    /// 表达式补全/悬停与语义诊断一样整体停用。
    /// </summary>
    public Compilation? Compilation { get; private set; }

    /// <summary>
    /// 启动期问题（找不到 runtime / 程序集读不了）。宿主转成 window/showMessage。
    /// 读之前**必须**先完成 Build：后台预热与 initialized 通知并发时，若在 Build 前读会得到
    /// 空列表，于是"目录没找到"的警告永远不发出，编辑器表现为"高亮正常但零诊断"。
    /// 返回快照，避免调用方持有会被 Reload 清空的集合。
    /// </summary>
    public IReadOnlyList<string> Notes
    {
        get
        {
            _ = Catalog;
            lock (_gate) return _notes.ToArray();
        }
    }

    public UiCatalog Catalog => _catalog.Value;

    public void Reload()
    {
        lock (_gate)
            _catalog = new Lazy<UiCatalog>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private UiCatalog Build()
    {
        // notes 先在局部收集、构建结束时一次性发布：Lazy 保证同一实例只跑一次 Build，
        // 但 Reload 会换实例，不能被并发的 Clear/Add 交错成空列表。
        var notes = new List<string>();
        RuntimePath = DiscoverRuntime(notes);

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
                notes.Add($"cannot reference '{path}': {ex.Message}");
            }
        }

        // 共享框架：目录扫描要通过 Roslyn 解析 UiObject/StyledProperty 等元数据。
        foreach (var path in TrustedPlatformAssemblies()) Add(path);

        if (RuntimePath is null)
        {
            notes.Add($"'{RuntimeAssemblyFileName}' not found - tag completion AND semantic " +
                      "diagnostics (EUI001/EUI002/...) are disabled. " +
                      "Pass --runtime <path> or set eclipseui.runtime.path in VS Code settings.");
            PublishNotes(notes);
            Compilation = null;
            return EmptyCatalog();
        }

        Add(RuntimePath);

        // 样式属性目录从 StyleBuilder/Visual 的符号派生（design/17 §4.3），需要渲染程序集在场；
        // 缺了不会崩，但 `Style="CornerRadius: 8"` 会被误判成未知属性（EUI051），所以尽量找。
        var rendering = FindRenderingAssembly(RuntimePath);
        if (rendering is not null) Add(rendering);

        // 应用自己的输出目录：用户组件（UiObject 派生）就在这里。
        var runtimeDirectory = Path.GetDirectoryName(RuntimePath)!;

        foreach (var assembly in _options.Assemblies) Add(assembly);

        if (_options.IncludeSiblingAssemblies)
        {
            foreach (var candidate in SiblingAssemblies(runtimeDirectory))
                Add(candidate);
        }

        // 工作区自己的构建产物：用户组件（.ui 派生类、自定义控件）在 Gallery.dll 这类
        // app 程序集里。开发布局下 runtime 在 src/EclipseUI.Runtime/bin，同级扫描够不着它，
        // 所以再从工作区 bin 目录里收一遍（引用了 runtime 的非框架程序集）。
        if (_options.ProbeRoot is not null)
        {
            foreach (var candidate in WorkspaceAssemblies(_options.ProbeRoot))
                Add(candidate);
        }

        var compilation = CSharpCompilation.Create(
            "EclipseUICatalogProbe",
            Array.Empty<SyntaxTree>(),
            references,
            // 可空上下文 Enabled：code-behind/@code 合成树没有 #nullable 指令，继承编译级设置。
            // 保持 Disabled 会把 oblivious 类型在探针树（#nullable enable）里消费成 maybe-null，
            // 表达式悬停/补全的类型显示就多出假 `?`。与仓库 Build.props 的全局 Nullable enable 对齐。
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: Microsoft.CodeAnalysis.NullableContextOptions.Enable));

        var catalog = UiCatalogFactory.Build(compilation);
        Compilation = compilation;
        PublishNotes(notes);
        return catalog;
    }

    private void PublishNotes(List<string> notes)
    {
        lock (_gate)
        {
            _notes.Clear();
            _notes.AddRange(notes);
        }
    }

    /// <summary>文档不属于任何项目时的空目录（§6.9.3 L1：不给目录、不给诊断）。</summary>
    public static UiCatalog EmptyCatalog() =>
        new(Array.Empty<UiElementDef>(), Array.Empty<UiHiddenTag>(), Array.Empty<UiMetadataProblem>());

    /// <summary>
    /// 发现顺序：显式参数 → 环境变量 → server 自身目录（发布版随包带一份）→
    /// 从工作区向上找仓库布局 <c>src/EclipseUI.Runtime/bin/{Release,Debug}/net*/</c>。
    /// 开发机上最后一条最好用：不用配任何东西，打开仓库即生效。
    /// </summary>
    private string? DiscoverRuntime(List<string> notes)
    {
        var explicitPath = new[] { _options.RuntimePath, Environment.GetEnvironmentVariable("ECLIPSEUI_RUNTIME") }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (explicitPath is not null)
        {
            var resolved = Resolve(explicitPath);
            if (resolved is not null) return resolved;
            // 显式配置无效时不要静默回退到自动发现：用户以为自己指定了产物，
            // 实际用的是另一份，排错时这会变成最难查的一类"看起来生效了"。
            notes.Add($"explicit runtime path '{explicitPath}' has no {RuntimeAssemblyFileName}; " +
                      "tag completion AND semantic diagnostics are disabled.");
            return null;
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

    /// <summary>
    /// 工作区 bin 目录里的应用/第三方程序集（引用了 EclipseUI.Runtime 的非框架 dll）。
    /// 开发布局下 <see cref="SiblingAssemblies"/> 的起点是框架 runtime 的 bin，够不着应用
    /// 自己的输出（samples/Gallery/bin/…/Gallery.dll）——这里补上。准入与同级扫描一致：
    /// 只认显式引用 runtime 的程序集；<c>EclipseUI.*</c> 框架程序集排除（runtime 已由
    /// <see cref="DiscoverRuntime"/> 定位，双份会让探针编译的类型歧义）。
    /// </summary>
    private IEnumerable<string> WorkspaceAssemblies(string workspaceRoot)
    {
        List<string> binDirectories;
        try
        {
            // 只找构建产物目录（bin）；数量有上限，防巨型工作区拖慢启动。
            binDirectories = Directory
                .EnumerateDirectories(workspaceRoot, "bin", SearchOption.AllDirectories)
                .Take(24)
                .ToList();
        }
        catch (Exception)
        {
            yield break;
        }

        // 同名 dll（bin/Debug 与 bin/Release 各一份 Gallery.dll）只留最新的：
        // 双份会进两套相同类型，探针的 GetTypeByMetadataName 直接歧义。
        var newest = new Dictionary<string, (string Path, DateTime Time)>(StringComparer.OrdinalIgnoreCase);
        var probed = 0;
        foreach (var bin in binDirectories)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(bin, "*.dll", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (probed >= 200) break;
                probed++;

                var name = Path.GetFileName(file);
                if (name.StartsWith("EclipseUI.", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (!ReferencesAssembly(file, "EclipseUI.Runtime")) continue;

                var time = File.GetLastWriteTimeUtc(file);
                if (!newest.TryGetValue(name, out var existing) || time > existing.Time)
                    newest[name] = (file, time);
            }
            if (probed >= 200) break;
        }

        foreach (var entry in newest.Values) yield return entry.Path;
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
