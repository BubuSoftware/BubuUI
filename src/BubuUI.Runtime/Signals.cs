using System;
using System.Collections.Generic;

namespace BubuUI;

/// <summary>
/// 显式信号：写入触发订阅者（编译器生成的定向更新函数），
/// 读取不追踪依赖——依赖关系由编译期分析静态生成。
/// </summary>
public interface ISignal
{
    IDisposable Subscribe(Action handler);

    object? BoxedValue { get; }
}

public sealed class Signal<T> : ISignal
{
    private T _value;
    private List<Subscription>? _subs;

    private sealed class Subscription : IDisposable
    {
        private Signal<T>? _owner;

        public Subscription(Signal<T> owner, Action handler)
        {
            _owner = owner;
            Handler = handler;
        }

        public Action Handler { get; }

        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            _owner?.Unsubscribe(this);
            _owner = null;
        }
    }

    public Signal(T initialValue) => _value = initialValue;

    public T Value
    {
        get => _value;
        set
        {
            if (EqualityComparer<T>.Default.Equals(_value, value)) return;
            _value = value;
            var subs = _subs;
            if (subs is null) return;
            // 快照后触发：处理函数内允许订阅/退订；已退订的不触发
            var snapshot = subs.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (!snapshot[i].IsDisposed)
                    snapshot[i].Handler();
            }
        }
    }

    public IDisposable Subscribe(Action handler)
    {
        var sub = new Subscription(this, handler);
        (_subs ??= new List<Subscription>()).Add(sub);
        return sub;
    }

    /// <summary>当前订阅数（DevTools 依赖图与泄漏测试用）。</summary>
    public int SubscriberCount => _subs?.Count ?? 0;

    private void Unsubscribe(Subscription sub) => _subs?.Remove(sub);

    object? ISignal.BoxedValue => _value;

    public override string ToString() => _value?.ToString() ?? "";
}
