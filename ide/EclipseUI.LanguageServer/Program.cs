using EclipseUI.LanguageServer;
using EclipseUI.LanguageServer.Analysis;
using EclipseUI.LanguageServer.Protocol;
using EclipseUI.LanguageServer.Server;
using EclipseUI.LanguageServer.Workspace;

var arguments = ServerArguments.Parse(args);

if (arguments.ShowHelp)
{
    Console.WriteLine(ServerArguments.Usage);
    return 0;
}
if (arguments.ShowVersion)
{
    Console.WriteLine(typeof(LanguageServerHost).Assembly.GetName().Version?.ToString() ?? "0.1.0");
    return 0;
}

StreamWriter? logFile = null;
if (arguments.LogPath is { Length: > 0 } logPath)
{
    var directory = Path.GetDirectoryName(Path.GetFullPath(logPath));
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    logFile = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
    {
        AutoFlush = true,
    };
}

void Log(string message)
{
    var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
    logFile?.WriteLine(line);
    if (arguments.LogToStandardError) Console.Error.WriteLine(line);
}

var catalogs = new CatalogProvider(arguments.Catalog);
// §6.9：项目系统驱动的单一 Compilation。没有 csproj / untitled / 内存态分析会自动
// 退回 legacy 的 dll 目录，所以测试和独立打开 .ui 都不会突然失去能力。
var projects = new ProjectWorkspace(arguments.Catalog, Log);
var service = new LanguageService(catalogs, new DocumentStore(), projects);

using var shutdown = new CancellationTokenSource();

var connection = new JsonRpcConnection(Console.OpenStandardInput(), Console.OpenStandardOutput(), Log);
var host = new LanguageServerHost(connection, service, catalogs, Log)
{
    ExitRequested = shutdown.Cancel,
};
connection.RequestHandler = host.HandleRequestAsync;
connection.NotificationHandler = host.HandleNotificationAsync;

// 目录扫描（Roslyn 符号派生）约几十到几百毫秒：放到后台预热，别让第一次补全等它。
// 失败只影响标签表，不影响进程——所以这里吞掉异常，详情在 CatalogProvider.Notes 里。
_ = Task.Run(() =>
{
    try
    {
        var count = catalogs.Catalog.Elements.Count;
        Log($"catalog warmed: {count} tags");
    }
    catch (Exception ex)
    {
        Log($"catalog warm-up failed: {ex}");
    }
});

Log($"server started (pid {Environment.ProcessId})");

try
{
    await connection.RunAsync(shutdown.Token);
}
catch (Exception ex)
{
    Log($"fatal: {ex}");
    logFile?.Dispose();
    return 1;
}

Log($"server exiting (code {host.ExitCode})");
logFile?.Dispose();
return host.ExitCode;
