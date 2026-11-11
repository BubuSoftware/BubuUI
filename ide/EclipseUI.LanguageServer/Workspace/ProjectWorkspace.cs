using EclipseUI.Compiler;
using EclipseUI.Compiler.Metadata;
using EclipseUI.LanguageServer.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace EclipseUI.LanguageServer.Workspace;

/// <summary>一条 Tier 2 诊断：目标文件 + 该文件的偏移（CS 错误经 #line 映射回 .ui / .eustyle）。</summary>
public sealed record ProjectDiagnostic(string Path, UiDiagnostic Diagnostic);

/// <summary>
/// 一个项目的快照（design/06 §6.9 L2/L3/L4）：真源码 + 精确引用 + 正确选项的 Compilation，
/// 跑真生成器后的生成树，以及**与编译器同源**的目录（输入编译 + .ui 声明清单，spec/02 §2.7）。
/// 每项目一份，消除跨项目同名假歧义。
/// </summary>
public sealed class ProjectSnapshot
{
    public required ProjectEvaluation Evaluation { get; init; }
    public required Compilation Compilation { get; init; }
    public required UiCatalog Catalog { get; init; }
    public required IReadOnlyList<SyntaxTree> GeneratedTrees { get; init; }
    public required IReadOnlyList<ProjectDiagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>每个 .ui 的生成文档 + 双向映射（design/18 §5.2）；键 = .ui 完整路径。</summary>
    public required IReadOnlyDictionary<string, GeneratedDocument> GeneratedDocuments { get; init; }
}

/// <summary>
/// L1 + L2 + L3 + L4 的门面（design/06 §6.9.3）。唯一职责：把「一个文档属于哪些项目」和
/// 「这些项目的 Compilation / 目录 / Tier 2 诊断」算出来并缓存。
///
/// 口径总则：**编译时按什么，IDE 就按什么**。归属判据是求值后 AdditionalFiles 命中，
/// 一个文档被多个项目收就返回多个快照，没被任何项目收就明确提示、不给目录也不给诊断。
/// 打开的文档用编辑器缓冲区覆盖磁盘（未保存的 MyControl.cs 也可见，R4）。
/// </summary>
public sealed class ProjectWorkspace
{
    private const int MaxProjects = 64;

    private readonly CatalogOptions _options;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Lazy<ProjectSnapshot>> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    // L6 跨项目源码符号查询用的**轻量**编译：只有 Compile 源码树，不跑生成器/不算目录。
    private readonly Dictionary<string, Lazy<Compilation>> _sourceLookups = new(StringComparer.OrdinalIgnoreCase);
    private Lazy<IReadOnlyList<ProjectEvaluation>> _evaluations;
    private int _generation;

