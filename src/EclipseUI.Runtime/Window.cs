using EclipseUI.Elements;

namespace EclipseUI;

/// <summary>
/// 窗口描述（D101）：应用侧用纯数据表达「要一个什么样的窗口」，平台生命周期负责材料化
/// （Win32 窗口 + 表面 + 帧循环）。Runtime 不认识任何具体平台/渲染类型（<see cref="Theme"/>
/// 走 <see cref="IHostTheme"/> 标记），帧循环调节项按 <c>WindowHostOptions</c>（Platform.Windows）
/// 的字段语义命名，由平台侧逐一映射。
/// </summary>
public sealed class Window
{
    /// <summary>窗口标题（空串 = 平台默认）。</summary>
    public string Title { get; set; } = "";

    /// <summary>初始客户区宽度（逻辑单位）；&lt;= 0 时用平台默认。</summary>
    public double Width { get; set; }

    /// <summary>初始客户区高度（逻辑单位）；&lt;= 0 时用平台默认。</summary>
    public double Height { get; set; }

    /// <summary>根元素（必需；缺失时生命周期报错）。</summary>
    public Element? Content { get; set; }

    /// <summary>根组件（可选）：承担所有权与失效队列，随帧循环每帧处理（G2 口径）。</summary>
    public Component? RootComponent { get; set; }

    /// <summary>窗口主题（<c>ElementTheme.Dark/Light</c> 等）；null 回落 <see cref="Application.DefaultTheme"/>，再回落平台默认。</summary>
    public IHostTheme? Theme { get; set; }

    /// <summary>主动限帧；0 表示交给 surface（vsync）或 GPU 队列（语义同 WindowHostOptions）。</summary>
    public int MaxFramesPerSecond { get; set; }

    /// <summary>忽略脏区判定，每帧都上屏（性能对比 / 持续动画场景）。</summary>
    public bool AlwaysRender { get; set; }

    /// <summary>Esc 关闭窗口（应用内可关掉，自己处理键）。</summary>
    public bool CloseOnEscape { get; set; } = true;

    /// <summary>无脏区且未限帧时的休眠时长，避免空转占满 CPU。</summary>
    public int IdleSleepMilliseconds { get; set; } = 4;

    /// <summary>调试用：最多跑多少个循环 tick 后自动退出（null = 跑到窗口关闭）。</summary>
    public int? MaxTicks { get; set; }
}
