namespace BubuUI;

public abstract partial class Element
{
    private Dictionary<StyledProperty, ITransitionState>? _transitions;
    private HashSet<Element>? _activeAnimationElements;
    private Element? _animationRegistryOwner;
    private bool _animationNotification;
    private StyledProperty? _baseReadProperty;
    private bool _animationsEnabled = true, _animationPaused, _suppressTransitions;
    internal bool IsAnimationNotification => _animationNotification;

    // Preserve a binding's CLR getter, including its conversions, while making reads
    // of the bound property observe the base target instead of an animation overlay.
    internal T ReadBindingTarget<T>(StyledProperty<T> property, Func<T> readTarget)
    {
        var previous = _baseReadProperty;
        _baseReadProperty = property;
        try { return readTarget(); }
        finally { _baseReadProperty = previous; }
    }
    /// <summary>Enables implicit transitions on this element.</summary>
    [UiIgnore]
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set
        {
            if (_animationsEnabled == value) return;
            _animationsEnabled = value;
            if (!value)
            {
                CancelTransitions();
                foreach (var child in Descendants().ToArray()) child.CancelTransitions();
            }
        }
    }
    internal bool CanAnimate => IsAttached && IsEffectivelyVisible && _animationsEnabled && !_animationPaused && !_suppressTransitions && (Parent?.CanAnimate ?? true);
    internal void SetAnimationPaused(bool paused) => _animationPaused = paused;

    private static void ValidateTransition<T>(StyledProperty<T> property, TransitionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Easing);
        if (spec.Duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(spec), "Duration cannot be negative.");
        if (!property.Animatable || property.Inherits || property.Invalidate != InvalidateKind.Render ||
            !TransitionInterpolators.IsSupported<T>())
            throw new NotSupportedException($"Property '{property}' does not support transitions.");
    }
    private TransitionState<T> TransitionFor<T>(StyledProperty<T> property)
    {
        _transitions ??= new();
        if (!_transitions.TryGetValue(property, out var state))
            _transitions[property] = state = new TransitionState<T>(property);
        return (TransitionState<T>)state;
    }
    internal void SetLocalTransition<T>(StyledProperty<T> property, TransitionSpec spec)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        UiThreadGuard.AssertUiThread(this, nameof(SetLocalTransition));
        ValidateTransition(property, spec);
        TransitionFor(property).SetLocal(spec);
    }
    internal void RemoveLocalTransition<T>(StyledProperty<T> property)
    {
        UiThreadGuard.AssertUiThread(this, nameof(RemoveLocalTransition));
        if (_transitions is null || !_transitions.TryGetValue(property, out var state)) return;
        state.SetLocal(null);
        state.Cancel(this);
        ReconcileAnimationRegistration();
    }
    public void SetStyleTransition<T>(StyledProperty<T> property, TransitionSpec spec)
    {
        ValidateTransition(property, spec);
        TransitionFor(property).SetStyle(spec);
    }
    public void ClearStyleTransitions()
    {
        if (_transitions is null) return;
        foreach (var state in _transitions.Values.ToArray()) state.SetStyle(null);
        if (_valueBatchDepth == 0) CommitTransitions();
    }
    public void SetControlThemeTransition<T>(StyledProperty<T> property, TransitionSpec spec)
    {
        ValidateTransition(property, spec);
        TransitionFor(property).SetTheme(spec);
    }
    public void ClearControlThemeTransitions()
    {
        if (_transitions is null) return;
        foreach (var state in _transitions.Values.ToArray()) state.SetTheme(null);
        if (_valueBatchDepth == 0) CommitTransitions();
    }
    public void SetRuleTransition<T>(StyledProperty<T> property, TransitionSpec spec)
    {
        ValidateTransition(property, spec);
        TransitionFor(property).SetRule(spec);
    }
    public void ClearRuleTransitions()
    {
        if (_transitions is null) return;
        foreach (var state in _transitions.Values.ToArray()) state.SetRule(null);
        if (_valueBatchDepth == 0) CommitTransitions();
    }
    public void ReplaceRuleTransitions(IReadOnlyDictionary<StyledProperty, ThemeSetter> setters)
    {
        using var batch = BeginValueBatch();
        ClearRuleTransitions();
        foreach (var setter in setters.Values) setter.ApplyStyle(this);
    }
    public void ReplaceControlThemeTransitions(IReadOnlyDictionary<StyledProperty, ThemeSetter> setters)
    {
        using var batch = BeginValueBatch();
        ClearControlThemeTransitions();
        foreach (var setter in setters.Values) setter.Apply(this);
    }
    private void BeforeBaseChange(StyledProperty property)
    {
        if (_transitions?.TryGetValue(property, out var state) == true)
        {
            state.Capture(this);
            return;
        }
        // A style batch may install a transition after writing the property's value.
        // Capture the pre-batch value even before its configuration has appeared.
        if (_valueBatchDepth > 0 && property.Animatable)
        {
            state = property.CreateTransitionState();
            (_transitions ??= new())[property] = state;
            state.Capture(this);
        }
    }
    private void AfterBaseChange(StyledProperty property)
    {
        if (_valueBatchDepth == 0 && _transitions?.TryGetValue(property, out var state) == true)
        {
            state.Commit(this);
            ReconcileAnimationRegistration();
        }
    }
    private void CommitTransitions()
    {
        if (_transitions is null) return;
        foreach (var pair in _transitions.ToArray())
        {
            var state = pair.Value;
            if (state.HasPending) state.Commit(this);
            if (!state.IsConfigured) state.Cancel(this);
            if (!state.IsConfigured && !state.IsActive && _transitions.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, state))
                _transitions.Remove(pair.Key);
        }
        ReconcileAnimationRegistration();
    }
    private void ReconcileAnimationRegistration()
    {
        var active = _transitions?.Values.Any(state => state.IsActive) == true;
        var root = this;
        while (root.Parent is { } parent) root = parent;
        if (_animationRegistryOwner is { } previous && (!active || !ReferenceEquals(previous, root)))
        {
            previous._activeAnimationElements?.Remove(this);
            _animationRegistryOwner = null;
        }
        if (active && _animationRegistryOwner is null)
        {
            (root._activeAnimationElements ??= new()).Add(this);
            _animationRegistryOwner = root;
        }
    }
    internal static void AdvanceRegisteredTransitions(Element subtree, TimeSpan elapsed)
    {
        var root = subtree;
        while (root.Parent is { } parent) root = parent;
        if (root._activeAnimationElements is not { Count: > 0 } active) return;
        foreach (var element in active.ToArray())
        {
            if (!ReferenceEquals(element._animationRegistryOwner, root) || !element.CanAnimate) continue;
            var ancestor = element;
            while (ancestor is not null && !ReferenceEquals(ancestor, subtree)) ancestor = ancestor.Parent;
            if (ancestor is null) continue;
            element.AdvanceTransitions(elapsed);
        }
    }
    private void AdvanceTransitions(TimeSpan elapsed)
    {
        if (_transitions is null) return;
        foreach (var state in _transitions.Values.ToArray()) state.Advance(this, elapsed);
        ReconcileAnimationRegistration();
    }
    internal void CancelTransitions()
    {
        if (_transitions is null) return;
        foreach (var state in _transitions.Values.ToArray()) state.Cancel(this);
        ReconcileAnimationRegistration();
    }
    internal void PublishAnimationChange(StyledProperty property)
    {
        ApplyValueChange(property, animationChange: true);
    }
}
