using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace EclipseUI.VisualStudio;

// VS's FilesToWatch is tied to open-folder workspaces. Keep solution mode current too.
internal sealed class WorkspaceFileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<JObject, Task> _notify;
    private readonly Action<Exception> _onError;
    private bool _disposed;

    internal WorkspaceFileWatcher(string workspace, Func<JObject, Task> notify, Action<Exception> onError)
    {
        _notify = notify;
        _onError = onError;
        _timer = new Timer(Flush, null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(workspace)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, args) => Queue(args.FullPath, 1);
        _watcher.Changed += (_, args) => Queue(args.FullPath, 2);
        _watcher.Deleted += (_, args) => Queue(args.FullPath, 3);
        _watcher.Renamed += (_, args) => { Queue(args.OldFullPath, 3); Queue(args.FullPath, 1); };
        // A synthetic structural change forces a full workspace reevaluation after overflow.
        _watcher.Error += (_, args) => { _onError(args.GetException()); Queue(Path.Combine(workspace, "Directory.Build.props"), 2); };
        _watcher.EnableRaisingEvents = true;
    }

    private void Queue(string path, int kind)
    {
        if (!IsWatched(path)) return;
        lock (_gate)
        {
            if (_disposed) return;
            // Preserve creation when the write that created it also raises Changed.
            if (!_pending.TryGetValue(path, out var previous) || previous != 1 || kind != 2)
                _pending[path] = kind;
            _timer.Change(150, Timeout.Infinite);
        }
    }

    internal static bool IsWatched(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ui", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".eustyle", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(path).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || (Path.GetFileName(path).Equals("project.assets.json", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "obj", StringComparison.OrdinalIgnoreCase));
    }

    private void Flush(object? state) => _ = FlushAsync();

    private async Task FlushAsync()
    {
        try
        {
            var changes = new JArray();
            lock (_gate)
            {
                if (_disposed || _pending.Count == 0) return;
                foreach (var change in _pending)
                    changes.Add(new JObject { ["uri"] = new Uri(change.Key).AbsoluteUri, ["type"] = change.Value });
                _pending.Clear();
            }
            await _notify(new JObject { ["changes"] = changes }).ConfigureAwait(false);
        }
        catch (Exception exception) { _onError(exception); }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _pending.Clear(); }
        _timer.Dispose();
        _watcher.Dispose();
    }
}
