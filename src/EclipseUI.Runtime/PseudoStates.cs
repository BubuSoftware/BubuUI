namespace EclipseUI;

/// <summary>
/// 元素伪类状态掩码（D102，CSS 伪类对应物）：由 <see cref="InputRouter"/>（Hover/Pressed）
/// 与 <see cref="FocusManager"/>（Focused）在路由过程中维护；样式匹配
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
}
