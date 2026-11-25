namespace EclipseUI.Platform.iOS;

// UIKit delivers events for all scenes on the same thread. Capture await continuations
// in the target scene while dispatching an event, then restore UIKit's context.
internal readonly struct SceneDispatch : IDisposable
{
    private readonly SynchronizationContext? _previous;

    public SceneDispatch(UiSynchronizationContext context)
    {
        _previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
    }

    public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
}