    public ProjectWorkspace(CatalogOptions options, Action<string>? log = null)
    {
        // 必须早于 Discover()/ProjectEvaluator.Evaluate() 被 JIT：这两个方法体内含
        // Microsoft.Build 类型，而 runtime 资产被排除，只能靠 Locator 的解析回调。
        MsBuildEnvironment.Ensure();
        _options = options;
        _log = log;
        _evaluations = new Lazy<IReadOnlyList<ProjectEvaluation>>(Discover, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool HasProjects => _evaluations.Value.Count > 0;

    /// <summary>已求值出的项目事实（只读；诊断与测试用）。</summary>
    public IReadOnlyList<ProjectEvaluation> Evaluations => _evaluations.Value;

    /// <summary>
    /// 无文档上下文时的目录（能力声明/命令面板/日志）。多项目工作区里"第一个项目"是
    /// 遍历顺序的偶然结果（可能是不含 .ui 的测试工程，目录只有内置标签），所以优先取
    /// **收了 .ui/.eustyle 的项目**——它才是 .ui 文档会命中的那份。仍按求值顺序取第一份，
    /// 不按标签数量挑（数量不是归属判据）。
    /// </summary>
    public UiCatalog? DefaultCatalog
    {
        get
        {
            var evaluations = _evaluations.Value;
            int TemplateFiles(ProjectEvaluation e) => e.AdditionalFiles.Count(static f =>
                Path.GetExtension(f) is ".ui" or ".eustyle");
            // 收 .ui/.eustyle 的项目优先，其中收得最多的优先（它最可能是当前在编辑的工程）；
            // 其余项目兜底。只影响无文档上下文时的展示值，文档级目录仍按 AdditionalFiles 命中。
            var ordered = evaluations.OrderByDescending(TemplateFiles).ThenBy(e => e.AssemblyName, StringComparer.Ordinal);
            foreach (var evaluation in ordered)
            {
                var snapshot = Snapshot(evaluation.ProjectPath);
                if (snapshot is not null) return snapshot.Catalog;
            }
            return null;
        }
    }

    public IReadOnlyList<string> Notes
    {
        get
        {
            var notes = new List<string>();
            lock (_gate)
            {
                foreach (var evaluation in _evaluations.Value) notes.AddRange(evaluation.Notes);
                foreach (var lazy in _snapshots.Values)
                    if (lazy.IsValueCreated) notes.AddRange(lazy.Value.Notes);
            }
            return notes;
        }
    }

    /// <summary>didOpen / didChange：缓冲区覆盖磁盘内容（L2，R4）。不依赖 watcher。</summary>
    public void ApplyBuffer(string uri, string text)
    {
        var path = DocumentStore.UriToPath(uri);
        if (path.Length == 0) return;
        lock (_gate)
        {
            _buffers[path] = text;
            _generation++;
            _snapshots.Clear();
            _sourceLookups.Clear();
        }
    }

    public void ForgetBuffer(string uri)
    {
        var path = DocumentStore.UriToPath(uri);
        if (path.Length == 0) return;
        lock (_gate)
        {
            if (!_buffers.Remove(path)) return;
            _generation++;
            _snapshots.Clear();
            _sourceLookups.Clear();
        }
    }

    public bool TryBuffer(string path, out string text)
    {
        lock (_gate) return _buffers.TryGetValue(path, out text!);
    }

    /// <summary>L7：watcher 变更。结构文件（csproj/props/assets）重求值；其余只丢快照。</summary>
    public void Invalidate(IEnumerable<string> paths)
    {
        var structural = false;
        foreach (var path in paths)
        {
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("project.assets.json", StringComparison.OrdinalIgnoreCase))
            {
                structural = true;
                break;
            }
        }

        lock (_gate)
        {
            _generation++;
            _snapshots.Clear();
            _sourceLookups.Clear();
            if (structural)
                _evaluations = new Lazy<IReadOnlyList<ProjectEvaluation>>(Discover, LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    public void Warmup()
    {
        foreach (var evaluation in _evaluations.Value) _ = Snapshot(evaluation.ProjectPath);
    }

    /// <summary>文档 → 命中 AdditionalFiles 的**全部**项目快照（§6.9.3 L1：不选一个）。</summary>
    public IReadOnlyList<ProjectSnapshot> ResolveAll(string path)
    {
        if (path.Length == 0 || !Path.IsPathRooted(path)) return Array.Empty<ProjectSnapshot>();
        var full = Path.GetFullPath(path);
        return _evaluations.Value
            .Where(e => e.Owns(full))
            .Select(e => Snapshot(e.ProjectPath))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToArray();
    }

    /// <summary>
    /// L6 跨项目源码回退（§6.9.0 夹具 4：框架属性 F12 落仓库源码）。当前项目把框架类型
    /// 当元数据引用时，符号没有源码位置；但同一工作区里往往有另一个项目**以源码**编译
    /// 同一类型（本仓库的 src/EclipseUI.Runtime 就是）。这里按元数据名逐份项目做 Roslyn
    /// 符号查询，只返回确有源码声明的类型符号——不是正则扫文本。
    ///
    /// 用的是**轻量编译**：只解析 Compile 源码树，不跑生成器、不算目录、不做全编译诊断。
    /// F12 是交互路径，不能为了找一次声明把整个解决方案的 Compilation 都算一遍；
    /// 轻量编译按项目缓存，缓冲区/watcher 失效时一并丢弃。
    /// </summary>
    public IEnumerable<INamedTypeSymbol> FindSourceTypes(string metadataName)
    {
        if (metadataName.Length == 0) yield break;
        foreach (var evaluation in _evaluations.Value)
        {
            Compilation compilation;
            try { compilation = SourceLookup(evaluation); }
            catch (Exception ex)
            {
                _log?.Invoke($"source lookup '{evaluation.ProjectPath}' failed: {ex.Message}");
                continue;
            }
            var type = compilation.GetTypeByMetadataName(metadataName);
            if (type is not null && type.Locations.Any(static l => l.IsInSource)) yield return type;
        }
    }

    private Compilation SourceLookup(ProjectEvaluation evaluation)
    {
        Lazy<Compilation> lazy;
        lock (_gate)
        {
            if (!_sourceLookups.TryGetValue(evaluation.ProjectPath, out lazy!))
            {
                lazy = new Lazy<Compilation>(() => BuildSourceLookup(evaluation),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _sourceLookups[evaluation.ProjectPath] = lazy;
            }
        }
        return lazy.Value;
    }

    private Compilation BuildSourceLookup(ProjectEvaluation evaluation)
    {
        var parseOptions = new CSharpParseOptions(evaluation.ParseLanguageVersion(),
            DocumentationMode.Parse, SourceCodeKind.Regular, evaluation.DefineConstants);
        var trees = new List<SyntaxTree>();
        foreach (var path in evaluation.CompileFiles)
            if (TryRead(path, out var text))
                trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(text), parseOptions, path));
        // 零引用即可解析出项目自己声明的类型；BaseType 可能是 error symbol，但
        // 取成员声明位置只需要当前类型自己的源码树。
        return CSharpCompilation.Create(evaluation.AssemblyName + ".SourceLookup", trees);
    }

    /// <summary>该磁盘文档是否不属于任何项目（编译时不参与 → IDE 不给目录/诊断，明确提示）。</summary>
    public bool IsUnowned(string path)
    {
        if (!HasProjects || path.Length == 0 || !Path.IsPathRooted(path)) return false;
        var full = Path.GetFullPath(path);
        return !_evaluations.Value.Any(e => e.Owns(full));
    }

    private ProjectSnapshot? Snapshot(string projectPath)
    {
        Lazy<ProjectSnapshot> lazy;
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(projectPath, out lazy!))
            {
                var evaluation = _evaluations.Value.FirstOrDefault(e =>
                    string.Equals(e.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase));
                if (evaluation is null) return null;
                var generation = _generation;
                lazy = new Lazy<ProjectSnapshot>(() => Build(evaluation, generation),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _snapshots[projectPath] = lazy;
            }
        }
        try { return lazy.Value; }
        catch (Exception ex)
        {
            _log?.Invoke($"project snapshot '{projectPath}' failed: {ex}");
            return null;
        }
    }

    private IReadOnlyList<ProjectEvaluation> Discover()
    {
        MsBuildEnvironment.Ensure();
        var root = _options.ProbeRoot;
        var result = new List<ProjectEvaluation>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

        // VS Code 传的是打开的文件夹；只打开 samples/Gallery 时，框架源码工程
        // (src/EclipseUI.Runtime) 不在其中，L6 的"框架属性 F12 落仓库源码"就会退回反编译。
        // 所以扫描起点向上扩到仓库根（.git / *.sln* 标记）——**只扩大"已知项目集合"**，
        // 文档归属仍严格按求值后 AdditionalFiles 命中，不看离哪个项目近（§6.9.3 L1）。
        var scanRoot = FindRepositoryRoot(Path.GetFullPath(root)) ?? Path.GetFullPath(root);

        var collection = new Microsoft.Build.Evaluation.ProjectCollection();
        var projects = new List<string>();
        try
        {
            var pending = new Stack<string>();
            pending.Push(scanRoot);
            while (pending.Count > 0 && projects.Count < MaxProjects)
            {
                var directory = pending.Pop();
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(directory))
                    {
                        var name = Path.GetFileName(sub);
                        if (name is "bin" or "obj" or ".git" or "node_modules" or ".vs") continue;
                        pending.Push(sub);
                    }
                    foreach (var file in Directory.EnumerateFiles(directory, "*.csproj"))
                    {
                        projects.Add(file);
                        if (projects.Count >= MaxProjects) break;
                    }
                }
                catch (Exception)
                {
                    // 权限/并发删除：跳过该目录。
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"project discovery failed: {ex.Message}");
        }

        foreach (var path in projects)
        {
            try { result.Add(ProjectEvaluator.Evaluate(path, collection)); }
            catch (Exception ex) { _log?.Invoke($"evaluate '{path}' failed: {ex.Message}"); }
        }
        _log?.Invoke($"projects: {result.Count} evaluated from {scanRoot} (workspace {root})");
        return result;
    }

    private static string? FindRepositoryRoot(string workspaceRoot)
    {
        try
        {
            for (var current = new DirectoryInfo(workspaceRoot); current is not null; current = current.Parent)
            {
                var path = current.FullName;
                if (Directory.Exists(Path.Combine(path, ".git")) ||
                    File.Exists(Path.Combine(path, ".git")) ||
                    current.EnumerateFiles("*.sln*").Any())
                    return path;
            }
        }
        catch (Exception)
        {
            // 不可读的祖先目录：退回原工作区根，不让发现阶段整体失败。
        }
        return null;
    }

    private ProjectSnapshot Build(ProjectEvaluation evaluation, int generation)
    {
        var notes = new List<string>(evaluation.Notes);
        var parseOptions = new CSharpParseOptions(evaluation.ParseLanguageVersion(),
            DocumentationMode.Parse, SourceCodeKind.Regular, evaluation.DefineConstants);

        var trees = new List<SyntaxTree>();
        foreach (var path in evaluation.CompileFiles)
        {
            if (!TryRead(path, out var text)) continue;
            trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(text), parseOptions, path));
        }
        if (evaluation.UsingNamespaces.Count > 0)
        {
            var globalUsings = string.Join("\n", evaluation.UsingNamespaces);
            trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(globalUsings), parseOptions,
                Path.Combine(evaluation.Directory, "obj", "GlobalUsings.g.cs")));
        }

        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            if (!File.Exists(path) || !seen.Add(Path.GetFullPath(path))) return;
            try { references.Add(MetadataReference.CreateFromFile(path)); }
            catch (Exception ex) { notes.Add($"cannot reference '{path}': {ex.Message}"); }
        }
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        foreach (var path in tpa.Split(Path.PathSeparator))
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) Add(path);
        foreach (var path in evaluation.References) Add(path);

        var nullable = evaluation.Nullable.Trim().ToLowerInvariant() switch
        {
            "enable" => NullableContextOptions.Enable,
            "warnings" => NullableContextOptions.Warnings,
            "annotations" => NullableContextOptions.Annotations,
            _ => NullableContextOptions.Disable,
        };
        var options = new CSharpCompilationOptions(evaluation.CompilationOutputKind(), nullableContextOptions: nullable);
        var compilation = CSharpCompilation.Create(evaluation.AssemblyName, trees, references, options);

        var additional = new List<(string Path, string Text)>();
        foreach (var path in evaluation.AdditionalFiles)
            if (TryRead(path, out var text)) additional.Add((path, text));

        var generated = GeneratorHost.Run(compilation, additional, parseOptions, evaluation.RootNamespace);
        // spec/02 §2.7：目录与编译器同源——输入编译 + .ui 声明清单。
        // 此前这里建在生成后编译上（看得见 .g.cs 里的 `: Component`），而真正跑构建的生成器
        // 看不见自己的产物，于是同一个标签「编辑器里绿、dotnet build 报 EUI001」。
        // 清单来自 AdditionalFiles，与生成器读的 AdditionalTexts 是同一批文件、同一份提取器。
        var catalog = UiCatalogFactory.Build(compilation, UiComponentScanner.FromFiles(additional));
        var diagnostics = MapDiagnostics(evaluation, generated.Diagnostics, generated.Compilation);

        _log?.Invoke($"project '{evaluation.AssemblyName}': {trees.Count} trees, " +
                     $"{references.Count} refs, {generated.GeneratedTrees.Count} generated, " +
                     $"{catalog.Elements.Count} tags, {diagnostics.Count} tier-2 diagnostics (gen {generation})");
        return new ProjectSnapshot
        {
            Evaluation = evaluation,
            Compilation = generated.Compilation,
            Catalog = catalog,
            GeneratedTrees = generated.GeneratedTrees,
            Diagnostics = diagnostics,
            Notes = notes,
            GeneratedDocuments = generated.GeneratedDocuments,
        };
    }

    /// <summary>
    /// Tier 2（§6.9.4）：生成器诊断（EUI，Location.Create(additionalFile, span)）+ 生成树编译的
    /// CS 错误（经 span 型 #line 映射回 .ui）统一按目标文件映射成 .ui 偏移。
    /// 与 build 同源——不写 CS→EUI 翻译器。映不回的（路径不在本项目 AdditionalFiles 内）丢弃。
    /// </summary>
    private IReadOnlyList<ProjectDiagnostic> MapDiagnostics(
        ProjectEvaluation evaluation,
        System.Collections.Immutable.ImmutableArray<Diagnostic> generatorDiagnostics,
        Compilation outputCompilation)
    {
        var owned = new HashSet<string>(evaluation.AdditionalFiles, StringComparer.OrdinalIgnoreCase);
        var result = new List<ProjectDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Emit(Diagnostic diagnostic)
        {
            if (diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden ||
                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Info) return;

            var location = diagnostic.Location;
            // 两类位置都要吃：① 生成树里的 CS 诊断，GetMappedLineSpan 会按 #line 回到 .ui；
            // ② 生成器用 Location.Create(path, …) 直接报在 .eustyle/.ui 上的 EUI 诊断——
            //    它不是 IsInSource，但 GetMappedLineSpan 仍给出真实文件与行位（不能按
            //    IsInSource 过滤，否则 .eustyle 的 EUI050–052 会被整批丢掉）。
            var lineSpan = location.GetMappedLineSpan();
            var path = string.IsNullOrEmpty(lineSpan.Path)
                ? location.SourceTree?.FilePath ?? ""
                : lineSpan.Path;
            if (path.Length == 0 || !owned.Contains(Path.GetFullPath(path))) return;
            if (Path.GetExtension(path) is not (".ui" or ".eustyle")) return;
            if (!TryRead(path, out var text)) return;

            var positions = new PositionMap(text);
            var start = positions.ToOffset(lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character);
            var end = positions.ToOffset(lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character);
            if (end <= start) end = Math.Min(text.Length, start + Math.Max(1, location.SourceSpan.Length));
            var span = new EclipseUI.Compiler.TextSpan(start, Math.Max(1, end - start));
            var severity = diagnostic.Severity switch
            {
                Microsoft.CodeAnalysis.DiagnosticSeverity.Error => EclipseUI.Compiler.DiagnosticSeverity.Error,
                Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => EclipseUI.Compiler.DiagnosticSeverity.Warning,
                _ => EclipseUI.Compiler.DiagnosticSeverity.Info,
            };
            var ui = new UiDiagnostic(diagnostic.Id, diagnostic.GetMessage(), severity, span);
            var key = $"{path}|{diagnostic.Id}|{span.Start}|{span.Length}|{diagnostic.GetMessage()}";
            if (!seen.Add(key)) return;
            result.Add(new ProjectDiagnostic(path, ui));
        }

        foreach (var diagnostic in generatorDiagnostics) Emit(diagnostic);
        foreach (var diagnostic in outputCompilation.GetDiagnostics()) Emit(diagnostic);
        return result;
    }

    private bool TryRead(string path, out string text)
    {
        if (TryBuffer(path, out text!)) return true;
        try
        {
            if (!File.Exists(path)) { text = ""; return false; }
            text = File.ReadAllText(path);
            return true;
        }
        catch (Exception)
        {
            text = "";
            return false;
        }
    }
}
