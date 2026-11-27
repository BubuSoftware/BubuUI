using BubuUI.Elements;

namespace BubuUI;

/// <summary>
/// 窗口对象：与控件共享初始化、更新和资源管理，内容为单个 <see cref="Element"/>。
/// 本类型不派生 Element，因此不能嵌入控件树。平台生命周期负责材料化原生窗口和表面。
/// .bui 文件直接继承根标签类型，窗口实例本身即模板实例。
/// </summary>
[UiContent(UiContentKind.Children, Target = nameof(Content))]
public class Window : UiObject
{
    private string _title = "";
    private double _width;
    private double _height;

    /// <summary>窗口标题（空串 = 平台默认）。</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            LiveUpdater?.SetTitle(value);
        }
    }

    /// <summary>初始客户区宽度（逻辑单位）；&lt;= 0 时用平台默认。</summary>
    public double Width
    {
        get => _width;
        set
        {
            if (_width == value) return;
            _width = value;
            LiveUpdater?.SetClientSize(_width, _height);
        }
    }

    /// <summary>初始客户区高度（逻辑单位）；&lt;= 0 时用平台默认。</summary>
    public double Height
    {
        get => _height;
        set
        {
            if (_height == value) return;
            _height = value;
            LiveUpdater?.SetClientSize(_width, _height);
        }
    }

    /// <summary>根元素（必需；缺失时生命周期报错）。</summary>
    public Element? Content
    {
        get => SingleContent;
        set => SingleContent = value;
    }

    internal IWindowLiveUpdater? LiveUpdater { get; set; }

    /// <summary>窗口主题（<c>ElementTheme.Dark/Light</c> 等）；null 回落 <see cref="Application.DefaultTheme"/>，再回落平台默认。</summary>
    public IHostTheme? Theme { get; set; }

    /// <summary>主动限帧；0 表示交给 surface（vsync）或 GPU 队列（语义同 WindowHostOptions）。</summary>
    public int MaxFramesPerSecond { get; set; }

    /// <summary>忽略脏区判定，每帧都上屏（性能对比 / 持续动画场景）。</summary>
    public bool AlwaysRender { get; set; }
    public bool AlwaysShowFocusVisuals { get; set; }

    /// <summary>Esc 关闭窗口（应用内可关掉，自己处理键）。</summary>
    public bool CloseOnEscape { get; set; } = true;

    /// <summary>无脏区且未限帧时的休眠时长，避免空转占满 CPU。</summary>
    public int IdleSleepMilliseconds { get; set; } = 4;

    /// <summary>调试用：最多跑多少个循环 tick 后自动退出（null = 跑到窗口关闭）。</summary>
    public int? MaxTicks { get; set; }

    protected override void OnDispose()
    {
        LiveUpdater = null;
        base.OnDispose();
    }
}
