using System;
using System.Collections.Generic;

namespace EclipseUI;

/// <summary>
/// 样式提供者（S-B）：把「样式类名 → 属性 setter 序列」的解析与应用接到
/// 上层实现（Rendering 层的 <c>StyleRegistry</c>）。Runtime 不依赖任何渲染类型；
/// 宿主启动时装配 <see cref="Element.StyleProvider"/> 即启用 Class 样式。
/// 优先级契约：local（<see cref="Element.SetValue"/>）&gt; class（<see cref="Element.SetStyleValue"/>）
/// &gt; 继承 &gt; 默认值。
/// </summary>
public interface IStyleProvider
{
    void ApplyRules(Element element) { }
    void ApplyTemplateStyles(Elements.TemplatedControl owner) { }
    /// <summary>应用一个样式类的 setter 序列（按 <see cref="Element.SetStyleValue"/> 写 class 层）。</summary>
    void ApplyClass(Element element, string className);

    /// <summary>清除该元素的 class 层值（Classes 变化或移除时调用；local 不受影响）。</summary>
    void ClearStyles(Element element);
}

/// <summary>样式系统装配点：宿主启动时设置实现；null（默认）时 <see cref="Element.Classes"/> 只存储不生效。</summary>
public static class Styling
{
    private static IStyleProvider? _provider;

    /// <summary>全局样式提供者。setter 必须在元素挂载前装配（宿主 Main 早期）。</summary>
    public static IStyleProvider? Provider
    {
        get => _provider;
        set
        {
            _provider = value;
            // 装配时机晚于元素建树的自检场景：对已声明 Classes 的存量元素补应用一次。
            if (value is null || PendingClasses.Count == 0)
                return;
            foreach (var (element, classes) in PendingClasses)
            {
                value.ClearStyles(element);
                foreach (var cls in classes)
                    value.ApplyClass(element, cls);
            }
            PendingClasses.Clear();
        }
    }

    /// <summary>Provider 未装配时声明过 Classes 的元素（装配时补应用）。</summary>
    internal static readonly List<(Element Element, IReadOnlyList<string> Classes)> PendingClasses = new();
}
