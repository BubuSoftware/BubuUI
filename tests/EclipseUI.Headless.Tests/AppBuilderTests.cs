using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Gallery;
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
        Assert.Null(window.RootComponent);
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
                .UseStyles(StyleRegistry.Instance, StyleBootstrap.Apply)
                .ConfigureServices(s => s.AddSingleton(options))
                .Build();

            var window = app.MainWindow;
            Assert.NotNull(window);
            Assert.Equal("EclipseUI Gallery", window.Title);
            Assert.Equal(options.Width, window.Width);
            Assert.Equal(options.Height, window.Height);
            Assert.NotNull(window.Content);
            Assert.NotNull(window.RootComponent);
            Assert.True(window.RootComponent.IsMounted);
            Assert.Same(ElementTheme.Dark, app.DefaultTheme);
            Assert.Same(StyleRegistry.Instance, Styling.Provider);
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
