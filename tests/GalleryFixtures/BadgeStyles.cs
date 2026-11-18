using EclipseUI;

namespace Gallery;

/// <summary>演示用枚举：样式表里写 keyword（<c>Tone: Success</c>）。</summary>
public enum BadgeTone
{
    Neutral,
    Success,
    Danger,
}

/// <summary>
/// design/19 P1 端到端演示：应用程序集用 <c>[UiStyleProperty]</c> 声明自定义样式属性——
/// 不改框架源码，目录进料口即从「StyleBuilder 方法」解耦为「标注字段」（opt-in，显式标注
/// 同时是文档）。三种值形态各演示一条：裸数字 / 枚举 keyword / 字符串。
/// 值经 .eustyle 类通道（s.Set）与模板 Style= 通道（SetValue）落到元素 style 层。
/// Owner 统一登记为 <c>Element</c>：这三个属性对所有元素可用（与内建属性口径一致）。
/// </summary>
public static class BadgeStyles
{
    /// <summary>裸数字形态：<c>Glow: 6</c>。属性名缺省 = 字段名去 Property 后缀。</summary>
    [UiStyleProperty]
    public static readonly StyledProperty<double> GlowProperty =
        StyledProperty<double>.Register<Element>("Glow", 0d);

    /// <summary>枚举 keyword 形态：<c>Tone: Success</c>（成员表编译期校验，发射 T.Member）。</summary>
    [UiStyleProperty(Name = "Tone", Owner = typeof(Element))]
    public static readonly StyledProperty<BadgeTone> ToneProperty =
        StyledProperty<BadgeTone>.Register<Element>("Tone", BadgeTone.Neutral);

    /// <summary>字符串形态：<c>BadgeLabel: "eustyle"</c>。</summary>
    [UiStyleProperty]
    public static readonly StyledProperty<string> BadgeLabelProperty =
        StyledProperty<string>.Register<Element>("BadgeLabel", "");
}
