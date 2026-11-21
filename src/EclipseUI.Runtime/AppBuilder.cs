using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace EclipseUI;

/// <summary>
/// 应用构建器（D101，Avalonia 式启动形态）：
/// <code>
/// AppBuilder.Configure&lt;GalleryApp&gt;()
///     .UseWindows()                       // 平台后端（Platform.Windows 扩展）
///     .UseVello()                         // 渲染表面（Rendering.Vello 扩展）
///     .UseHarfBuzzText()                  // 文本管线（Text.Shaping 扩展）
///     .UseTheme(ElementTheme.Dark)
///     .UseStyles(StyleRegistry.Instance, StyleBootstrap.Apply)
///     .ConfigureServices(s => s.AddSingleton(options))
///     .Start(args);
/// </code>
/// 装配顺序：<see cref="UseSetup"/> 注册的平台服务按调用顺序先入容器，随后用户
/// <see cref="ConfigureServices"/> 最后入容器——MEDI 同类型多注册时最后注册优先，
/// 用户可覆盖框架默认。<see cref="Build"/> 可单独调用（无头/测试/离屏形态），
/// <see cref="Start"/> 在其上追加生命周期运行（需要平台已注册 <see cref="IApplicationLifetime"/>）。
///
/// 依赖方向约束：本类型在 Runtime，不引用 Rendering/平台包——主题经 <see cref="IHostTheme"/>
/// 标记接口、样式经 <see cref="IStyleProvider"/> 泛型传递，具体类型由调用方与扩展包提供。
/// </summary>
public sealed class AppBuilder
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    private Type? _appType;
    private readonly List<IPlatformSetup> _setups = [];
    private Action<IServiceCollection>? _configureServices;
    private IHostTheme? _defaultTheme;
    private IStyleProvider? _styleProvider;
    private Action? _styleWire;

    private AppBuilder()
    {
    }

    /// <summary>声明应用类型（需无参构造；实例在 <see cref="Build"/> 时创建）。</summary>
    public static AppBuilder Configure<TApp>() where TApp : Application, new()
        => new() { _appType = typeof(TApp) };

    /// <summary>注册用户服务（最后执行，可覆盖平台注册）。</summary>
    public AppBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        _configureServices += configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>
    /// 构建期默认主题：窗口未显式指定 <see cref="Window.Theme"/> 时生效。
    /// 直接传具体主题实例（如 <c>ElementTheme.Dark</c>，隐式转 <see cref="IHostTheme"/>）。
    /// </summary>
    public AppBuilder UseTheme(IHostTheme theme)
    {
        _defaultTheme = theme ?? throw new ArgumentNullException(nameof(theme));
        return this;
    }

    /// <summary>
    /// 装配 Class 样式表（S-B）：接线 <see cref="Styling.Provider"/>（晚装配的
    /// <c>PendingClasses</c> 在此补应用），再执行注册回调。直接传具体注册表实例
    /// （如 <c>StyleRegistry.Instance</c>）。
    /// </summary>
    public AppBuilder UseStyles<TProvider>(TProvider provider, Action<TProvider> configure)
        where TProvider : class, IStyleProvider
    {
        if (provider is null) throw new ArgumentNullException(nameof(provider));
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        _styleProvider = provider;
        _styleWire = () => configure(provider);
        return this;
    }

    /// <summary>平台/后端扩展点（<c>UseWindows()/UseVello()/UseHarfBuzzText()</c> 等内部经此注册）。</summary>
    public AppBuilder UseSetup(IPlatformSetup setup)
    {
        if (setup is null) throw new ArgumentNullException(nameof(setup));
        _setups.Add(setup);
        return this;
    }

    /// <summary>
    /// 执行装配并返回应用实例：构建容器 → 创建 app → 接线主题/样式 → 触发
    /// <see cref="Application.OnFrameworkInitializationCompleted"/>。不运行生命周期
    /// （无头/离屏/测试形态）。
    /// </summary>
    public Application Build()
    {
        if (_appType is null)
            throw new InvalidOperationException("Call Configure<TApp>() first.");

        var services = new ServiceCollection();
        foreach (var setup in _setups)
            setup.RegisterServices(services);
        _configureServices?.Invoke(services);

        var provider = services.BuildServiceProvider();

        var app = (Application)Activator.CreateInstance(_appType)!;
        app.Services = provider;
        app.DefaultTheme = _defaultTheme;

        // 样式装配（S-B）：Provider 先接线（晚装配补应用），再注册类定义。
        if (_styleProvider is not null)
        {
            Styling.Provider = _styleProvider;
            _styleWire?.Invoke();
        }

        app.OnFrameworkInitializationCompleted();
        return app;
    }

    /// <summary>
    /// 装配并运行桌面生命周期（阻塞到最后一个应用窗口关闭，返回退出码）。
    /// 未注册 <see cref="IApplicationLifetime"/>（缺 <c>UseWindows()</c> 之类平台装配）时 fail-fast。
    /// </summary>
    public int Start(string[] args)
    {
        var app = Build();
        var lifetime = app.Services.GetService<IApplicationLifetime>()
            ?? throw new InvalidOperationException(
                "No IApplicationLifetime registered. Add the platform setup first (e.g. AppBuilder.UseWindows()).");
        return lifetime.Run(app, args ?? []);
    }
}
