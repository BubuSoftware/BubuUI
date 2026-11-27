using Android.Runtime;

namespace BubuUI.Platform.Android;

/// <summary>Process-wide application and services, shared across activity recreation.</summary>
public abstract class BubuAndroidApplication : global::Android.App.Application, IWindowHost
{
    private readonly List<BubuView> _views = [];

    protected BubuAndroidApplication(nint handle, JniHandleOwnership ownership) : base(handle, ownership) { }

    public Application BubuApplication { get; private set; } = null!;
    protected abstract AppBuilder CreateAppBuilder();

    public override void OnCreate()
    {
        base.OnCreate();
        BubuApplication = CreateAppBuilder().Build();
        BubuApplication.Host = this;
    }

    internal void Attach(BubuView view) => _views.Add(view);
    internal void Detach(BubuView view) => _views.Remove(view);

    void IWindowHost.SetTheme(IHostTheme theme)
    {
        foreach (var view in _views.ToArray()) view.SetTheme(theme);
    }
}

public abstract class BubuAndroidApplication<TApp> : BubuAndroidApplication
    where TApp : Application, new()
{
    protected BubuAndroidApplication(nint handle, JniHandleOwnership ownership) : base(handle, ownership) { }

    protected override AppBuilder CreateAppBuilder()
        => CustomizeAppBuilder(AppBuilder.Configure<TApp>().UseAndroid());

    protected virtual AppBuilder CustomizeAppBuilder(AppBuilder builder) => builder;
}
