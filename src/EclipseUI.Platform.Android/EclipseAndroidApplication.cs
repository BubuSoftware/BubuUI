using Android.Runtime;

namespace EclipseUI.Platform.Android;

/// <summary>Process-wide application and services, shared across activity recreation.</summary>
public abstract class EclipseAndroidApplication : global::Android.App.Application, IWindowHost
{
    private readonly List<EclipseView> _views = [];

    protected EclipseAndroidApplication(nint handle, JniHandleOwnership ownership) : base(handle, ownership) { }

    public Application EclipseApplication { get; private set; } = null!;
    protected abstract AppBuilder CreateAppBuilder();

    public override void OnCreate()
    {
        base.OnCreate();
        EclipseApplication = CreateAppBuilder().Build();
        EclipseApplication.Host = this;
    }

    internal void Attach(EclipseView view) => _views.Add(view);
    internal void Detach(EclipseView view) => _views.Remove(view);

    void IWindowHost.SetTheme(IHostTheme theme)
    {
        foreach (var view in _views.ToArray()) view.SetTheme(theme);
    }
}

public abstract class EclipseAndroidApplication<TApp> : EclipseAndroidApplication
    where TApp : Application, new()
{
    protected EclipseAndroidApplication(nint handle, JniHandleOwnership ownership) : base(handle, ownership) { }

    protected override AppBuilder CreateAppBuilder()
        => CustomizeAppBuilder(AppBuilder.Configure<TApp>().UseAndroid());

    protected virtual AppBuilder CustomizeAppBuilder(AppBuilder builder) => builder;
}
