using System;
using System.Linq;
using EclipseUI.Elements;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// K37 <c>@inject</c> 端到端（spec/01 §4.18、spec/02 §4.7/R11）：<see cref="AppBuilder.Build"/>
/// 置位 <see cref="Application.Current"/> 后，生成组件在 InitializeUi 最前解析服务并驱动模板
/// 表达式；服务未注册或应用未构建时抛 <see cref="InvalidOperationException"/>（fail-fast，
/// 不构建内容树）。用例改全局静态（Application.Current）并在收尾还原
/// （程序集已禁用并行，仍按 AppBuilderTests 口径隔离）。
/// </summary>
public class InjectServiceTests
{
    private sealed class InjectTestApp : Application
    {
    }

    private static Application BuildApp(bool registerGreeter)
    {
        var builder = AppBuilder.Configure<InjectTestApp>();
        if (registerGreeter)
            builder.ConfigureServices(s => s.AddSingleton<IInjectGreeter, StaticGreeter>());
        return builder.Build();
    }

    [Fact]
    public void RegisteredService_ResolvesBeforeContentTree_AndDrivesTemplate()
    {
        var previous = Application.Current;
        var app = BuildApp(registerGreeter: true);
        try
        {
            Assert.Same(app, Application.Current);

            var probe = new InjectProbe();
            using var root = new HeadlessRoot(probe);
            root.UpdateLayout();

            // 解析实例以容器注册为准（单例 → 同一实例），并驱动 {Greeter.Greet()} 模板表达式
            Assert.Same(app.Services.GetService<IInjectGreeter>(), probe.Greeter);
            var text = Assert.Single(probe.Descendants<TextBlock>());
            Assert.Equal("hello-di", text.FullText());
        }
        finally
        {
            Application.Current = previous;
            (app.Services as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void UnregisteredService_ThrowsInvalidOperation_BeforeBuildingAnyTree()
    {
        var previous = Application.Current;
        var app = BuildApp(registerGreeter: false);
        try
        {
            var probe = new InjectProbe();

            var error = Assert.Throws<InvalidOperationException>(() => probe.EnsureInitialized());

            Assert.Contains("Greeter", error.Message);
            Assert.Contains("not registered", error.Message);
            // fail-fast：解析是 InitializeUi 最前语句，内容树尚未开始构建
            Assert.False(probe.IsInitialized);
            Assert.True(probe.IsDisposed);
            Assert.Empty(probe.Descendants<TextBlock>());
        }
        finally
        {
            Application.Current = previous;
            (app.Services as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void WithoutBuiltApplication_ThrowsInvalidOperation()
    {
        var previous = Application.Current;
        Application.Current = null;
        try
        {
            var probe = new InjectProbe();

            var error = Assert.Throws<InvalidOperationException>(() => probe.EnsureInitialized());

            Assert.Contains("Greeter", error.Message);
            Assert.False(probe.IsInitialized);
        }
        finally
        {
            Application.Current = previous;
        }
    }
}
