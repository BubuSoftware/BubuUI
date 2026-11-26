using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Gallery;
using Gallery.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// AppBuilder 装配契约（D101）：Configure/Build/Start 三段分离、DI 注册顺序（用户覆盖平台）、
/// 主题/样式接线、无平台 fail-fast。改全局静态（Styling.Provider）的用例集中在本类
/// （xunit 同类内串行）并在收尾还原，避免污染其它测试。
/// </summary>
public class AppBuilderTests
{
    private sealed class ActivityLifetime : IActivityApplicationLifetime
    {
        public Func<Element>? MainViewFactory { get; set; }
    }

    [Fact]
    public void GalleryApp_ActivityLifetime_CreatesFreshViewsWithoutDesktopOptions()
    {
        var lifetime = new ActivityLifetime();
        var app = AppBuilder.Configure<GalleryApp>()
            .ConfigureServices(s => s.AddSingleton<IActivityApplicationLifetime>(lifetime))
            .UseTheme(ElementTheme.Dark)
            .Build();
        try
        {
            Assert.Null(app.MainWindow);
            var factory = Assert.IsType<Func<Element>>(lifetime.MainViewFactory);
            var first = factory();
            first.Dispose();
            using var second = factory();
            Assert.NotSame(first, second);
            Assert.False(second.IsDisposed);
            Assert.IsType<FluentGalleryPage>(second).ApplyTheme!(ElementTheme.Light);
            Assert.Same(ElementTheme.Light, app.DefaultTheme);
        }
        finally { (app.Services as IDisposable)?.Dispose(); }
    }

    private sealed class TestApp : Application
    {
        public int Initialized;
        public override void OnFrameworkInitializationCompleted() => Initialized++;
    }

    private sealed class TestTheme : IHostTheme
    {
    }

    private sealed class FakeStyleProvider : IStyleProvider
    {
        public bool Configured;
        public void ApplyClass(Element element, string className) { }
        public void ClearStyles(Element element) { }
    }

    private interface IGreeter
    {
        string Greet();
    }

    private sealed class GreeterA : IGreeter
    {
        public string Greet() => "A";
    }

    private sealed class GreeterB : IGreeter
    {
        public string Greet() => "B";
    }

    [Fact]
    public void Build_CreatesApp_CallsInit_ExposesServices()
    {
        var app = AppBuilder.Configure<TestApp>()
            .ConfigureServices(s => s.AddSingleton<IGreeter, GreeterA>())
            .Build();

        var testApp = Assert.IsType<TestApp>(app);
        Assert.Equal(1, testApp.Initialized);
        Assert.Equal("A", app.Services.GetRequiredService<IGreeter>().Greet());
        Assert.Null(app.DefaultTheme);
        Assert.Null(app.MainWindow);
    }

    [Fact]
    public void UseTheme_ExposesDefaultTheme()
    {
        var theme = new TestTheme();
        var app = AppBuilder.Configure<TestApp>().UseTheme(theme).Build();

        Assert.Same(theme, app.DefaultTheme);
    }

    [Fact]
    public void UseStyles_WiresProvider_AndRunsConfigure()
    {
        try
        {
            var provider = new FakeStyleProvider();
            var app = AppBuilder.Configure<TestApp>()
                .UseStyles(provider, p => p.Configured = true)
                .Build();

            Assert.Same(provider, Styling.Provider);
            Assert.True(provider.Configured);
        }
        finally
        {
            Styling.Provider = null;
        }
    }

    [Fact]
    public void PlatformSetupsRegisterFirst_UserServicesCanOverride()
    {
        var app = AppBuilder.Configure<TestApp>()
            .UseSetup(new TestSetup(s => s.AddSingleton<IGreeter, GreeterA>()))
            .ConfigureServices(s => s.AddSingleton<IGreeter, GreeterB>())
            .Build();

        // MEDI 同类型多注册：最后注册优先——用户可覆盖平台默认。
        Assert.Equal("B", app.Services.GetRequiredService<IGreeter>().Greet());
    }

