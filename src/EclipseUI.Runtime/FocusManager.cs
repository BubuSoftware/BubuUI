namespace EclipseUI;

/// <summary>Per-root focus state, input modality and transactional focus transfers.</summary>
public sealed class FocusManager
{
    private readonly Element _root;
    private List<Element> _path = [], _nextPath = [];
    private readonly List<Element> _changed = [];
    private readonly Queue<Request> _requests = new();
    private readonly record struct Request(Element? Target, FocusOrigin Origin, FocusVisibility Visibility, KeyModifiers Modifiers);
    private bool _committing, _refresh, _showRequested, _alwaysShow;
    private FocusOrigin _dispatchOrigin = FocusOrigin.Programmatic;
    private KeyModifiers _dispatchModifiers;
    public FocusOrigin InputMode { get; private set; } = FocusOrigin.Keyboard;
    public FocusOrigin LastFocusOrigin { get; private set; } = FocusOrigin.Programmatic;
    public Element? Focused { get; private set; }
    public FocusManager(Element root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
        var previous = root._focusManager;
        root._focusManager = this;
        previous?.ClearFocus();
    }
    public static FocusManager? GetFor(Element element) => element.FindFocusManager();

    public bool AlwaysShowFocusVisuals
    {
        get => _alwaysShow;
        set { UiThreadGuard.AssertUiThread(_root, nameof(AlwaysShowFocusVisuals)); if (_alwaysShow == value) return; _alwaysShow = value; RefreshFocusPath(); }
    }
    public IReadOnlyList<Element> Focusables => _root.Descendants()
        .Where(e => e.IsFocusable && e.IsTabStop && IsValid(e)).ToArray();

    internal bool IsValid(Element? element)
    {
        if (element is null || element.IsDisposed || !element.IsFocusable || !element.IsEffectiveEnabled || !element.IsEffectivelyVisible ||
            _root.IsDisposed || _root.Parent is not null || !ReferenceEquals(_root._focusManager, this)) return false;
        for (var e = element; e is not null; e = e.Parent) if (ReferenceEquals(e, _root)) return true;
        return false;
    }

    public bool Focus(Element? element, FocusOrigin origin = FocusOrigin.Auto, FocusVisibility visibility = FocusVisibility.Auto)
    {
        UiThreadGuard.AssertUiThread(_root, nameof(Focus));
        if (!IsValid(element)) return false;
        if (origin == FocusOrigin.Auto) origin = _dispatchOrigin;
        if (origin is FocusOrigin.Pointer or FocusOrigin.Keyboard) NotifyInput(origin);
        _requests.Enqueue(new(element, origin, visibility, _dispatchModifiers));
        Drain();
        return true;
    }

    public void ClearFocus()
    {
        UiThreadGuard.AssertUiThread(_root, nameof(ClearFocus));
        _requests.Enqueue(new(null, _dispatchOrigin, FocusVisibility.Auto, _dispatchModifiers));
        Drain();
    }

    /// <summary>Changes visibility on the same focused object without sending focus events.</summary>
    public void NotifyInput(FocusOrigin origin)
    {
        UiThreadGuard.AssertUiThread(_root, nameof(NotifyInput));
        if (origin is not (FocusOrigin.Keyboard or FocusOrigin.Pointer)) throw new ArgumentOutOfRangeException(nameof(origin));
        if (InputMode == origin && !_showRequested) return;
        InputMode = origin; _showRequested = false; RefreshFocusPath();
    }

    internal InputScope EnterInput(FocusOrigin origin, KeyModifiers modifiers)
    {
        var scope = new InputScope(this, _dispatchOrigin, _dispatchModifiers);
        _dispatchOrigin = origin; _dispatchModifiers = modifiers;
        return scope;
    }
    internal readonly struct InputScope(FocusManager manager, FocusOrigin previous, KeyModifiers modifiers) : IDisposable
    {
        public void Dispose() { manager._dispatchOrigin = previous; manager._dispatchModifiers = modifiers; }
    }

    internal void RefreshFocusPath()
    {
        if (Focused is null && _path.Count == 0) return;
        _refresh = true; Drain();
    }
    public bool ClearFocusIfDetached()
    {
        var old = Focused;
        RefreshFocusPath();
        return old is not null && Focused is null;
    }
    private void Drain()
    {
        if (_committing) return;
        _committing = true;
        try
        {
            var count = 0;
            List<Exception>? failures = null;
            while (_requests.Count > 0 || _refresh)
            {
                if (++count > 100) throw new InvalidOperationException("Focus updates exceeded 100 rounds.");
                if (_requests.TryDequeue(out var request))
                {
                    if (request.Target is not null && !IsValid(request.Target)) continue;
                    _showRequested = request.Visibility == FocusVisibility.Show;
                    try { Commit(request.Target, request.Origin, request.Modifiers); }
                    catch (Exception error) { (failures ??= []).Add(error); }
                }
                else
                {
                    _refresh = false;
                    try { Commit(IsValid(Focused) ? Focused : null, LastFocusOrigin, _dispatchModifiers); }
                    catch (Exception error) { (failures ??= []).Add(error); }
                }
                if (Focused is not null && !IsValid(Focused)) _refresh = true;
            }
            if (failures is not null) throw new AggregateException("Focus updates failed.", failures);
        }
        finally { _committing = false; _requests.Clear(); _refresh = false; }
    }

    private void Commit(Element? target, FocusOrigin origin, KeyModifiers modifiers)
    {
        var old = Focused;
        var moved = !ReferenceEquals(old, target);
        _nextPath.Clear();
        for (var e = target; e is not null; e = e.Parent) _nextPath.Add(e);
        var visible = target is not null && (_alwaysShow || _showRequested || target.IsTextInputClient || InputMode == FocusOrigin.Keyboard);
        Focused = target;
        if (moved) LastFocusOrigin = origin;
        _changed.Clear();
        foreach (var node in _path)
            if (node.SetFocusState(ReferenceEquals(node, target), _nextPath.Contains(node), visible && ReferenceEquals(node, target))) _changed.Add(node);
        foreach (var node in _nextPath)
            if (!_path.Contains(node) && node.SetFocusState(ReferenceEquals(node, target), true, visible && ReferenceEquals(node, target))) _changed.Add(node);
        (_path, _nextPath) = (_nextPath, _path);
        _nextPath.Clear();
        List<Exception>? failures = null;
        foreach (var node in _changed)
        {
            try { node.PublishFocusState(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        _changed.Clear();
        if (moved)
        {
            try { old?.RaiseEvent(new FocusEventArgs(Element.LostFocusEvent, origin, modifiers) { Source = old }); }
            catch (Exception error) { (failures ??= []).Add(error); }
            try { target?.RaiseEvent(new FocusEventArgs(Element.GotFocusEvent, origin, modifiers) { Source = target }); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException("Focus notification failed.", failures);
    }

    public Element? MovePrevious() => Move(-1);
    public Element? MoveNext() => Move(1);
    private Element? Move(int direction)
    {
        var items = Focusables;
        if (items.Count == 0) return null;
        int index = -1;
        for (var i = 0; i < items.Count; i++) if (ReferenceEquals(items[i], Focused)) { index = i; break; }
        var next = items[index < 0 ? direction > 0 ? 0 : items.Count - 1 : (index + direction + items.Count) % items.Count];
        Focus(next, FocusOrigin.Keyboard);
        return Focused;
    }
}
