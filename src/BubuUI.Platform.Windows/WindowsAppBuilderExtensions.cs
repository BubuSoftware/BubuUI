using System.Runtime.Versioning;
using BubuUI;
using BubuUI.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace BubuUI.Platform.Windows;

/// <summary>
/// AppBuilder 扩展（D101）：注册 Win32 桌面生命周期——把 <see cref="Application.MainWindow"/>
/// 的描述材料化为 Win32 窗口 + 表面（<c>ISurfaceFactory</c>，另经 <c>UseVello()</c> 注册）+
/// <see cref="WindowHost"/> 帧循环，跑至窗口关闭。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsAppBuilderExtensions
{
    /// <summary>使用 Win32 窗口与消息/帧循环（Windows 桌面生命周期）。</summary>
    public static AppBuilder UseWindows(this AppBuilder builder)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        return builder.UseSetup(new WindowsPlatformSetup());
    }

    private sealed class WindowsPlatformSetup : IPlatformSetup
    {
        public void RegisterServices(IServiceCollection services)
        {
            services.AddSingleton<ClassicDesktopLifetime>();
            services.AddSingleton<IApplicationLifetime>(provider => provider.GetRequiredService<ClassicDesktopLifetime>());
            services.AddSingleton<IWindowLifetime>(provider => provider.GetRequiredService<ClassicDesktopLifetime>());
        }
    }
}

/// <summary>
/// 桌面生命周期（D101）：窗口描述 → Win32Window + ISurfacePresenter + WindowHost。
/// 诊断输出沿用原画廊宿主（backend/首帧/统计）；退出时先释放 UI 对象，
/// 再按 host → presenter → 原生窗口顺序释放平台资源。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ClassicDesktopLifetime : IApplicationLifetime, IWindowLifetime
{
    public int Run(Application app, string[] args)
        => RunWindow(app, app.MainWindow
            ?? throw new InvalidOperationException(
                "Application.MainWindow was not set. Create it in OnFrameworkInitializationCompleted()."));

    public int RunWindow(Application app, Window window, bool modal = false)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.ValidateWindow(window);
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(window.IsDisposed, window);
        Win32Window? win = null;
        ISurfacePresenter? presenter = null;
        WindowHost? host = null;
        IDisposable? session = null;
        var previousClipboard = Clipboard.Current;
        Exception? failure = null;
        try
        {
            window.EnsureInitialized();
            var content = window.Content
                ?? throw new InvalidOperationException("Window.Content was not set.");

            var textPainter = app.Services.GetService<ITextPainter>();
            var surfaceFactory = app.Services.GetService<ISurfaceFactory>()
                ?? throw new InvalidOperationException(
                    "No ISurfaceFactory registered. Add a render backend first (e.g. AppBuilder.UseVello()).");

            var width = window.Width > 0 ? (int)window.Width : 960;
            var height = window.Height > 0 ? (int)window.Height : 640;
            win = new Win32Window(window.Title, width, height, quitOnDestroy: false);
            window.LiveUpdater = new Win32WindowLiveUpdater(win);
            presenter = surfaceFactory.Create(
                win.Handle, Math.Max(win.ClientWidth, 1), Math.Max(win.ClientHeight, 1));

            Console.WriteLine(presenter.Description);
            Console.WriteLine($"vsync={presenter.IsVsync}, max-fps={window.MaxFramesPerSecond}");

            // 主题解析：窗口显式 > 应用默认（UseTheme）> 平台默认；IHostTheme 擦除后还原为 ElementTheme。
            var theme = window.Theme ?? app.DefaultTheme ?? ElementTheme.Light;
            host = new WindowHost(win, presenter, content, new WindowHostOptions
            {
                Theme = theme,
                TextPainter = textPainter,
                SurfaceFactory = surfaceFactory,
                PopupWindowFactory = new Win32PopupWindowFactory(win),
                MaxFramesPerSecond = window.MaxFramesPerSecond,
                AlwaysRender = window.AlwaysRender,
                AlwaysShowFocusVisuals = window.AlwaysShowFocusVisuals,
                CloseOnEscape = window.CloseOnEscape,
                IdleSleepMilliseconds = window.IdleSleepMilliseconds,
            }, window);
            // The Win32 host's legacy constructor installs its clipboard immediately.
            // Let the shared session capture the owner's clipboard before switching it.
            var clipboard = Clipboard.Current;
            Clipboard.Current = previousClipboard;
            session = app.AttachWindow(window, host, win.Close, clipboard, modal);
            host.FrameTicked += (_, e) =>
            {
                if (e.Iteration == 1)
                    Console.WriteLine($"first frame: {presenter.Width}x{presenter.Height} px, {host.Root.Descendants().Count()} elements");
            };

            var rendered = host.Run(window.MaxTicks);
            Console.WriteLine($"ticks={host.Ticks} rendered={rendered} skipped={host.SkippedFrames} " +
                              $"logical={win.LogicalClientSize.Width:F0}x{win.LogicalClientSize.Height:F0} scale={win.ScaleFactor:F2}");
            return 0;
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            List<Exception>? cleanupFailures = null;
            void CleanUp(IDisposable? resource)
            {
                try { resource?.Dispose(); }
                catch (Exception error) { (cleanupFailures ??= new()).Add(error); }
            }

            // UI resource hooks run while the host, rendering surface and native window
            // are still usable, including when platform construction only partly succeeded.
            CleanUp(window);
            window.LiveUpdater = null;
            CleanUp(host);
            CleanUp(presenter);
            CleanUp(win);
            CleanUp(session);
            if (session is null) Clipboard.Current = previousClipboard;
            if (cleanupFailures is not null)
            {
                if (failure is not null) cleanupFailures.Insert(0, failure);
                throw new AggregateException("Window lifetime cleanup failed.", cleanupFailures);
            }
        }
    }
}
