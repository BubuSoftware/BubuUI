using System;
using System.Runtime.CompilerServices;

namespace EclipseUI;

/// <summary>
/// 附加属性句柄（design/14 §14.5.5）：宿主类声明 <c>public static readonly AttachedProperty&lt;T&gt; XxxProperty</c>
/// 与 <c>public static GetXxx/SetXxx</c> 方法对，模板经 <c>Owner.Prop="v"</c> 写入，生成代码直调静态 setter。
/// 值按元素弱表（<see cref="ConditionalWeakTable{TKey,TValue}"/>）存储：元素卸载后随 GC 回收，
/// 不需要在 Unmount 时清理（K33 泄漏口径——弱表键不阻止元素回收）。
/// </summary>
/// <typeparam name="T">属性值类型；编译期字面量转换与目录 <see cref="Compiler.UiTypeShape"/> 由扫描器从该类型实参派生。</typeparam>
public sealed class AttachedProperty<T>
{
    // 值盒复用 PropertySystem 的 ValueBox<T>（internal）：重复赋值只改盒内字段，不产生新分配。
    private readonly ConditionalWeakTable<Element, ValueBox<T>> _values = new();

    public AttachedProperty(string name, Type ownerType, T defaultValue = default!)
    {
        Name = name;
        OwnerType = ownerType;
        DefaultValue = defaultValue;
    }

    public string Name { get; }

    /// <summary>声明该句柄的宿主类型；与模板前缀（OwnerTag）对应，仅供诊断与工具使用。</summary>
    public Type OwnerType { get; }

    public T DefaultValue { get; }

    /// <summary>读值：元素上没有本地值时返回 <see cref="DefaultValue"/>。</summary>
    public T GetValue(Element? element)
        => element is not null && _values.TryGetValue(element, out var box) ? box.Value : DefaultValue;

    /// <summary>写值：首个写入分配值盒，后续写入复用同一盒子（A1 去装箱口径）。</summary>
    public void SetValue(Element element, T value)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (_values.TryGetValue(element, out var box))
        {
            if (EqualityComparer<T>.Default.Equals(box.Value, value)) return;
            box.Value = value;
        }
        else
            _values.Add(element, new ValueBox<T>(value));
        element.InvalidateMeasure();
        element.Parent?.InvalidateMeasure();
        element.InvalidateRender();
    }

    /// <summary>清除本地值；之后读取回落到 <see cref="DefaultValue"/>。弱表本身无需清理，此方法只供显式复位。</summary>
    public void ClearValue(Element element) => _values.Remove(element);

    public override string ToString() => $"{OwnerType.Name}.{Name}";
}
