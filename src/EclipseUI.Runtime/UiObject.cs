using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EclipseUI;

/// <summary>
/// Shared initialization, update scheduling and resources for UI-authored and code-authored objects.
/// Children and update ancestry are supplied by the real content tree; no component ownership tree exists.
/// </summary>
public abstract class UiObject : IDisposable
{
    private const int MaxUpdateRounds = 100;
    private bool _initializing;
    private bool _processingUpdates;
    private volatile bool _initialized;
    private volatile bool _disposed;
    private Exception? _initializationFailure;
    private List<IDisposable>? _resources;
    private UpdateQueue? _updates;
    // Published only while mutating content on the UI thread. Background marking never reads the tree.
    private UiObject? _publishedRoot;
    private Element? _singleContent;
    private UiSynchronizationContext? _uiContext;
    private object? _attachedHost;
    private bool _attachmentNotified;
    private bool _detachmentPending;
    private long _attachmentVersion;

    public bool IsInitialized => _initialized;
    public bool IsDisposed => _disposed;
    /// <summary>Whether this object belongs to an active host tree, independently of its content parent.</summary>
    public bool IsAttached => _attachedHost is not null;

    /// <summary>Initializes once, after caller-supplied property values have been assigned.</summary>
    public void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized || _initializing) return;
        UiThreadGuard.AssertUiThread(this, nameof(EnsureInitialized));
        if (_initializationFailure is not null)
            throw new InvalidOperationException($"Initialization of '{GetType().FullName}' previously failed.", _initializationFailure);
        _initializing = true;
        try
        {
            InitializeUi();
            ObjectDisposedException.ThrowIf(_disposed, this);
            _initialized = true;
            OnInitialized();
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        catch (Exception error)
        {
            _initializationFailure = error;
            try { Dispose(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("UI initialization and resource cleanup failed.", error, cleanupError);
            }
            throw;
        }
        finally { _initializing = false; }
    }

    /// <summary>Generated overrides set constructor inputs, initialize the base, then populate the root instance.</summary>
    protected virtual void InitializeUi() { }
    protected virtual void Render() { }
    protected virtual bool ShouldRender() => true;

    /// <summary>Runs once after the complete UI and its initial values are available, before host attachment.</summary>
    protected virtual void OnInitialized() { }
    /// <summary>Runs on entering an active host tree; ancestors are notified before descendants.</summary>
    protected virtual void OnAttached() { }
    /// <summary>Runs on leaving an active host tree; descendants are notified before ancestors.</summary>
    protected virtual void OnDetached() { }
    /// <summary>Runs before an accepted dynamic update. Initial construction is not an update.</summary>
    protected virtual void OnUpdating() { }
    /// <summary>Runs after a successful dynamic update.</summary>
    protected virtual void OnUpdated() { }

    internal virtual UiObject? UpdateParent => null;
    protected internal virtual IEnumerable<UiObject> UiChildren
    {
        get { if (_singleContent is not null) yield return _singleContent; }
    }

    internal Element? ContentElement => _singleContent;

    /// <summary>Shared storage and ownership service for a declared single-element content property.</summary>
    protected Element? SingleContent
    {
        get => _singleContent;
        set
        {
            if (ReferenceEquals(_singleContent, value)) return;
            ObjectDisposedException.ThrowIf(_disposed, this);
            UiThreadGuard.AssertUiThread(this, nameof(SingleContent));
            if (value is not null)
            {
                ValidateContent(value);
                value.EnsureInitialized();
                ObjectDisposedException.ThrowIf(_disposed, this);
                ValidateContent(value);
            }
            var previousRoot = SchedulingRoot;
            var previous = _singleContent;
            _singleContent = value;
            if (previous is not null)
            {
                previous._contentOwner = null;
                previous.ReconnectUpdates(previousRoot);
            }
            if (value is not null)
            {
                value._contentOwner = this;
                value.ReconnectUpdates();
            }
            List<Exception>? failures = null;
            try { previous?.DetachFromTree(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
            try { value?.AttachToParent(this); }
            catch (Exception error) { (failures ??= new()).Add(error); }
            if (failures is not null) throw new AggregateException("Content lifecycle notification failed.", failures);
        }
    }

    private void ValidateContent(Element value)
    {
        value.ValidateCanAttach();
        if (value.UpdateParent is not null)
            throw new InvalidOperationException("Content already belongs to another UI tree.");
        for (UiObject? ancestor = this; ancestor is not null; ancestor = ancestor.UpdateParent)
            if (ReferenceEquals(ancestor, value))
                throw new InvalidOperationException("UI content cannot contain itself or an ancestor.");
    }

    internal void DetachContent(Element child)
    {
        if (!ReferenceEquals(_singleContent, child)) return;
        // Child disposal must also unlink content while its owner is already disposing.
        var previousRoot = SchedulingRoot;
        _singleContent = null;
        child._contentOwner = null;
        child.ReconnectUpdates(previousRoot);
        child.DetachFromTree();
    }

    internal virtual void RemoveFromParent() { }

    internal void ValidateCanAttach()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_detachmentPending)
            throw new InvalidOperationException("An object awaiting OnDetached cannot be reattached.");
        if (IsAttached)
            throw new InvalidOperationException("The object already belongs to an active host tree.");
    }

    // Host tokens prevent a stale host from detaching a tree which has since moved elsewhere.
    internal void AttachToHost(object host)
    {
        ArgumentNullException.ThrowIfNull(host);
        ValidateHostRoot();
        EnsureInitialized();
        ValidateHostRoot();
        try
        {
            ChangeAttachment(host);
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        catch (Exception original)
        {
            try { DetachFromHost(host); }
            catch (Exception cleanup)
            {
                throw new AggregateException("Host attachment and rollback failed.", original, cleanup);
            }
            throw;
        }
    }

    private void ValidateHostRoot()
    {
        ValidateCanAttach();
        if (UpdateParent is not null)
            throw new InvalidOperationException("Only a root without a content parent can be attached to a host.");
    }

    internal void DetachFromHost(object host)
    {
        if (ReferenceEquals(_attachedHost, host)) DetachFromTree();
    }

    internal void AttachToParent(UiObject parent)
    {
        // An earlier notification may have removed, moved or disposed this node.
        if (_disposed || parent._disposed || !ReferenceEquals(UpdateParent, parent)) return;
        if (!ReferenceEquals(_attachedHost, parent._attachedHost)) ChangeAttachment(parent._attachedHost);
    }

    internal void DetachFromTree()
    {
        if (IsAttached) ChangeAttachment(null);
    }

    private void ChangeAttachment(object? host)
    {
        UiThreadGuard.AssertUiThread(this, "UiObject attachment");
        var changes = new List<(UiObject Node, long Version, bool Notify)>();
        Collect(this);

        // Commit the entire subtree before invoking user code. Version checks suppress stale
        // notifications if an earlier hook removes, adds or disposes part of this snapshot.
        foreach (var change in changes)
        {
            var node = change.Node;
            node._attachedHost = host;
            node._attachmentVersion = change.Version;
            node._attachmentNotified = false;
            node._detachmentPending = host is null && change.Notify;
        }

        List<Exception>? failures = null;
        var ordered = host is null ? changes.AsEnumerable().Reverse() : changes;
        foreach (var (node, version, notify) in ordered)
        {
            if (node._attachmentVersion != version) continue;
            node._detachmentPending = false;
            if (!notify) continue;
            if (host is not null)
            {
                if (node._disposed || !ReferenceEquals(node._attachedHost, host)) continue;
                node._attachmentNotified = true;
            }
            try
            {
                if (host is null) node.OnDetached();
                else node.OnAttached();
            }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures is not null) throw new AggregateException("UI lifecycle notification failed.", failures);

        void Collect(UiObject node)
        {
            if (host is not null)
            {
                ObjectDisposedException.ThrowIf(node._disposed, node);
                if (node._detachmentPending)
                    throw new InvalidOperationException("An object awaiting OnDetached cannot be reattached.");
                if (node._attachedHost is not null && !ReferenceEquals(node._attachedHost, host))
                    throw new InvalidOperationException("The object already belongs to another active host tree.");
            }
            if (!ReferenceEquals(node._attachedHost, host))
                changes.Add((node, node._attachmentVersion + 1, host is not null || node._attachmentNotified));
            foreach (var child in node.UiChildren.ToArray()) Collect(child);
        }
    }

    internal void BindUiContext(UiSynchronizationContext context) => _uiContext = context;

    internal void ClearUiContext(UiSynchronizationContext context)
    {
        if (ReferenceEquals(_uiContext, context)) _uiContext = null;
    }

    internal UiSynchronizationContext? FindUiContext()
    {
        for (UiObject? node = this; node is not null; node = node.UpdateParent)
            if (node._uiContext is { } context) return context;
        return null;
    }

    private UiObject UpdateRoot
    {
        get
        {
            var root = this;
            while (root.UpdateParent is { } parent) root = parent;
            return root;
        }
    }

    internal UiObject SchedulingRoot => UpdateRoot;

    private int UpdateDepth
    {
        get
        {
            var depth = 0;
            for (var parent = UpdateParent; parent is not null; parent = parent.UpdateParent) depth++;
            return depth;
        }
    }

    /// <summary>Thread-safe marking only; user code executes when the host drains updates.</summary>
    public void Invalidate()
    {
        if (_disposed || !_initialized) return;
        EnqueueInvalidation();
    }

    /// <summary>
    /// Runs a synchronous state change on the calling thread and invalidates on exit,
    /// including an exceptional exit. Rendering remains deferred to the host's update queue.
    /// </summary>
    public void UpdateState(Action update)
    {
        ArgumentNullException.ThrowIfNull(update);
        try { update(); }
        finally { Invalidate(); }
    }

    /// <summary>
    /// Awaits a state change before invalidating, including failure or cancellation.
    /// Normal await context capture and UI thread rules apply; this does not dispatch work.
    /// </summary>
    public async Task UpdateStateAsync(Func<Task> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        try { await update(); }
        finally { Invalidate(); }
    }

    /// <summary>
    /// Invalidates once when the returned scope is disposed. Nested scopes use the normal
    /// coalescing queue; they do not suspend rendering or provide transactional rollback.
    /// </summary>
    public StateUpdateScope BeginStateUpdate() => new(this);

    public sealed class StateUpdateScope : IDisposable
    {
        private UiObject? _owner;

        internal StateUpdateScope(UiObject owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Invalidate();
    }

    private UiObject PublishedRoot => System.Threading.Volatile.Read(ref _publishedRoot) ?? this;

    private void EnqueueInvalidation()
    {
        var target = PublishedRoot;
        while (true)
        {
            target.GetUpdates().Enqueue(this);
            var current = PublishedRoot;
            if (ReferenceEquals(target, current)) return;
            // A content move may have drained the old queue before this enqueue. Publishing the
            // new target first, then rechecking after enqueue, keeps the work reachable by its host.
            target = current;
        }
    }

    public void InvalidateSubtree()
    {
        if (_disposed) return;
        EnsureInitialized();
        var pending = new Stack<UiObject>();
        pending.Push(this);
        while (pending.TryPop(out var node))
        {
            if (node._disposed) continue;
            node.Invalidate();
            foreach (var child in node.UiChildren) pending.Push(child);
        }
    }

    /// <summary>Drains the real tree's shared update queue, ancestors before descendants.</summary>
    public bool ProcessInvalidations()
    {
        if (_disposed || _initializing) return false;
        EnsureInitialized();
        var root = UpdateRoot;
        if (!ReferenceEquals(root, this)) return root.ProcessInvalidations();
        if (_processingUpdates) return false;
        _processingUpdates = true;
        try { return ProcessUpdates(); }
        finally { _processingUpdates = false; }
    }

    private bool ProcessUpdates()
    {
        var root = this;
        var queue = _updates;
        if (queue is null) return false;
        var didWork = false;
        var rounds = 0;
        while (queue.Drain() is { Count: > 0 } batch)
        {
            if (++rounds > MaxUpdateRounds)
                throw new InvalidOperationException($"UI update loop exceeded {MaxUpdateRounds} rounds at '{batch[0].GetType().FullName}'.");
            foreach (var node in batch.OrderBy(static node => node.UpdateDepth))
            {
                if (node._disposed || !node._initialized) continue;
                if (!ReferenceEquals(node.UpdateRoot, root))
                {
                    node.Invalidate();
                    continue;
                }
                UiThreadGuard.AssertUiThread(node, "UiObject.Render");
                var shouldRender = node.ShouldRender();
                if (node._disposed) continue;
                if (!ReferenceEquals(node.UpdateRoot, root))
                {
                    node.Invalidate();
                    continue;
                }
                if (!shouldRender) continue;
                node.OnUpdating();
                if (node._disposed) continue;
                if (!ReferenceEquals(node.UpdateRoot, root))
                {
                    node.Invalidate();
                    continue;
                }
                node.Render();
                if (!node._disposed) node.OnUpdated();
                didWork = true;
            }
        }
        return didWork;
    }

    /// <summary>Moves pending work when an initialized subtree is connected to another content tree.</summary>
    internal void ReconnectUpdates(UiObject? previousRoot = null)
    {
        var root = UpdateRoot;
        var oldRoots = new HashSet<UiObject>();
        if (previousRoot is not null) oldRoots.Add(previousRoot);
        var pending = new Stack<UiObject>();
        pending.Push(this);
        while (pending.TryPop(out var node))
        {
            oldRoots.Add(node.PublishedRoot);
            System.Threading.Volatile.Write(ref node._publishedRoot, root);
            foreach (var child in node.UiChildren) pending.Push(child);
        }
        // Publish the whole subtree before draining any old queue. Concurrent invalidations either
        // enter the new queue directly or observe the publication after their enqueue and retry.
        foreach (var previous in oldRoots)
        {
            if (ReferenceEquals(previous, root) || previous._updates is not { } queue) continue;
            foreach (var dirty in queue.Drain())
                if (!dirty._disposed) dirty.EnqueueInvalidation();
        }
    }

    protected void Track(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (_disposed) { resource.Dispose(); return; }
        (_resources ??= new()).Add(resource);
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        if (_detachmentPending)
            throw new InvalidOperationException("An object awaiting OnDetached cannot be disposed before its notification.");
        UiThreadGuard.AssertUiThread(this, nameof(Dispose));
        _disposed = true;
        _updates?.Clear();
        List<Exception>? failures = null;
        try { RemoveFromParent(); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        try { DetachFromTree(); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        foreach (var child in UiChildren.ToArray().Reverse())
        {
            try { child.Dispose(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (_resources is { } resources)
        {
            for (var i = resources.Count - 1; i >= 0; i--)
            {
                try { resources[i].Dispose(); }
                catch (Exception error) { (failures ??= new()).Add(error); }
            }
            resources.Clear();
        }
        try { OnDispose(); }
        catch (Exception error) { (failures ??= new()).Add(error); }
        if (failures is not null) throw new AggregateException("UI resource cleanup failed.", failures);
    }

    protected virtual void OnDispose() { }

    /// <summary>Detaches and permanently disposes this object and its content, using the common lifecycle.</summary>
    public void Unmount() => Dispose();

    private UpdateQueue GetUpdates()
    {
        if (_updates is { } queue) return queue;
        return System.Threading.Interlocked.CompareExchange(ref _updates, new UpdateQueue(), null) ?? _updates!;
    }

    private sealed class UpdateQueue
    {
        private readonly object _gate = new();
        private readonly List<UiObject> _pending = new();
        private readonly HashSet<UiObject> _set = new();

        public void Enqueue(UiObject node)
        {
            lock (_gate) { if (_set.Add(node)) _pending.Add(node); }
        }

        public List<UiObject> Drain()
        {
            lock (_gate)
            {
                var batch = new List<UiObject>(_pending);
                _pending.Clear();
                _set.Clear();
                return batch;
            }
        }

        public void Clear()
        {
            lock (_gate) { _pending.Clear(); _set.Clear(); }
        }
    }
}
