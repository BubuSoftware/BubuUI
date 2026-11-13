using System;
using EclipseUI;
using EclipseUI.Elements;
using Xunit;

namespace EclipseUI.Headless.Tests;

/// <summary>
/// design/20 端到端（无头）：窗口模板 fixture（Fixtures/ShellWindow.ui）经 CreateWindow()
/// 产出接线完整的 Window 描述；动态属性走既有失效循环（Invalidate → Render → U 推送）——
/// 无头环境 LiveUpdater 未安装，推送为 no-op，但基类属性的更新路径可观察；窗口描述是
/// 创建期一次性纯数据（D101），不随失效循环变化。
/// </summary>
public class WindowTemplateTests
{
    [Fact]
    public void CreateWindow_WiresDescriptionAndComponent()
    {
        var shell = new ShellWindow { TitleText = "Demo", W = 800, H = 600 };

        var window = shell.CreateWindow();

        Assert.Equal("Demo", window.Title);
        Assert.Equal(800, window.Width);
        Assert.Equal(600, window.Height);
        Assert.True(shell.IsMounted);
        Assert.NotNull(window.Content);
        Assert.Same(shell, window.RootComponent);
        Assert.Same(shell.Root, window.Content);
    }

    [Fact]
    public void CreateWindow_IsOneShot()
    {
        var shell = new ShellWindow();
        shell.CreateWindow();

        var error = Assert.Throws<InvalidOperationException>(() => shell.CreateWindow());
        Assert.Contains("exactly one window", error.Message);
    }

    [Fact]
    public void WindowTemplate_LayoutRunsHeadless()
    {
        var shell = new ShellWindow();
        var root = new HeadlessRoot(shell);

        root.UpdateLayout();

        var panel = Assert.IsType<StackPanel>(root.Content);
        Assert.True(panel.DesiredSize.Height > 0);
    }

    [Fact]
    public void LivePropertyPush_UpdatesComponent_NotDescription()
    {
        var shell = new ShellWindow { TitleText = "first" };
        var root = new HeadlessRoot(shell);
        var window = shell.CreateWindow();
        Assert.Equal("first", window.Title);
        Assert.Equal("first", shell.Title);

        // 失效循环驱动 live 推送（无头环境 updater 未安装 → 平台回推 no-op，不抛）。
        shell.TitleText = "second";
        shell.Invalidate();
        Assert.True(shell.ProcessInvalidations());

        // 基类属性（上次推送值）更新；窗口描述是创建期一次性数据，保持不变（D101）。
        Assert.Equal("second", shell.Title);
        Assert.Equal("first", window.Title);
    }

    [Fact]
    public void LivePropertyPush_AppliesLatestValuePerBatch()
    {
        var shell = new ShellWindow { TitleText = "first" };
        var root = new HeadlessRoot(shell);
        shell.CreateWindow();

        // 同一批失效内连改两次：U 推送在渲染时读取最新状态（表达式求值不快照在入队时）。
        shell.TitleText = "second";
        shell.Invalidate();
        shell.TitleText = "third";
        Assert.True(shell.ProcessInvalidations());

        Assert.Equal("third", shell.Title);
    }

    [Fact]
    public void WindowTemplate_App_WiresMainWindow()
    {
        var app = AppBuilder.Configure<ShellApp>().Build();

        var window = app.MainWindow;
        Assert.NotNull(window);
        Assert.Equal("App Shell", window.Title);
        Assert.Equal(800, window.Width);
        Assert.Equal(600, window.Height);
        Assert.True(window.RootComponent!.IsMounted);
    }

    private sealed class ShellApp : Application
    {
        // design/20：应用侧样板收敛为一行——模板属性传值 + CreateWindow()。
        public override void OnFrameworkInitializationCompleted()
            => MainWindow = new ShellWindow { TitleText = "App Shell", W = 800, H = 600 }.CreateWindow();
    }
}
