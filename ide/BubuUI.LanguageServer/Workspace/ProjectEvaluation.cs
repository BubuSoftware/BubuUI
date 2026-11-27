using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BubuUI.LanguageServer.Workspace;

/// <summary>
/// 一个 csproj 的**求值结果**（design/06 §6.9.3 L1）。求值 = 读 csproj + 递归 SDK imports +
/// 算属性最终值 + 展开 Item glob/Condition，**不执行任何 Target**。因此这里是编译时的口径，
/// 而不是自己解析 csproj 或扫 <c>**/*.cs</c>（§6.9.8 明令禁止的第二事实源）。
///
/// 与 build 的固有差集：<c>obj/…/AssemblyInfo.cs</c> 这类执行阶段才加入的 Compile item
/// 不在其中（SDK 10.0.400 已核实），这是已知且允许的差集。
/// </summary>
public sealed class ProjectEvaluation
{
    public required string ProjectPath { get; init; }
    public required string Directory { get; init; }
    public required string AssemblyName { get; init; }
    public required string RootNamespace { get; init; }
    public required string Nullable { get; init; }
    public required string LangVersion { get; init; }
    public required string OutputType { get; init; }
    public required string TargetFramework { get; init; }
    public required IReadOnlyList<string> CompileFiles { get; init; }
    public required IReadOnlyList<string> AdditionalFiles { get; init; }
    public required IReadOnlyList<string> UsingNamespaces { get; init; }
    public required IReadOnlyList<string> DefineConstants { get; init; }
    public required IReadOnlyList<string> References { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>归属判据：求值后 AdditionalFiles 命中该文件（§6.9.3 L1）——不看 slnx、不看目录远近。</summary>
    public bool Owns(string path) => AdditionalFiles.Contains(path, StringComparer.OrdinalIgnoreCase);

    public LanguageVersion ParseLanguageVersion() => LangVersion.Trim().ToLowerInvariant() switch
    {
        "preview" => LanguageVersion.Preview,
        "latest" or "latestmajor" => LanguageVersion.Latest,
        "default" => LanguageVersion.Default,
        _ => LanguageVersion.Latest,
    };

    public OutputKind CompilationOutputKind() => OutputType.Trim().ToLowerInvariant() switch
    {
        "exe" => OutputKind.ConsoleApplication,
        "winexe" => OutputKind.WindowsApplication,
        "module" => OutputKind.NetModule,
        _ => OutputKind.DynamicallyLinkedLibrary,
    };
}

/// <summary>MSBuild 求值 + assets.json 引用解析。所有 IO 失败都降级成 Notes，不抛。</summary>
public static class ProjectEvaluator
{
    public static ProjectEvaluation Evaluate(string csprojPath, Microsoft.Build.Evaluation.ProjectCollection collection)
    {
        MsBuildEnvironment.Ensure();
        csprojPath = Path.GetFullPath(csprojPath);
        var directory = Path.GetDirectoryName(csprojPath)!;
        var notes = new List<string>();
        var project = GetOrLoad(collection, csprojPath);

        string Prop(string name) => project.GetPropertyValue(name) ?? "";
        IReadOnlyList<string> Items(string type) => project.GetItems(type)
            .Select(i => i.EvaluatedInclude)
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        var compile = ResolvePaths(directory, Items("Compile"));
        var additional = ResolvePaths(directory, Items("AdditionalFiles"));

        // ImplicitUsings 的展开来源就是求值出来的 Using items（SDK 在 .props 里定义）——
        // 不手写 System/System.Linq 清单，与编译期口径一致（spike：Gallery 得 7 条）。
        var usings = project.GetItems("Using")
            .Select(static i =>
            {
                var name = i.EvaluatedInclude;
                var alias = i.GetMetadataValue("Alias");
                var isStatic = string.Equals(i.GetMetadataValue("Static"), "true", StringComparison.OrdinalIgnoreCase);
                if (isStatic) return "global using static global::" + name + ";";
                if (!string.IsNullOrEmpty(alias)) return "global using " + alias + " = global::" + name + ";";
                return "global using global::" + name + ";";
            })
            .ToArray();

        var references = ReadReferences(directory, project, collection, notes);

        return new ProjectEvaluation
        {
            ProjectPath = csprojPath,
            Directory = directory,
            AssemblyName = string.IsNullOrEmpty(Prop("AssemblyName")) ? Path.GetFileNameWithoutExtension(csprojPath) : Prop("AssemblyName"),
            RootNamespace = Prop("RootNamespace"),
            Nullable = Prop("Nullable"),
            LangVersion = Prop("LangVersion"),
            OutputType = Prop("OutputType"),
            TargetFramework = Prop("TargetFramework"),
            CompileFiles = compile,
            AdditionalFiles = additional,
            UsingNamespaces = usings,
            DefineConstants = Prop("DefineConstants").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            References = references,
            Notes = notes,
        };
    }

