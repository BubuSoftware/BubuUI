namespace BubuUI;

/// <summary>
/// 元素伪类状态掩码（D102，CSS 伪类对应物）：由 <see cref="InputRouter"/>（Hover/Pressed）
/// 与 <see cref="FocusManager"/>（Focused/FocusVisible/FocusWithin）在路由过程中维护；样式匹配
/// （<c>StyleRegistry.ApplyClass</c>）与绘制段缓存键共用。状态变化自动触发
/// 渲染失效与 class 层样式重应用（变体 setter 经 <c>AppBuilder.UseStyles</c> 链式声明）。
/// </summary>
[Flags]
public enum PseudoStates
{
    None = 0,

    /// <summary>指针悬停（CSS :hover）。捕获期间冻结在捕获元素上，释放后重算。</summary>
    Hover = 1,

    /// <summary>指针按下未释放（CSS :active / WPF IsPressed）。捕获元素持有至释放。</summary>
    Pressed = 2,

    /// <summary>键盘焦点（CSS :focus）。仅 <see cref="Element.IsFocusable"/> 元素成立。</summary>
    Focused = 4,

    /// <summary>
    /// 有效禁用（CSS :disabled，E3）：**有效值**——<see cref="Element.IsEnabled"/> 沿祖先链
    /// 继承合成（父禁用则子也禁用），与 Hover/Pressed 的「自身事件位」不同源。
    /// 合成进掩码意味着样式变体匹配与段键零特殊处理；祖先启用状态变化需逐后代失效重算
    /// （见 <see cref="Element.IsEnabled"/> setter）。
    /// </summary>
    Disabled = 8,
    /// <summary>直接拥有焦点且需要可见提示（CSS :focus-visible）。</summary>
    FocusVisible = 16,
    /// <summary>自身或实际内容后代拥有焦点（CSS :focus-within）。</summary>
    FocusWithin = 32,
}
