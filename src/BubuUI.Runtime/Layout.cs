namespace BubuUI;

/// <summary>
/// 水平对齐（design/05 §布局契约，Avalonia 同名枚举语义对齐）：决定元素在排列槽内
/// 的水平占位。默认 <see cref="Stretch"/> 填满排列槽并应用尺寸约束；
/// 非 Stretch 档采用槽与受约束期望的较小值，再应用显式尺寸和 Min/Max。
/// </summary>
public enum HorizontalAlignment
{
    /// <summary>填满排列槽；受尺寸约束未铺满时居中。</summary>
    Stretch,

    /// <summary>靠槽左缘。</summary>
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
