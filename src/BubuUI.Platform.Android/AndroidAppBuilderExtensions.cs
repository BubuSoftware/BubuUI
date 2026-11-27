using Microsoft.Extensions.DependencyInjection;

namespace BubuUI.Platform.Android;

public static class AndroidAppBuilderExtensions
{
    /// <summary>Registers the OS-driven activity lifetime; use Build(), not Start().</summary>
    public static AppBuilder UseAndroid(this AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseSetup(new AndroidPlatformSetup());
    }

    private sealed class AndroidPlatformSetup : IPlatformSetup
    {
        public void RegisterServices(IServiceCollection services)
            => services.AddSingleton<IActivityApplicationLifetime, AndroidApplicationLifetime>();
    }

    private sealed class AndroidApplicationLifetime : IActivityApplicationLifetime
    {
        public Func<Element>? MainViewFactory { get; set; }
    }
}
