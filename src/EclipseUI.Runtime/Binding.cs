using System;
using System.Collections.Generic;

namespace EclipseUI;

/// <summary>
/// 控件双向绑定：构造时源 → 目标，目标变化时回写源并失效所属组件。
/// Refresh 幂等；Dispose 解除属性监听。
/// </summary>
public sealed class UiBinding : IDisposable
{
    private readonly Action _refresh;
    private Action? _dispose;

    private UiBinding(Action refresh, Action dispose)
    {
        _refresh = refresh;
        _dispose = dispose;
    }

    public void Refresh()
    {
        if (_dispose is not null)
            _refresh();
    }

    public void Dispose()
    {
        var dispose = _dispose;
        _dispose = null;
        dispose?.Invoke();
    }

    public static UiBinding TwoWay<T>(
        Element element,
        StyledProperty<T> property,
        Func<T> readTarget,
        Action<T> writeTarget,
        Func<T> readSource,
        Action<T> writeSource,
        Action invalidate)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(readTarget);
        ArgumentNullException.ThrowIfNull(writeTarget);
        ArgumentNullException.ThrowIfNull(readSource);
        ArgumentNullException.ThrowIfNull(writeSource);
        ArgumentNullException.ThrowIfNull(invalidate);

        bool syncing = false;

        void PushSourceToTarget()
        {
            syncing = true;
            try
            {
                writeTarget(readSource());
            }
            finally
            {
                syncing = false;
            }
        }

        void OnPropertyChanged(Element sender, StyledProperty changed)
        {
            if (syncing || element.IsAnimationNotification || !ReferenceEquals(changed, property)) return;

            var value = element.ReadBindingTarget(property, readTarget);
            if (EqualityComparer<T>.Default.Equals(value, readSource())) return;

            writeSource(value);
            invalidate();
        }

        PushSourceToTarget();
        element.PropertyChanged += OnPropertyChanged;

        return new UiBinding(
            () =>
            {
                if (!EqualityComparer<T>.Default.Equals(element.ReadBindingTarget(property, readTarget), readSource()))
                    PushSourceToTarget();
            },
            () => element.PropertyChanged -= OnPropertyChanged);
    }
}
