namespace EclipseUI;

/// <summary>Maps normalized time to progress.</summary>
public static class Easings
{
    public static double Linear(double progress) => progress;
    public static double CubicOut(double progress) => 1 - Math.Pow(1 - progress, 3);
}

/// <summary>Immutable settings for an implicit property transition.</summary>
public sealed record TransitionSpec(TimeSpan Duration, Func<double, double> Easing)
{
    public TransitionSpec(TimeSpan duration) : this(duration, Easings.Linear) { }
}

public static class Transitions
{
    public static void Set<T>(Element element, StyledProperty<T> property, TransitionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetLocalTransition(property, spec);
    }
    public static void Remove<T>(Element element, StyledProperty<T> property)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.RemoveLocalTransition(property);
    }
}

/// <summary>Registers typed interpolation for values owned by other assemblies.</summary>
public static class TransitionInterpolators
{
    internal static class Slot<T>
    {
        internal static Func<T, T, double, T>? Interpolate;
        internal static Func<T, T, bool>? CanInterpolate;
    }
    public static void Register<T>(Func<T, T, double, T> interpolate, Func<T, T, bool>? canInterpolate = null)
    {
        ArgumentNullException.ThrowIfNull(interpolate);
        Slot<T>.Interpolate = interpolate;
        Slot<T>.CanInterpolate = canInterpolate;
    }
    public static bool IsSupported<T>() => typeof(T) == typeof(double) || typeof(T) == typeof(UiColor) || Slot<T>.Interpolate is not null;
}

internal interface ITransitionState
{
    bool HasPending { get; }
    bool IsActive { get; }
    void Capture(Element element);
    bool Commit(Element element);
    void Advance(Element element, TimeSpan elapsed);
    void Cancel(Element element);
    void SetStyle(TransitionSpec? spec);
    void SetTheme(TransitionSpec? spec);
    void SetRule(TransitionSpec? spec);
    void SetLocal(TransitionSpec? spec);
    bool IsConfigured { get; }
}

internal sealed class TransitionState<T>(StyledProperty<T> property) : ITransitionState
{
    private TransitionSpec? _local, _style, _rule, _theme, _running;
    private T _oldBase = default!, _oldDisplay = default!, _from = default!, _target = default!, _current = default!;
    private bool _pending, _active;
    private long _generation;
    private TimeSpan _elapsed;
    private TransitionSpec? Effective => _local ?? _style ?? _rule ?? _theme;
    public bool HasPending => _pending;
    public bool IsActive => _active;
    public bool IsConfigured => Effective is not null;
    public bool TryGet(out T value) { value = _current; return _active; }
    public void SetStyle(TransitionSpec? spec) => _style = spec;
    public void SetTheme(TransitionSpec? spec) => _theme = spec;
    public void SetRule(TransitionSpec? spec) => _rule = spec;
    public void SetLocal(TransitionSpec? spec) => _local = spec;
    public void Capture(Element element)
    {
        if (_pending) return;
        _oldBase = element.GetBaseValue(property);
        _oldDisplay = element.GetValue(property);
        _pending = true;
    }
    public bool Commit(Element element)
    {
        if (!_pending) return false;
        _pending = false;
        var generation = ++_generation;
        var next = element.GetBaseValue(property);
        if (EqualityComparer<T>.Default.Equals(_oldBase, next)) return false;
        var spec = Effective;
        var interpolable = CanInterpolate(_oldDisplay, next);
        if (generation != _generation) return true;
        if (spec is null || spec.Duration == TimeSpan.Zero || !element.CanAnimate ||
            !interpolable || EqualityComparer<T>.Default.Equals(_oldDisplay, next))
        {
            _active = false;
            _running = null;
            return true;
        }
        _from = _oldDisplay;
        _target = next;
        _current = _from;
        _elapsed = TimeSpan.Zero;
        _running = spec;
        _active = true;
        return true;
    }
    public void Advance(Element element, TimeSpan elapsed)
    {
        if (!_active || _running is not { } spec) return;
        var generation = _generation;
        var from = _from; var target = _target;
        _elapsed = _elapsed >= spec.Duration - elapsed ? spec.Duration : _elapsed + elapsed;
        var t = Math.Clamp(_elapsed.TotalSeconds / spec.Duration.TotalSeconds, 0, 1);
        var eased = spec.Easing(t);
        if (generation != _generation) return;
        if (!double.IsFinite(eased)) eased = t;
        var next = Interpolate(from, target, Math.Clamp(eased, 0, 1));
        if (generation != _generation) return;
        var changed = !EqualityComparer<T>.Default.Equals(_current, next);
        _current = next;
        if (t >= 1) { _active = false; _running = null; }
        if (changed) element.PublishAnimationChange(property);
    }
    public void Cancel(Element element)
    {
        _generation++;
        if (!_active) return;
        var old = _current;
        _active = false;
        _running = null;
        if (!EqualityComparer<T>.Default.Equals(old, element.GetBaseValue(property))) element.PublishAnimationChange(property);
    }
    private static bool CanInterpolate(T from, T to)
    {
        if (typeof(T) == typeof(double)) return double.IsFinite((double)(object)from!) && double.IsFinite((double)(object)to!);
        if (typeof(T) == typeof(UiColor)) return true;
        return TransitionInterpolators.Slot<T>.Interpolate is not null && (TransitionInterpolators.Slot<T>.CanInterpolate?.Invoke(from, to) ?? true);
    }
    private static T Interpolate(T a, T b, double t)
    {
        if (t <= 0) return a;
        if (t >= 1) return b;
        if (typeof(T) == typeof(double))
        {
            var x = (double)(object)a!; var y = (double)(object)b!;
            return (T)(object)(x * (1 - t) + y * t);
        }
        if (typeof(T) != typeof(UiColor)) return TransitionInterpolators.Slot<T>.Interpolate!(a, b, t);
        var c = (UiColor)(object)a!; var d = (UiColor)(object)b!;
        static byte Blend(byte x, byte y, double t) => (byte)Math.Clamp((int)Math.Round(x + (y - x) * t, MidpointRounding.AwayFromZero), 0, 255);
        return (T)(object)new UiColor(Blend(c.R, d.R, t), Blend(c.G, d.G, t), Blend(c.B, d.B, t), Blend(c.A, d.A, t));
    }
}
