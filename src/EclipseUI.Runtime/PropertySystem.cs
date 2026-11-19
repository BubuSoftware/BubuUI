using System;
using System.Collections.Generic;

namespace EclipseUI;

/// <summary>属性失效分级：变更后需要重做的最小工作。</summary>
public enum InvalidateKind
{
    None,
    Measure,
    Arrange,
    Render,
}

public sealed class PropertyOptions
{
    public bool Inherits { get; init; }
    public InvalidateKind Invalidate { get; init; } = InvalidateKind.None;
    public bool Animatable { get; init; }
}

[AttributeUsage(AttributeTargets.Field)]
public sealed class UiAnimatableAttribute : Attribute { }

public abstract class StyledProperty
{
    protected StyledProperty(string name, Type ownerType, Type valueType, PropertyOptions options)
    {
        Name = name;
        OwnerType = ownerType;
        ValueType = valueType;
        Options = options;
    }

    public string Name { get; }
    public Type OwnerType { get; }
    public Type ValueType { get; }
    internal PropertyOptions Options { get; }
    public bool Inherits => Options.Inherits;
    public InvalidateKind Invalidate => Options.Invalidate;
    public bool Animatable => Options.Animatable;

    internal abstract ITransitionState CreateTransitionState();

    public override string ToString() => $"{OwnerType.Name}.{Name}";
}

public sealed class StyledProperty<T> : StyledProperty
{
    private StyledProperty(string name, Type ownerType, T defaultValue, PropertyOptions options)
        : base(name, ownerType, typeof(T), options)
    {
        DefaultValue = defaultValue;
    }

    public T DefaultValue { get; }

    internal override ITransitionState CreateTransitionState() => new TransitionState<T>(this);

    public static StyledProperty<T> Register<TOwner>(string name, T defaultValue = default!, PropertyOptions? options = null)
    {
        var property = new StyledProperty<T>(name, typeof(TOwner), defaultValue, options ?? new PropertyOptions());
        StyledPropertyRegistry.Register(property);
        return property;
    }
}

/// <summary>
/// 属性本地值的存储标记。值以 <see cref="ValueBox{T}"/> 的泛型字段内联保存，
/// 再以引用类型身份存入字典，避免把值类型装箱成 <see cref="object"/>（A1 去装箱）。
/// </summary>
internal interface IValueBox
{
}

/// <summary>
/// 强类型值盒：<typeparamref name="T"/> 以字段内联，命中已有盒子时只改
/// <see cref="Value"/> 即可复用，重复赋值不再产生装箱与新对象分配。
/// </summary>
internal sealed class ValueBox<T> : IValueBox
{
    public T Value;

    public ValueBox(T value) => Value = value;
}

/// <summary>属性注册表：编译器与 LSP 未来的组件元数据查询入口。</summary>
public static class StyledPropertyRegistry
{
    // 各类型的静态构造器可能在多线程并行触发首次注册，必须线程安全
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type Owner, string Name), StyledProperty> s_properties = new();

    internal static void Register(StyledProperty property)
    {
        if (!s_properties.TryAdd((property.OwnerType, property.Name), property))
            throw new InvalidOperationException($"Property '{property.OwnerType.Name}.{property.Name}' is already registered.");
    }

    public static StyledProperty? Find(Type ownerType, string name)
        => s_properties.TryGetValue((ownerType, name), out var property) ? property : null;
}
