using System.Diagnostics;

namespace BubuUI;

/// <summary>Cooperative frame dispatch for every running window on one UI thread.</summary>
internal sealed class WindowFrameScheduler : IDisposable
{
    [ThreadStatic] private static List<WindowFrameScheduler>? _windows;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly Action _tick;
    private readonly SynchronizationContext _context;
    private readonly IClipboard _clipboard;
    private readonly long _interval;
    private long _nextFrame;
    private bool _disposed;

    internal WindowFrameScheduler(Action tick, SynchronizationContext context,
        IClipboard clipboard, double maxFramesPerSecond, int idleMilliseconds)
    {
        _tick = tick;
        _context = context;
        _clipboard = clipboard;
        _interval = Math.Max(1, (long)(Stopwatch.Frequency *
            (maxFramesPerSecond > 0 ? 1 / maxFramesPerSecond : Math.Max(1, idleMilliseconds) / 1000.0)));
        (_windows ??= []).Add(this);
    }

    internal void PumpOthers()
    {
        VerifyAccess();
        if (_windows!.Count <= 1) return;
        foreach (var window in _windows!.ToArray())
        {
            if (ReferenceEquals(window, this) || window._disposed) continue;
            var now = Stopwatch.GetTimestamp();
            if (now < window._nextFrame) continue;
            // Mark before dispatch: a callback can itself open another nested loop.
            // Such a loop must still service this window's newly posted work.
            window._nextFrame = now + window._interval;
            var previousContext = SynchronizationContext.Current;
            var previousClipboard = Clipboard.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(window._context);
                Clipboard.Current = window._clipboard;
                window._tick();
            }
            finally
            {
                Clipboard.Current = previousClipboard;
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

    internal void MarkFrame()
    {
        VerifyAccess();
        _nextFrame = Stopwatch.GetTimestamp() + _interval;
    }

    // Pacing a slow/idle child must not limit an animated owner's frame rate.
    internal void Wait(double milliseconds)
    {
        VerifyAccess();
        var deadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0, milliseconds) * Stopwatch.Frequency / 1000);
        while (!_disposed)
        {
            PumpOthers();
            var now = Stopwatch.GetTimestamp();
            if (now >= deadline) break;
            var wake = deadline;
            foreach (var window in _windows!)
                if (!ReferenceEquals(window, this) && !window._disposed)
                    wake = Math.Min(wake, window._nextFrame);
            Thread.Sleep(Math.Max(1, (int)((wake - now) * 1000 / Stopwatch.Frequency)));
        }
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Window frames must run on their owning UI thread.");
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _windows!.Remove(this);
    }
}
