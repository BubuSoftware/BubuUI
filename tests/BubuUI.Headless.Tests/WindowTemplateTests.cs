using BubuUI.Elements;
using Xunit;

namespace BubuUI.Headless.Tests;

/// <summary>窗口模板继承根类型，初始化与更新直接作用于窗口实例。</summary>
public class WindowTemplateTests
{
    [Fact]
    public void Initialize_AssignsRootPropertiesOnTheWindowInstance()
    {
        var shell = new ShellWindow { TitleText = "Demo", W = 800, H = 600 };

        shell.EnsureInitialized();

        Assert.IsAssignableFrom<Window>(shell);
        Assert.False(typeof(Element).IsAssignableFrom(typeof(ShellWindow)));
        Assert.Equal("Demo", shell.Title);
        Assert.Equal(800, shell.Width);
        Assert.Equal(600, shell.Height);
        Assert.NotNull(shell.Content);
    }

    [Fact]
    public void Initialize_IsIdempotent_AndRetainsTheContentInstance()
    {
        var shell = new ShellWindow();
        shell.EnsureInitialized();
        var content = shell.Content;

        shell.EnsureInitialized();

        Assert.Same(content, shell.Content);
    }

    [Fact]
    public void WindowTemplate_LayoutRunsHeadless()
    {
        var shell = new ShellWindow();
        var root = new HeadlessRoot(shell);

        root.UpdateLayout();

        var panel = Assert.IsType<StackPanel>(root.Content);
        Assert.Same(shell.Content, panel);
        Assert.True(panel.DesiredSize.Height > 0);
    }

    [Fact]
    public void Invalidate_UpdatesTheSameWindowInstance()
    {
        var shell = new ShellWindow { TitleText = "first" };
        var root = new HeadlessRoot(shell);
        Assert.Equal("first", shell.Title);

        shell.TitleText = "second";
        shell.Invalidate();
        root.UpdateLayout();

        Assert.Equal("second", shell.Title);
        Assert.Same(shell.Content, root.Content);
    }

    [Fact]
    public void Invalidate_AppliesLatestValuePerBatch()
    {
        var shell = new ShellWindow { TitleText = "first" };
        var root = new HeadlessRoot(shell);

        shell.TitleText = "second";
        shell.Invalidate();
        shell.TitleText = "third";
        root.UpdateLayout();

        Assert.Equal("third", shell.Title);
    }

    [Fact]
    public void WindowTemplate_App_UsesTheGeneratedWindowDirectly()
    {
        var app = AppBuilder.Configure<ShellApp>().Build();

        var shell = Assert.IsType<ShellWindow>(app.MainWindow);
        shell.EnsureInitialized();
        Assert.Equal("App Shell", shell.Title);
        Assert.Equal(800, shell.Width);
        Assert.Equal(600, shell.Height);
        Assert.NotNull(shell.Content);
    }

    private sealed class ShellApp : Application
    {
        public override void OnFrameworkInitializationCompleted()
            => MainWindow = new ShellWindow { TitleText = "App Shell", W = 800, H = 600 };
    }
}
