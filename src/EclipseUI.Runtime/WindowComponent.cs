using System;

namespace EclipseUI;

/// <summary>
/// 窗口模板基类（design/20）：根节点为 <c>&lt;Window&gt;</c> 的 .ui 文件编译为本类的子类——
/// 「根标签决定基类」（Avalonia 式）在 Component 家族内的落点：失效/参数/生命周期机制全部
/// 继承自 <see cref="Component"/>（组件不派生 Element，spec/03 §5.3，所以控件根不走派生语义）。
/// <para>
/// 模板属性是本类的 [UiParameter] 面（生成代码在 Build 开头赋值，动态表达式由 U 推送守卫更新）；
/// <see cref="CreateWindow"/> 把它们一次性组装成 <see cref="Window"/> 纯数据描述（D101：平台
/// 生命周期负责材料化），并自动接线 <c>Content</c>/<c>RootComponent</c>——应用侧从
/// 「Mount + Content + RootComponent 三处手工同步」收敛为一行。
/// </para>
/// </summary>
[UiTemplateRoot]
public abstract class WindowComponent : Component
{
    private Window? _created;
    private IWindowLiveUpdater? _liveUpdater;

    /// <summary>窗口标题（空串 = 平台默认）。动态属性：变更经 <see cref="PushWindowTitle"/> 回推。</summary>
    [UiParameter] public string Title { get; set; } = "";

    /// <summary>初始客户区宽度（逻辑单位）；&lt;= 0 时用平台默认。动态属性（成对推送）。</summary>
    [UiParameter] public double Width { get; set; }

    /// <summary>初始客户区高度（逻辑单位）；&lt;= 0 时用平台默认。动态属性（成对推送）。</summary>
    [UiParameter] public double Height { get; set; }

    /// <summary>窗口主题（<c>ElementTheme.Dark/Light</c> 等）；null 回落 <see cref="Application.DefaultTheme"/>。模板只能用 {表达式} 形式（无字面量转换器）。</summary>
    [UiParameter] public IHostTheme? Theme { get; set; }

    /// <summary>主动限帧；0 表示交给 surface（vsync）或 GPU 队列。创建期一次性（WindowHostOptions 固化，不随失效循环更新）。</summary>
    [UiParameter] public int MaxFramesPerSecond { get; set; }

    /// <summary>忽略脏区判定，每帧都上屏。创建期一次性。</summary>
    [UiParameter] public bool AlwaysRender { get; set; }

    /// <summary>Esc 关闭窗口（应用内可关掉，自己处理键）。创建期一次性。</summary>
    [UiParameter] public bool CloseOnEscape { get; set; } = true;

    /// <summary>无脏区且未限帧时的休眠时长。创建期一次性。</summary>
    [UiParameter] public int IdleSleepMilliseconds { get; set; } = 4;

    /// <summary>
    /// 一次性生成窗口描述（design/20）：挂载内容、按模板属性组装 <see cref="Window"/>，
    /// 自动接线 <c>Content = Root</c>、<c>RootComponent = this</c>。重复调用抛异常——
    /// 一个窗口模板实例只描述一个窗口。
    /// </summary>
    public Window CreateWindow()
    {
        if (_created is not null)
            throw new InvalidOperationException(
                $"CreateWindow was already called on '{GetType().FullName}'; a window template describes exactly one window.");
        // Mount 幂等（Component.Mount 首行守卫）；Build 内已把模板属性赋给本类属性。
        Mount();
        _created = new Window
        {
            Title = Title,
            Width = Width,
            Height = Height,
            Theme = Theme,
            MaxFramesPerSecond = MaxFramesPerSecond,
            AlwaysRender = AlwaysRender,
            CloseOnEscape = CloseOnEscape,
            IdleSleepMilliseconds = IdleSleepMilliseconds,
            Content = Root,
            RootComponent = this,
        };
        return _created;
    }

    /// <summary>
    /// 窗口描述变更回推通道的安装槽（design/20 动态属性）：平台生命周期在材料化后赋值
    /// （internal + IVT，应用代码不可达）。未安装（无头测试 / 未材料化）时推送为 no-op。
    /// </summary>
    internal IWindowLiveUpdater? LiveUpdater
    {
        get => _liveUpdater;
        set => _liveUpdater = value;
    }

    /// <summary>动态推送窗口标题（生成代码的 U 推送调用；值未变的守卫在生成侧）。</summary>
    protected void PushWindowTitle(string title) => _liveUpdater?.SetTitle(title);

    /// <summary>动态推送窗口客户区尺寸（逻辑单位）。Width/Height 两路推送共用本方法，读取当前属性成对下发，保证一致。</summary>
    protected void PushWindowClientSize(double width, double height) => _liveUpdater?.SetClientSize(width, height);
}

/// <summary>
/// 窗口描述变更回推通道（design/20）：把 WindowComponent 的动态属性推送落到**已材料化**的
/// 实际窗口。平台侧实现；Runtime 不认识任何具体平台类型（D101 边界）。
/// </summary>
public interface IWindowLiveUpdater
{
    /// <summary>更新窗口标题（窗口已关闭时应为 no-op）。</summary>
    void SetTitle(string title);

    /// <summary>更新窗口客户区尺寸（逻辑单位；窗口已关闭时应为 no-op）。</summary>
    void SetClientSize(double width, double height);
}
