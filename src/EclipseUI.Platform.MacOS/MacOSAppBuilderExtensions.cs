using EclipseUI;
using EclipseUI.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace EclipseUI.Platform.MacOS;

/// <summary>
/// AppBuilder 扩展（D101）：注册 MacOS 桌面生命周期——把 <see cref="Application.MainWindow"/>
/// 的描述材料化为 MacOS 窗口 + 表面（<c>ISurfaceFactory</c>，另经 <c>UseVello()</c> 注册）+
/// <see cref="WindowHost"/> 帧循环，跑至窗口关闭。
/// </summary>
public static class MacOSAppBuilderExtensions
{
    /// <summary>使用 SDL3 Cocoa 窗口与 macOS 桌面帧循环；Start 必须在进程主线程调用。</summary>
    public static AppBuilder UseMacOS(this AppBuilder builder)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        return builder.UseSetup(new MacOSPlatformSetup());
    }

    private sealed class MacOSPlatformSetup : IPlatformSetup
    {
        public void RegisterServices(IServiceCollection services)
        {
            services.AddSingleton<MacOSDesktopLifetime>();
            services.AddSingleton<IApplicationLifetime>(provider => provider.GetRequiredService<MacOSDesktopLifetime>());
            services.AddSingleton<IWindowLifetime>(provider => provider.GetRequiredService<MacOSDesktopLifetime>());
        }
    }
}

/// <summary>
/// 桌面生命周期（D101）：窗口描述 → MacOSWindow + ISurfacePresenter + WindowHost。
/// 诊断输出沿用原画廊宿主（backend/首帧/统计）；退出时先释放 UI 对象，
/// 再按 host → presenter → 原生窗口顺序释放平台资源。
/// </summary>
internal sealed class MacOSDesktopLifetime : IApplicationLifetime, IWindowLifetime
{
    public int Run(Application app, string[] args)
    {
        var window = app.MainWindow
            ?? throw new InvalidOperationException(
                "Application.MainWindow was not set. Create it in OnFrameworkInitializationCompleted().");
        return RunWindow(app, window);
    }

    public int RunWindow(Application app, Window window, bool modal = false)
        => RunPreparedWindow(app, window, null, modal);

    internal static int RunPreparedWindow(Application app, Window window, MacOSWindow? preparedWindow, bool modal)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.ValidateWindow(window);
        MacOSWindow? win = null;
        ISurfacePresenter? presenter = null;
        WindowHost? host = null;
        IDisposable? session = null;
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
            win = preparedWindow ?? new MacOSWindow(window.Title, width, height);
            window.LiveUpdater = win;
            if (preparedWindow is not null)
            {
                win.SetTitle(window.Title);
                win.SetClientSize(width, height);
            }
            // WindowHost applies the native appearance before its Run method shows the window.
            Console.WriteLine($"window: SDL3/{win.VideoDriver}");
            presenter = surfaceFactory.Create(
                win.SurfaceHandle, Math.Max(win.ClientWidth, 1), Math.Max(win.ClientHeight, 1));

            Console.WriteLine(presenter.Description);
            Console.WriteLine($"vsync={presenter.IsVsync}, max-fps={window.MaxFramesPerSecond}");

            // 主题解析：窗口显式 > 应用默认（UseTheme）> 平台默认；IHostTheme 擦除后还原为 ElementTheme。
            var theme = window.Theme ?? app.DefaultTheme ?? ElementTheme.Light;
            host = new WindowHost(win, presenter, content, new WindowHostOptions
            {
                Theme = theme,
                TextPainter = textPainter,
                SurfaceFactory = surfaceFactory,
                PopupWindowFactory = new MacOSPopupWindowFactory(win),
                MaxFramesPerSecond = window.MaxFramesPerSecond,
                AlwaysRender = window.AlwaysRender,
                AlwaysShowFocusVisuals = window.AlwaysShowFocusVisuals,
                CloseOnEscape = window.CloseOnEscape,
                IdleSleepMilliseconds = window.IdleSleepMilliseconds,
            }, window);
            session = app.AttachWindow(window, host, win.Close, win.Clipboard, modal);
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
            if (preparedWindow is null) CleanUp(win);
            CleanUp(session);
            if (cleanupFailures is not null)
            {
                if (failure is not null) cleanupFailures.Insert(0, failure);
                throw new AggregateException("Window lifetime cleanup failed.", cleanupFailures);
            }
        }
    }
}
