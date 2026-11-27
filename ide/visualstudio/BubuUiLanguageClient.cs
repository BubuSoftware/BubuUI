using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using StreamJsonRpc;
using Task = System.Threading.Tasks.Task;

namespace BubuUI.VisualStudio;

[Export(typeof(ILanguageClient))]
[ContentType(ContentTypes.Template)]
[ContentType(ContentTypes.Stylesheet)]
[RunOnContext(RunningContext.RunOnHost)]
public sealed class BubuUiLanguageClient : ILanguageClient, ILanguageClientCustomMessage2, IDisposable
{
    private readonly BubuUiMiddleLayer _middleLayer = new();
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private System.Diagnostics.Process? _process;
    private WorkspaceFileWatcher? _watcher;
    private SolutionEvents? _solutionEvents;
    private string? _logPath;
    private bool _disposed;

    [Import(typeof(SVsServiceProvider))]
    internal IServiceProvider ServiceProvider { get; set; } = null!;

    public string Name => "BubuUI";
    public IEnumerable<string> ConfigurationSections => Array.Empty<string>();
    public object? InitializationOptions => null;
    public IEnumerable<string> FilesToWatch => ServerLaunchSettings.WatchPatterns;
    public bool ShowNotificationOnInitializeFailed => true;
    public object MiddleLayer => _middleLayer;
    public object? CustomMessageTarget => null;
    public event AsyncEventHandler<EventArgs>? StartAsync;
    public event AsyncEventHandler<EventArgs>? StopAsync;

    public async Task OnLoadedAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (_solutionEvents == null && ServiceProvider.GetService(typeof(DTE)) is DTE dte)
        {
            _solutionEvents = dte.Events.SolutionEvents;
            _solutionEvents.Opened += OnSolutionOpened;
            _solutionEvents.AfterClosing += OnSolutionOpened;
        }
        if (StartAsync != null) await StartAsync.InvokeAsync(this, EventArgs.Empty);
    }

    public async Task<Connection?> ActivateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
        string? workspace = null;
        if (ServiceProvider.GetService(typeof(DTE)) is DTE dte)
        {
            var solution = dte.Solution?.FullName;
            var document = dte.ActiveDocument?.FullName;
            workspace = ServerLaunchSettings.FindWorkspace(solution, document);
        }
        _middleLayer.Workspace = workspace;
        // Process startup and filesystem probing must not block the editor thread.
        await TaskScheduler.Default;
        StopProcess();
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var server = ServerLaunchSettings.ResolveServer(directory, workspace,
            Environment.GetEnvironmentVariable("BUBUUI_SERVER_DLL"));
        var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BubuUI", "VisualStudio", "logs");
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, "server-" + Guid.NewGuid().ToString("N") + ".log");
        var process = new System.Diagnostics.Process
        {
            StartInfo = ServerLaunchSettings.CreateStartInfo(server, workspace, _logPath,
                Environment.GetEnvironmentVariable("BUBUUI_DOTNET"),
                Environment.GetEnvironmentVariable("BUBUUI_RUNTIME"),
                Environment.GetEnvironmentVariable("BUBUUI_CATALOG_ASSEMBLIES")),
        };
        // Drain stderr: a full pipe otherwise freezes the language server. The server also writes its log file.
        process.ErrorDataReceived += (_, args) => { if (args.Data != null) Trace.WriteLine(args.Data, "BubuUI"); };
        try
        {
            token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("Could not start the BubuUI language server.");
            _process = process;
            process.BeginErrorReadLine();
            token.ThrowIfCancellationRequested();
            return new Connection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        }
        catch
        {
            StopProcess();
            process.Dispose();
            throw;
        }
    }

    public Task AttachForCustomMessageAsync(JsonRpc rpc)
    {
        _middleLayer.Rpc = rpc;
        var process = _process;
        rpc.Disconnected += (_, _) =>
        {
            if (ReferenceEquals(_process, process)) StopProcess();
        };
        return Task.CompletedTask;
    }

    public Task OnServerInitializedAsync()
    {
        var rpc = _middleLayer.Rpc;
        var workspace = _middleLayer.Workspace;
        if (rpc != null && workspace != null && Directory.Exists(workspace))
        {
            try
            {
                _watcher = new WorkspaceFileWatcher(workspace,
                    changes => rpc.NotifyWithParameterObjectAsync("workspace/didChangeWatchedFiles", changes),
                    exception => Trace.WriteLine(exception, "BubuUI watcher"));
            }
            catch (IOException exception) { Trace.WriteLine(exception, "BubuUI watcher"); }
            catch (UnauthorizedAccessException exception) { Trace.WriteLine(exception, "BubuUI watcher"); }
        }
        return Task.CompletedTask;
    }

    public Task<InitializationFailureContext?> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
    {
        StopProcess();
        return Task.FromResult<InitializationFailureContext?>(new InitializationFailureContext
        {
            FailureMessage = "BubuUI language server failed to initialize. Install .NET SDK 11 or later " +
                "and check BUBUUI_SERVER_DLL / BUBUUI_DOTNET if configured.\nLog: " + _logPath +
                "\n" + initializationState.InitializationException?.Message,
        });
    }

    private void OnSolutionOpened()
    {
        // The DTE event cannot return a Task; FileAndForget reports failures through VS telemetry.
#pragma warning disable VSSDK007
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await _restartGate.WaitAsync();
            try
            {
                if (_disposed) return;
                if (StopAsync != null) await StopAsync.InvokeAsync(this, EventArgs.Empty);
                StopProcess();
                if (StartAsync != null) await StartAsync.InvokeAsync(this, EventArgs.Empty);
            }
            finally { _restartGate.Release(); }
        }).FileAndForget("BubuUI/RestartLanguageServer");
#pragma warning restore VSSDK007
    }

    private void StopProcess()
    {
        Interlocked.Exchange(ref _watcher, null)?.Dispose();
        var process = Interlocked.Exchange(ref _process, null);
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    public void Dispose()
    {
        _disposed = true;
        StopProcess();
        var solutionEvents = _solutionEvents;
        if (solutionEvents != null)
        {
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                solutionEvents.Opened -= OnSolutionOpened;
                solutionEvents.AfterClosing -= OnSolutionOpened;
                _solutionEvents = null;
            }).FileAndForget("BubuUI/DisposeLanguageClient");
#pragma warning restore VSSDK007
        }
        GC.SuppressFinalize(this);
    }
}
