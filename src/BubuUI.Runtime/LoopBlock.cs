using System;
using BubuUI.Elements;

namespace BubuUI;

/// <summary>
/// A layout-transparent imperative template loop. Each refresh executes the loop again
/// and replaces its content; use ForEachBlock for keyed item reuse.
/// </summary>
[UiIgnore]
public sealed class LoopBlock : StackPanelBase
{
    private Action<BlockScope>? _builder;
    private BlockScope? _scope;

    public override bool IsVisualTransparent => true;
    public override bool IsLayoutTransparent => true;

    public void SetContent(Action<BlockScope> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _builder = builder;
    }

    public void Refresh()
    {
        if (IsDisposed || _builder is null) return;
        try { DisposeScope(); }
        finally { Children.Clear(); }
        var scope = new BlockScope();
        try
        {
            _builder(scope);
            foreach (var root in scope.Roots) Children.Add(root);
            _scope = scope;
        }
        catch (Exception error)
        {
            Children.Clear();
            scope.Abort(error);
            throw;
        }
    }

    private void DisposeScope()
    {
        var scope = _scope;
        _scope = null;
        scope?.DisposeResources();
    }

    protected override void OnDispose()
    {
        try { DisposeScope(); }
        finally
        {
            _builder = null;
            Children.Clear();
            base.OnDispose();
        }
    }
}