    /// <summary>
    /// 同一个 <see cref="Microsoft.Build.Evaluation.ProjectCollection"/> 里对同一路径重复
    /// <c>new Project(...)</c> 会抛「已有等效项目」。ProjectReference 解析会顺带把被引用
    /// 工程加载进集合，之后 Discover 再把它当顶层工程求值时就撞上。——所以一律先查
    /// LoadedProjects 复用，不只是为了省一次求值，更是为了不静默丢掉整个项目。
    /// </summary>
    private static Microsoft.Build.Evaluation.Project GetOrLoad(
        Microsoft.Build.Evaluation.ProjectCollection collection, string path)
    {
        var full = Path.GetFullPath(path);
        var existing = collection.LoadedProjects.FirstOrDefault(p =>
            string.Equals(Path.GetFullPath(p.FullPath), full, StringComparison.OrdinalIgnoreCase));
        var project = existing ?? new Microsoft.Build.Evaluation.Project(full, null, null, collection);
        // An outer multi-target build has no Compile items or platform references.
        // Use its first declared target for the standalone language-server context.
        if (string.IsNullOrWhiteSpace(project.GetPropertyValue("TargetFramework")))
        {
            var framework = project.GetPropertyValue("TargetFrameworks")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (framework is not null)
            {
                project.SetGlobalProperty("TargetFramework", framework);
                project.ReevaluateIfNecessary();
            }
        }
        return project;
    }

    private static IReadOnlyList<string> ResolvePaths(string directory, IEnumerable<string> includes)
    {
        var result = new List<string>();
        foreach (var include in includes)
        {
            var normalized = include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var full = Path.IsPathRooted(normalized) ? normalized : Path.Combine(directory, normalized);
            full = Path.GetFullPath(full);
            if (!result.Contains(full, StringComparer.OrdinalIgnoreCase)) result.Add(full);
        }
        return result;
    }

    /// <summary>
    /// 引用路径从 <c>obj/project.assets.json</c> 取（restore 产物，精确到 dll）：package 的
    /// compile assets + project 的求值 TargetPath。不跑 ResolveReferences、不扫 bin 目录
    /// （§6.9.5 净减代码：删掉整套 dll 启发式）。
    /// </summary>
    private static IReadOnlyList<string> ReadReferences(string directory,
        Microsoft.Build.Evaluation.Project project,
        Microsoft.Build.Evaluation.ProjectCollection collection, List<string> notes)
    {
        var result = new List<string>();
        var assetsPath = Path.Combine(directory, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
        {
            notes.Add($"'{project.GetPropertyValue("AssemblyName")}' has no obj/project.assets.json - " +
                      "run `dotnet restore` to enable project-compilation based IDE features.");
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("targets", out var targets) ||
                !root.TryGetProperty("libraries", out var libraries) ||
                !root.TryGetProperty("packageFolders", out var folders)) return result;

            var folderPaths = folders.EnumerateObject().Select(p => p.Name).ToArray();
            var framework = project.GetPropertyValue("TargetFramework");
            var target = targets.EnumerateObject().FirstOrDefault(t => t.Name == framework);
            if (target.Value.ValueKind != JsonValueKind.Object)
                target = targets.EnumerateObject().FirstOrDefault();
            if (target.Value.ValueKind != JsonValueKind.Object) return result;

            foreach (var lib in target.Value.EnumerateObject())
            {
                var type = lib.Value.TryGetProperty("type", out var t) ? t.GetString() : "";
                if (type == "project")
                {
                    // project 类型的 compile 是 bin/placeholder/*.dll 占位符，真实产物从被引用
                    // 项目的求值属性取（OutputPath + TargetFileName），不是文件名探测。
                    if (!libraries.TryGetProperty(lib.Name, out var meta) ||
                        !meta.TryGetProperty("msbuildProject", out var msbuild)) continue;
                    var relative = msbuild.GetString();
                    if (string.IsNullOrEmpty(relative)) continue;
                    var refPath = Path.GetFullPath(Path.Combine(directory,
                        relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                    if (!File.Exists(refPath)) continue;
                    var refProject = GetOrLoad(collection, refPath);
                    var output = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(refPath)!,
                        refProject.GetPropertyValue("OutputPath").Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar),
                        refProject.GetPropertyValue("TargetFileName")));
                    if (File.Exists(output)) result.Add(output);
                    else notes.Add($"project reference '{lib.Name}' is not built ({output}); " +
                                  "build the solution once to see its controls in the IDE.");
                    continue;
                }

                if (!lib.Value.TryGetProperty("compile", out var compile)) continue;
                var libPath = libraries.TryGetProperty(lib.Name, out var libraryMeta) &&
                              libraryMeta.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (libPath is null) continue;
                foreach (var entry in compile.EnumerateObject())
                {
                    // NuGet 的空包约定；占位符不是程序集。
                    if (entry.Name.EndsWith("_._", StringComparison.Ordinal)) continue;
                    foreach (var folder in folderPaths)
                    {
                        var dll = Path.GetFullPath(Path.Combine(folder,
                            libPath.Replace('/', Path.DirectorySeparatorChar),
                            entry.Name.Replace('/', Path.DirectorySeparatorChar)));
                        if (!File.Exists(dll)) continue;
                        result.Add(dll);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            notes.Add($"cannot read '{assetsPath}': {ex.Message}");
        }
        return result;
    }
}
