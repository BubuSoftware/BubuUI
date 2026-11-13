using System;
using System.Collections.Generic;
using System.Linq;

namespace EclipseUI;

/// <summary>
/// Shared initialization, update scheduling and resources for UI-authored and code-authored objects.
/// Children and update ancestry are supplied by the real content tree; no component ownership tree exists.
/// </summary>
public abstract class UiObject : IDisposable
{
    private const int MaxUpdateRounds = 100;
    private bool _initializing;
    private volatile bool _initialized;
    private volatile bool _disposed;
    private Exception? _initializationFailure;
    private List<IDisposable>? _resources;
    private UpdateQueue? _updates;
    // Published only while mutating content on the UI thread. Background marking never reads the tree.
    private UiObject? _publishedRoot;
    private Element? _singleContent;
    private UiSynchronizationContext? _uiContext;

    public bool IsInitialized => _initialized;
    public bool IsDisposed => _disposed;

    /// <summary>Initializes once, after caller-supplied property values have been assigned.</summary>
    public void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized || _initializing) return;
        if (_initializationFailure is not null)
            throw new InvalidOperationException($"Initialization of '{GetType().FullName}' previously failed.", _initializationFailure);
        _initializing = true;
        try
        {
            InitializeUi();
            ObjectDisposedException.ThrowIf(_disposed, this);
            _initialized = true;
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
            if (value is not null)
            {
                if (value.Parent is not null || value._contentOwner is not null)
                    throw new InvalidOperationException("Content already belongs to another UI tree.");
                for (UiObject? ancestor = this; ancestor is not null; ancestor = ancestor.UpdateParent)
                    if (ReferenceEquals(ancestor, value))
                        throw new InvalidOperationException("UI content cannot contain itself or an ancestor.");
                value.EnsureInitialized();
            }
            if (_singleContent is { } previous)
            {
                previous._contentOwner = null;
                previous.ReconnectUpdates(SchedulingRoot);
            }
            _singleContent = value;
            if (value is not null)
            {
                value._contentOwner = this;
                value.ReconnectUpdates();
            }
        }
    }

    internal void DetachContent(Element child)
    {
        if (ReferenceEquals(_singleContent, child)) SingleContent = null;
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
        if (_disposed) return false;
        EnsureInitialized();
        var root = UpdateRoot;
        if (!ReferenceEquals(root, this)) return root.ProcessInvalidations();
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
                if (!node.ShouldRender()) continue;
                UiThreadGuard.AssertUiThread(node, "UiObject.Render");
                node.Render();
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
        _disposed = true;
        _updates?.Clear();
        List<Exception>? failures = null;
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
