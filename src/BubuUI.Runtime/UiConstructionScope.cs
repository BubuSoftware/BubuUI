using System;
using System.Collections.Generic;

namespace BubuUI;

/// <summary>
/// Owns newly created UI objects only until a build completes. A successful build transfers
/// ownership to its real content tree; a failed build releases objects that were not attached yet.
/// </summary>
public sealed class UiConstructionScope : IDisposable
{
    private List<UiObject>? _pending;
    private bool _completed;
    private bool _disposed;

    public T Track<T>(T value) where T : UiObject
    {
        ArgumentNullException.ThrowIfNull(value);
        ObjectDisposedException.ThrowIf(_disposed || _completed, this);
        (_pending ??= new()).Add(value);
        return value;
    }

    /// <summary>Releases temporary references without disposing the completed content tree.</summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) return;
        _completed = true;
        _pending?.Clear();
        _pending = null;
    }

    /// <summary>Preserves the build failure if cleanup also fails; callers rethrow after a successful cleanup.</summary>
    public void Abort(Exception original)
    {
        ArgumentNullException.ThrowIfNull(original);
        try { Dispose(); }
        catch (Exception cleanup)
        {
            var failures = new List<Exception> { original };
            if (cleanup is AggregateException aggregate)
                failures.AddRange(aggregate.Flatten().InnerExceptions);
            else failures.Add(cleanup);
            throw new AggregateException("UI construction and cleanup failed.", failures);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var pending = _pending;
        _pending = null;
        if (_completed || pending is null) return;

        List<Exception>? failures = null;
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            try { pending[i].Dispose(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        pending.Clear();
        if (failures is not null)
            throw new AggregateException("UI construction cleanup failed.", failures);
    }
}
