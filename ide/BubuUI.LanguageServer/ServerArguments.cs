using BubuUI.LanguageServer.Workspace;

namespace BubuUI.LanguageServer;

/// <summary>
/// 启动参数。设计约束：**不带参数也要能跑**——开发机上打开仓库就有效果，
/// 靠 <see cref="CatalogProvider"/> 自己去找 <c>src/BubuUI.Runtime/bin/**</c>。
/// 参数只为两种场景存在：发布版随包带 runtime，以及显式指定用户程序集。
/// </summary>
public sealed class ServerArguments
{
    public CatalogOptions Catalog { get; init; } = new();

    /// <summary>诊断日志输出文件（LSP 的 stdout 是协议通道，日志只能走文件/stderr）。</summary>
    public string? LogPath { get; init; }

    public bool LogToStandardError { get; init; }

    public bool ShowHelp { get; init; }

    public bool ShowVersion { get; init; }
    public bool MaterializeDecompiled { get; init; }

    public static ServerArguments Parse(string[] args)
    {
        string? runtime = null;
        string? workspace = null;
        string? log = null;
        var assemblies = new List<string>();
        var includeSiblings = true;
        var help = false;
        var version = false;
        var logToStderr = false;
        var materializeDecompiled = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--stdio":
                    break; // 默认就是 stdio；保留是为了兼容编辑器侧的通用启动模板
                case "--materialize-decompiled":
                    materializeDecompiled = true;
                    break;
                case "--runtime":
                case "-r":
                    runtime = Next(args, ref i);
                    break;
                case "--assembly":
                case "-a":
                    var assembly = Next(args, ref i);
                    if (assembly is not null) assemblies.Add(assembly);
                    break;
                case "--workspace":
                case "-w":
                    workspace = Next(args, ref i);
                    break;
                case "--log":
                    log = Next(args, ref i);
                    break;
                case "--log-stderr":
                    logToStderr = true;
                    break;
                case "--no-sibling-assemblies":
                    includeSiblings = false;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                case "--version":
                case "-v":
                    version = true;
                    break;
            }
        }

        return new ServerArguments
        {
            Catalog = new CatalogOptions
            {
                RuntimePath = runtime,
                ProbeRoot = workspace,
                Assemblies = assemblies,
                IncludeSiblingAssemblies = includeSiblings,
            },
            LogPath = log,
            LogToStandardError = logToStderr,
            ShowHelp = help,
            ShowVersion = version,
            MaterializeDecompiled = materializeDecompiled,
        };
    }

    private static string? Next(string[] args, ref int index) =>
        index + 1 < args.Length ? args[++index] : null;

    public const string Usage = """
        BubuUI language server (stdio LSP)

        usage: BubuUI.LanguageServer [options]

          --stdio                      talk LSP over stdin/stdout (default)
          --materialize-decompiled     use cached .cs files for metadata navigation (Rider)
          --runtime, -r <path>         BubuUI.Runtime.dll or the directory containing it
          --assembly, -a <path>        extra assembly to scan for components (repeatable)
          --workspace, -w <path>       start probing for the repository layout here
          --no-sibling-assemblies      do not scan assemblies next to BubuUI.Runtime.dll
          --log <path>                 append protocol/diagnostic log to a file
          --log-stderr                 mirror the log to stderr
          --version, -v                print the server version
          --help, -h                   print this help
        """;
}
