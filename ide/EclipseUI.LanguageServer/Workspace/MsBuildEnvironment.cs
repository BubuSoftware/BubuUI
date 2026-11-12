using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace EclipseUI.LanguageServer.Workspace;

/// <summary>
/// L1 的启动前提（design/06 §6.9.3）：MSBuildLocator 必须在**任何** Microsoft.Build 类型
/// 被 JIT 解析之前注册一次。server 由 <c>dotnet</c> 启动，SDK 必在；求值用的是 SDK 里
/// 那份 Microsoft.Build.dll，编译期契约锁 17.11.4（最老支持 SDK），版本倒挂靠
/// <c>ExcludeAssets=runtime</c> 规避（R1，2026-11-11 spike 实测通过）。
///
/// <para><c>ExcludeAssets=runtime</c> 意味着输出目录里<strong>没有</strong>
/// Microsoft.Build.dll，全靠 Locator 注册的解析回调从 SDK 目录加载。CLR 在 JIT 编译
/// 一个方法时就会解析该方法体内用到的类型标记，所以注册必须发生在**任何引用
/// Microsoft.Build 类型的方法被 JIT 之前**：调用点放在单独的方法（构造函数）里，
/// 且 <see cref="Ensure"/> 自身禁止内联——否则它会被内联进 <c>Discover()</c>，
/// 解析仍发生在注册之前，启动时直接 FileNotFoundException。</para>
///
/// <para>兜底：<c>RegisterDefaults()</c> 依赖 Locator 自己的实例枚举。在"只装了较新的
/// .NET SDK、没装 Visual Studio"的机器上，旧版 Locator 可能一个实例都枚举不到
/// （本机 1.11.2 + SDK 11.0.100 实测 <c>QueryVisualStudioInstances() == 0</c>），
/// 于是抛 <c>No instances of MSBuild could be detected</c>，server 启动即崩。
/// 这时改为从 dotnet 安装位置挑一个 SDK 目录，用 <c>RegisterMSBuildPath</c> 显式注册，
/// 绕开实例枚举。找不到 SDK 时仍抛原异常，不静默吞掉。</para>
/// </summary>
public static class MsBuildEnvironment
{
    private static readonly object Gate = new();
    private static bool _registered;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ensure(Action<string>? log = null)
    {
        lock (Gate)
        {
            if (_registered) return;
            if (MSBuildLocator.IsRegistered)
            {
                _registered = true;
                return;
            }

            try
            {
                MSBuildLocator.RegisterDefaults();
            }
            catch (InvalidOperationException)
            {
                var sdkPath = FindLatestSdkPath();
                if (sdkPath is null) throw;
                MSBuildLocator.RegisterMSBuildPath(sdkPath);
                log?.Invoke($"msbuild: registered explicit SDK path {sdkPath}");
            }
            _registered = true;
        }
    }

    /// <summary>从 dotnet 安装位置里挑 MSBuild.dll 所在的最新稳定 SDK 目录。</summary>
    private static string? FindLatestSdkPath()
    {
        foreach (var sdkRoot in CandidateSdkRoots())
        {
            if (!Directory.Exists(sdkRoot)) continue;

            var best = Directory.EnumerateDirectories(sdkRoot)
                .Select(directory =>
                {
                    var name = Path.GetFileName(directory);
                    var dash = name.IndexOf('-');
                    Version.TryParse(dash < 0 ? name : name[..dash], out var version);
                    return (
                        Path: directory,
                        Name: name,
                        Version: version,
                        Stable: dash < 0,
                        // SDK 的 MSBuild 是给它自己的运行时编译的：net10 进程加载 SDK 11 的
                        // MSBuild 会报 "Could not load System.Runtime, Version=11.0.0.0"。
                        // 优先选与当前运行时同主版本的 SDK；server 配了 LatestMajor 前滚，
                        // 只有新 SDK 时进程本身也跑在新运行时上，二者仍然一致。
                        MatchesRuntime: version?.Major == Environment.Version.Major);
                })
                .Where(candidate => candidate.Version is not null)
                .OrderByDescending(candidate => candidate.MatchesRuntime)
                .ThenByDescending(candidate => candidate.Stable)
                .ThenByDescending(candidate => candidate.Version)
                .ThenByDescending(candidate => candidate.Name, StringComparer.Ordinal)
                .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate.Path, "MSBuild.dll")));

            if (best.Path is not null) return best.Path;
        }
        return null;
    }

    private static IEnumerable<string> CandidateSdkRoots()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host))
        {
            var hostDirectory = Path.GetDirectoryName(host);
            if (!string.IsNullOrEmpty(hostDirectory)) yield return Path.Combine(hostDirectory, "sdk");
        }

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot)) yield return Path.Combine(dotnetRoot, "sdk");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles)) yield return Path.Combine(programFiles, "dotnet", "sdk");

        foreach (var root in ListSdkRootsFromCli()) yield return root;
    }

    /// <summary>最后一招：解析 <c>dotnet --list-sdks</c> 的 "版本 [sdk 根目录]" 输出。</summary>
    private static IReadOnlyList<string> ListSdkRootsFromCli()
    {
        var roots = new List<string>();
        try
        {
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            var startInfo = new ProcessStartInfo(
                string.IsNullOrEmpty(host) ? "dotnet" : host, "--list-sdks")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null) return roots;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            foreach (var line in output.Split('\n'))
            {
                var open = line.LastIndexOf('[');
                var close = line.LastIndexOf(']');
                if (open < 0 || close <= open) continue;
                var root = line[(open + 1)..close].Trim();
                if (Directory.Exists(root)) roots.Add(root);
            }
        }
        catch (Exception)
        {
            // dotnet 不在 PATH / 启动失败：让调用方继续尝试其它候选，或最终抛原异常。
        }
        return roots;
    }
}