    [Fact]
    public void Start_WithoutLifetime_FailsFast()
    {
        var builder = AppBuilder.Configure<TestApp>();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Start([]));
        Assert.Contains("IApplicationLifetime", ex.Message);
    }

    [Fact]
    public void SetTheme_WithoutHost_IsNoOp()
    {
        var app = AppBuilder.Configure<TestApp>().Build();
        app.SetTheme(new TestTheme()); // Host 为 null：静默忽略，不抛
    }

    [Fact]
    public void Window_DescribeDefaults()
    {
        var window = new Window();

        Assert.Equal(string.Empty, window.Title);
        Assert.Null(window.Content);
        Assert.IsAssignableFrom<UiObject>(window);
        Assert.Null(window.Theme);
        Assert.Equal(0, window.MaxFramesPerSecond);   // 交给 vsync / GPU 队列
        Assert.False(window.AlwaysRender);
        Assert.True(window.CloseOnEscape);
        Assert.Equal(4, window.IdleSleepMilliseconds);
        Assert.Null(window.MaxTicks);
    }

    [Fact]
    public void GalleryApp_Build_WiresRealApplication()
    {
        try
        {
            var options = GalleryLaunchOptions.Parse([]);
            var app = AppBuilder.Configure<GalleryApp>()
                .UseTheme(ElementTheme.Dark)
                .UseStyles(StyleRegistry.Instance, Gallery.StyleBootstrap.Apply)
                .ConfigureServices(s =>
                {
                    s.AddSingleton(options);
                    s.AddSingleton<IGalleryDemoState, GalleryDemoState>();
                })
                .Build();

            var window = app.MainWindow;
            Assert.NotNull(window);
            Assert.Equal("EclipseUI Gallery", window.Title);
            Assert.Equal(options.Width, window.Width);
            Assert.Equal(options.Height, window.Height);
            Assert.NotNull(window.Content);
            // 14 行控件目录 + 1 行 K37 @inject 示例（服务已注册 → DependencyInjectionExample 挂载）
            Assert.Equal(15, Assert.IsType<MainWindow>(window).Page.LiveControls.Count);
            Assert.Same(ElementTheme.Dark, app.DefaultTheme);
            Assert.Same(StyleRegistry.Instance, Styling.Provider);
        }
        finally
        {
            Styling.Provider = null;
        }
    }

    /// <summary>
    /// design/19 P1 端到端（Gallery 演示）：应用程序集声明的 <c>[UiStyleProperty]</c> 自定义
    /// 样式属性经 .eustyle 类通道编译为 s.Set 并注册——元素挂 badge 类后，值经
    /// Styling.Provider 落到 class 层（badge 元素自身 0×0 不绘制，零视觉影响）。
    /// </summary>
    [Fact]
    public void GalleryApp_CustomStyleProperties_ReachTheClassLayer()
    {
        try
        {
            Styling.Provider = StyleRegistry.Instance;
            Gallery.StyleBootstrap.Apply(StyleRegistry.Instance);
            using var window = new LegacyWindow();
            window.EnsureInitialized();
            GalleryFixtureInteractions.Wire(window);

            Assert.True(StyleRegistry.Instance.IsDefined("badge"));

            // 第 5 分区的可见类通道演示：读回文案证明 .badge 规则的三个值落在 class 层
            //（hover 变体的运行时翻转需要真实指针，这里只断言 base 层）。
            var demo = window.Content!.Descendants<Button>()
                .Single(b => b.Label.StartsWith("class .badge", StringComparison.Ordinal));
            Assert.Contains("Glow=6", demo.Label);
            Assert.Contains("Tone=Success", demo.Label);
            Assert.Contains("BadgeLabel=\"eustyle\"", demo.Label);

            var badge = new TextBlock();
            badge.Classes = ["badge"];
            Assert.Equal(6d, badge.GetValue(BadgeStyles.GlowProperty));
            Assert.Equal(BadgeTone.Success, badge.GetValue(BadgeStyles.ToneProperty));
            Assert.Equal("eustyle", badge.GetValue(BadgeStyles.BadgeLabelProperty));

            // 变体通道（&:hover { Glow: 12; }）的运行时行为由 HeadlessRoot.PointerMove
            // 维护 IsHovered——这里只断言 base 层；hover 变体的编译与状态选择
            // 已由 EustyleTests / StyleTests 覆盖。
        }
        finally
        {
            Styling.Provider = null;
        }
    }

    private sealed class TestSetup(Action<IServiceCollection> register) : IPlatformSetup
    {
        public void RegisterServices(IServiceCollection services) => register(services);
    }
}
