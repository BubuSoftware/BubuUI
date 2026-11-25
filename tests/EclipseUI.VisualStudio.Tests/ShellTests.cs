using System.Runtime.InteropServices;
using EclipseUI.VisualStudio;
using Nerdbank.Streams;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;

namespace EclipseUI.VisualStudio.Tests;

public sealed class ShellTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EclipseUI VS shell 中文 " + Guid.NewGuid().ToString("N"));

    public ShellTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("")]
    [InlineData("normal")]
    [InlineData("D:\\workspace with spaces\\中文\\")]
    [InlineData("a\"b")]
    [InlineData("before\\\"after")]
    [InlineData("\\\\host\\share name\\")]
    public void CommandLine_RoundTripsThroughWindowsParser(string value)
    {
        if (!OperatingSystem.IsWindows()) return;
        var args = ParseCommandLine("server.exe " + ServerLaunchSettings.QuoteArgument(value));
        Assert.Equal(new[] { "server.exe", value }, args);
    }

    [Fact]
    public void StartInfo_PreservesPathsAndRepeatedAssemblyArguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        var server = Path.Combine(_root, "server folder", "LanguageServer.dll");
        var log = Path.Combine(_root, "log file.log");
        var runtime = Path.Combine(_root, "Runtime.dll");
        var first = Path.Combine(_root, "one.dll");
        var second = Path.Combine(_root, "two.dll");
        var start = ServerLaunchSettings.CreateStartInfo(server, _root, log, "C:\\dotnet folder\\dotnet.exe", runtime, first + "; ;" + second);
        Assert.Equal(new[] { "dotnet", server, "--stdio", "--log", log, "--log-stderr", "--workspace", _root,
            "--runtime", runtime, "--assembly", first, "--assembly", second }, ParseCommandLine("dotnet " + start.Arguments));
        Assert.Equal("C:\\dotnet folder\\dotnet.exe", start.FileName);
        Assert.Equal(_root, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError && start.CreateNoWindow);
    }

    [Fact]
    public void ResolveServer_ExplicitMissingPathDoesNotSilentlyUseBundle()
    {
        CreateFile("extension/server/EclipseUI.LanguageServer.dll");
        Assert.Throws<FileNotFoundException>(() => ServerLaunchSettings.ResolveServer(
            Path.Combine(_root, "extension"), _root, Path.Combine(_root, "missing.dll")));
    }

    [Fact]
    public void ResolveServer_PrefersConfiguredThenBundledThenRepository()
    {
        var configured = CreateFile("custom/Server.dll");
        var bundled = CreateFile("extension/server/EclipseUI.LanguageServer.dll");
        var development = CreateFile("repo/ide/EclipseUI.LanguageServer/bin/Release/net11.0/EclipseUI.LanguageServer.dll");
        var extension = Path.Combine(_root, "extension");
        var workspace = Path.Combine(_root, "repo", "samples", "app");
        Assert.Equal(configured, ServerLaunchSettings.ResolveServer(extension, workspace, configured));
        Assert.Equal(bundled, ServerLaunchSettings.ResolveServer(extension, workspace, null));
        File.Delete(bundled);
        Assert.Equal(development, ServerLaunchSettings.ResolveServer(extension, workspace, null));
        File.Delete(development);
        Assert.Throws<FileNotFoundException>(() => ServerLaunchSettings.ResolveServer(extension, workspace, null));
    }

    [Fact]
    public async Task Initialize_SuppliesSolutionRootAndPreservesCapabilities()
    {
        var middle = new EclipseUiMiddleLayer { Workspace = _root };
        var request = JObject.Parse("{capabilities:{textDocument:{hover:{dynamicRegistration:false}}}}");
        await middle.HandleRequestAsync("initialize", request, parameters =>
        {
            var root = parameters["rootUri"]!.Value<string>()!;
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root)),
                Path.TrimEndingDirectorySeparator(new Uri(root).LocalPath));
            Assert.Equal(root, parameters["workspaceFolders"]![0]!["uri"]!.Value<string>());
            Assert.False(parameters["capabilities"]!["textDocument"]!["hover"]!["dynamicRegistration"]!.Value<bool>());
            return Task.FromResult<JToken?>(new JObject());
        });
    }

    [Fact]
    public async Task Definitions_MaterializeVirtualDocumentThroughActualJsonRpc()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var client = JsonRpc.Attach(clientStream);
        using var server = JsonRpc.Attach(serverStream, new DecompiledServer());
        var middle = new EclipseUiMiddleLayer { Rpc = client, CacheDirectory = Path.Combine(_root, "cache") };
        var original = "eui-decompiled:/../../../not-a-file.cs?type=Demo";
        var results = new JArray(new JObject
        {
            ["uri"] = original,
            ["range"] = new JObject { ["start"] = new JObject { ["line"] = 3, ["character"] = 4 } },
        }, new JObject { ["uri"] = "file:///C:/real/Control.cs" });
        var response = (await middle.HandleRequestAsync("textDocument/definition", new JObject(), _ => Task.FromResult<JToken?>(results)))!;
        var materialized = new Uri(response[0]!["uri"]!.Value<string>()!).LocalPath;
        Assert.StartsWith(Path.Combine(_root, "cache") + Path.DirectorySeparatorChar, materialized);
        Assert.Equal("// 中文\npublic class Demo {}", File.ReadAllText(materialized));
        Assert.True((File.GetAttributes(materialized) & FileAttributes.ReadOnly) != 0);
        Assert.Equal(3, response[0]!["range"]!["start"]!["line"]!.Value<int>());
        Assert.Equal("file:///C:/real/Control.cs", response[1]!["uri"]!.Value<string>());
    }

    [Fact]
    public void DecompiledCache_VersionsContentAndPreventsUriPathTraversal()
    {
        var first = DecompiledDocumentStore.Materialize(_root, "eui-decompiled:../../evil.cs", "old");
        var again = DecompiledDocumentStore.Materialize(_root, "eui-decompiled:../../evil.cs", "old");
        var updated = DecompiledDocumentStore.Materialize(_root, "eui-decompiled:../../evil.cs", "new");
        Assert.Equal(first, again);
        Assert.NotEqual(first, updated);
        Assert.Equal(_root, Path.GetDirectoryName(new Uri(first).LocalPath));
        Assert.Equal("old", File.ReadAllText(new Uri(first).LocalPath));
        Assert.Equal("new", File.ReadAllText(new Uri(updated).LocalPath));
    }

    [Fact]
    public void WatcherContract_MatchesSharedClient()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "EclipseUI.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "ide", "vscode", "client", "extension.js"));
        var declaration = System.Text.RegularExpressions.Regex.Match(source, @"const WATCH_PATTERNS = \[([\s\S]*?)\];").Groups[1].Value;
        var patterns = System.Text.RegularExpressions.Regex.Matches(declaration, "'([^']+)'").Select(match => match.Groups[1].Value);
        Assert.Equal(patterns, ServerLaunchSettings.WatchPatterns);
    }

    [Fact]
    public void Workspace_UsesSolutionOrRepositoryRootForLooseFiles()
    {
        var solution = CreateFile("repo/Example.slnx");
        var document = CreateFile("repo/samples/app/Counter.ui");
        var repo = Path.Combine(_root, "repo");
        Assert.Equal(repo, ServerLaunchSettings.FindWorkspace(solution, document));
        Assert.Equal(repo, ServerLaunchSettings.FindWorkspace(repo, document));
        Assert.Equal(repo, ServerLaunchSettings.FindWorkspace(null, document));
        Assert.Null(ServerLaunchSettings.FindWorkspace(null, null));
    }

    [Theory]
    [InlineData("app/Control.cs", true)]
    [InlineData("app/Counter.ui", true)]
    [InlineData("app/theme.eustyle", true)]
    [InlineData("app/App.csproj", true)]
    [InlineData("app/Directory.Build.props", true)]
    [InlineData("app/obj/project.assets.json", true)]
    [InlineData("app/project.assets.json", false)]
    [InlineData("app/bin/Runtime.dll", false)]
    [InlineData("app/readme.md", false)]
    public void Watcher_FiltersWorkspaceInputs(string relative, bool expected)
        => Assert.Equal(expected, WorkspaceFileWatcher.IsWatched(Path.Combine(_root, relative)));

    [Fact]
    public async Task Watcher_ReportsRealFileCreationAndBothSidesOfRename()
    {
        var created = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renamed = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var original = Path.Combine(_root, "Original.cs");
        var replacement = Path.Combine(_root, "Replacement.cs");
        using var watcher = new WorkspaceFileWatcher(_root, changes =>
        {
            var batch = changes["changes"]!.ToArray();
            if (batch.Any(change => change["uri"]!.Value<string>() == new Uri(original).AbsoluteUri && change["type"]!.Value<int>() == 1)) created.TrySetResult(changes);
            if (batch.Any(change => change["uri"]!.Value<string>() == new Uri(original).AbsoluteUri && change["type"]!.Value<int>() == 3)
                && batch.Any(change => change["uri"]!.Value<string>() == new Uri(replacement).AbsoluteUri && change["type"]!.Value<int>() == 1)) renamed.TrySetResult(changes);
            return Task.CompletedTask;
        }, errors.Enqueue);
        File.WriteAllText(original, "class Original {}");
        await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
        File.Move(original, replacement);
        await renamed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(errors);
    }

    private string CreateFile(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
        return path;
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    public sealed class DecompiledServer
    {
        [JsonRpcMethod("eclipseui/decompiledContent", UseSingleObjectParameterDeserialization = true)]
        public object Content(JObject request)
        {
            Assert.StartsWith("eui-decompiled:", request["uri"]!.Value<string>());
            return new { content = "// 中文\npublic class Demo {}" };
        }
    }

    private static string[] ParseCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(argv); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
