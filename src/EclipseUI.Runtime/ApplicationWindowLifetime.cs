using Microsoft.Extensions.DependencyInjection;

namespace EclipseUI;

/// <summary>Creates a platform window and runs its message loop until that window closes.</summary>
public interface IWindowLifetime
{
    /// <summary>
    /// Runs on the UI thread. A modal window temporarily disables its current owner;
    /// closing the owner does not close this window. Resources are released before returning.
    /// </summary>
    int RunWindow(Application app, Window window, bool modal = false);
}

public abstract partial class Application
{
    private readonly List<ApplicationWindowSession> _windows = [];
    private int? _windowThread;
    private IWindowHost? _unmanagedHost;
    private IClipboard? _initialClipboard;
    private bool _shutdownRequested;

    /// <summary>Currently open managed windows, in opening order. Access on the UI thread.</summary>
    public IReadOnlyList<Window> Windows
    {
        get
        {
            AssertWindowThread();
            return _windows.Where(session => session.IsOpen).Select(session => session.Window).ToArray();
        }
    }

    /// <summary>
    /// Opens an independent native window and waits for it to close. The current window
    /// is disabled while the dialog is open; closing it leaves the dialog running.
    /// The desktop application exits when the outermost window loop has also finished.
    /// </summary>
    public void ShowDialog(Window window)
    {
        ValidateWindow(window);
        var lifetime = Services.GetService<IWindowLifetime>()
            ?? throw new InvalidOperationException("No IWindowLifetime registered. Add the platform setup first.");
        lifetime.RunWindow(this, window, modal: true);
    }

    /// <summary>Requests closure of all open windows. Call on the UI thread.</summary>
    public void Shutdown()
    {
        AssertWindowThread();
        _shutdownRequested = true;
        List<Exception>? failures = null;
        foreach (var session in _windows.ToArray().Reverse())
        {
            if (!session.IsOpen) continue;
            try { session.Close(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException("Application shutdown failed.", failures);
    }

    internal ApplicationWindowSession AttachWindow(Window window, IWindowHost host,
        Action close, IClipboard clipboard, bool modal)
    {
        ValidateWindow(window);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(clipboard);
        if (host.IsClosed) throw new InvalidOperationException("Cannot attach a closed window host.");
        if (_windows.Any(entry => ReferenceEquals(entry.Host, host)))
            throw new InvalidOperationException("This host already has a running window lifetime.");

        var owner = _windows.LastOrDefault(entry => entry.IsOpen);
        var session = new ApplicationWindowSession(this, window, host, close, clipboard, owner, modal);
        if (_windows.Count == 0)
        {
            _windowThread = Environment.CurrentManagedThreadId;
            _unmanagedHost = Host is { IsClosed: false } ? Host : null;
            _initialClipboard = Clipboard.Current;
        }
        _windows.Add(session);
        try
        {
            session.DisableOwner();
            Host = host;
            Clipboard.Current = clipboard;
            return session;
        }
        catch (Exception error)
        {
            try { session.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Window registration and cleanup failed.", error, cleanup); }
            throw;
        }
    }

    // Platform factories call this before taking ownership, so a rejected request
    // cannot dispose an already-running window in their resource cleanup path.
    internal void ValidateWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        AssertWindowThread();
        ObjectDisposedException.ThrowIf(window.IsDisposed, window);
        if (_shutdownRequested) throw new InvalidOperationException("The application is shutting down.");
        if (_windows.Any(entry => ReferenceEquals(entry.Window, window)))
            throw new InvalidOperationException("This window already has a running lifetime.");
    }

    internal void DetachWindow(ApplicationWindowSession session)
    {
        AssertWindowThread();
        _windows.Remove(session);
        // An intermediate window may already have closed while a deeper dialog stayed open.
        var current = _windows.LastOrDefault(candidate => candidate.IsOpen);
        Host = current?.Host ?? (_unmanagedHost is { IsClosed: false } ? _unmanagedHost : null);
        Clipboard.Current = current?.Clipboard ?? _initialClipboard ?? Clipboard.Current;
        if (_windows.Count == 0)
        {
            _windowThread = null;
            _unmanagedHost = null;
            _initialClipboard = null;
        }
    }

    private void AssertWindowThread()
    {
        if (_windowThread is { } thread && thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Application window operations must run on their UI thread.");
    }
}

/// <summary>Shared bookkeeping; platform lifetimes own UI, hosts, surfaces and native resources.</summary>
internal sealed class ApplicationWindowSession : IDisposable
{
    private readonly Application _application;
    private readonly ApplicationWindowSession? _owner;
    private readonly Element? _ownerContent;
    private readonly Element? _ownerFocus;
    private readonly bool _ownerEnabled;
    private readonly bool _modal;
    private bool _ownerDisabled, _disposed;

    internal ApplicationWindowSession(Application application, Window window, IWindowHost host,
        Action close, IClipboard clipboard, ApplicationWindowSession? owner, bool modal)
    {
        _application = application;
        Window = window;
        Host = host;
        Close = close;
        Clipboard = clipboard;
        _owner = owner;
        _modal = modal;
        _ownerContent = owner?.Window.Content;
        _ownerEnabled = _ownerContent?.IsEnabled ?? true;
        _ownerFocus = _ownerContent is null ? null : FocusManager.GetFor(_ownerContent)?.Focused;
    }

    internal Window Window { get; }
    internal IWindowHost Host { get; }
    internal Action Close { get; }
    internal IClipboard Clipboard { get; }
    internal bool IsOpen => !_disposed && !Window.IsDisposed && !Host.IsClosed;

    internal void DisableOwner()
    {
        if (!_modal || _ownerContent is null) return;
        _ownerDisabled = true;
        _ownerContent.IsEnabled = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Detach first also enforces thread affinity without leaving a half-disposed registration.
        _application.DetachWindow(this);
        _disposed = true;
        if (!_ownerDisabled || _owner?.IsOpen != true || _ownerContent is not { IsDisposed: false }) return;
        _ownerContent.IsEnabled = _ownerEnabled;
        if (_ownerEnabled && _ownerFocus is { IsDisposed: false, IsAttached: true })
            _ownerFocus.RequestFocus(FocusOrigin.Restore);
    }
}
