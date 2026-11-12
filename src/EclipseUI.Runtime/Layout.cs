namespace EclipseUI;

/// <summary>
/// 水平对齐（design/05 §布局契约，Avalonia 同名枚举语义对齐）：决定元素在排列槽内
/// 的水平占位。默认 <see cref="Stretch"/> 与既有面板行为零回归——排列槽填满；
/// 非 Stretch 档按期望宽占位（可用 <see cref="Element.Width"/> 显式定宽）。
/// </summary>
public enum HorizontalAlignment
{
    /// <summary>填满排列槽（缺省——与既有布局行为一致）。</summary>
    Stretch,

    /// <summary>靠槽左缘，宽度取期望宽（可溢出槽，由祖先裁剪）。</summary>
    Left,

    /// <summary>槽内水平居中。</summary>
    Center,

    /// <summary>靠槽右缘。</summary>
    Right,
}

/// <summary>垂直对齐（design/05 §布局契约）：决定元素在排列槽内的垂直占位，语义同水平档。</summary>
public enum VerticalAlignment
{
    /// <summary>填满排列槽（缺省）。</summary>
    Stretch,

    /// <summary>靠槽上缘。</summary>
    Top,

    /// <summary>槽内垂直居中。</summary>
    Center,

    /// <summary>靠槽下缘。</summary>
    Bottom,
}
