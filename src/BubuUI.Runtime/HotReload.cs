using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(BubuUI.HotReload))]

namespace BubuUI;

/// <summary>Bridges .NET metadata updates to the host's UI update queue.</summary>
public static class HotReload
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<UiObject>> Instances = new();

    internal static void Register(UiObject instance)
    {
#if !DEBUG
        // A Debug application can reference a Release-built runtime package.
        if (!MetadataUpdater.IsSupported) return;
#endif
        lock (Gate)
        {
            Instances.RemoveAll(reference => !reference.TryGetTarget(out var value) || value.IsDisposed);
            Instances.Add(new(instance));
        }
    }

    /// <summary>Called by dotnet watch / the debugger; never executes UI code on this thread.</summary>
    public static void UpdateApplication(Type[]? updatedTypes)
    {
        lock (Gate)
        {
            Instances.RemoveAll(reference => !reference.TryGetTarget(out var value) || value.IsDisposed);
            foreach (var reference in Instances)
                if (reference.TryGetTarget(out var instance) && Matches(instance.GetType(), updatedTypes))
                    instance.RequestHotReload();
        }
    }

    private static bool Matches(Type instanceType, Type[]? updatedTypes)
    {
        if (updatedTypes is null) return true;
        foreach (var changed in updatedTypes)
            for (Type? type = changed; type is not null; type = type.DeclaringType)
            {
                if (type.IsAssignableFrom(instanceType)) return true;
                // Metadata updates can name an open generic definition rather than
                // the constructed type of a live component.
                if (type.IsGenericTypeDefinition)
                    for (Type? candidate = instanceType; candidate is not null; candidate = candidate.BaseType)
                        if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == type) return true;
            }
        return false;
    }
}
