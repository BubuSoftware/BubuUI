using EclipseUI.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace EclipseUI.Platform.iOS;

public static class IosAppBuilderExtensions
{
    /// <summary>Registers the scene-driven iOS lifetime and native text painter.</summary>
    public static AppBuilder UseiOS(this AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseSetup(new IosPlatformSetup());
    }

    private sealed class IosPlatformSetup : IPlatformSetup
    {
        public void RegisterServices(IServiceCollection services)
        {
            services.AddSingleton<IActivityApplicationLifetime>(_ => new SceneLifetime());
            services.AddSingleton<ITextPainter>(_ => new CoreTextPainter());
        }
    }

    private sealed class SceneLifetime : IActivityApplicationLifetime
    {
        public Func<Element>? MainViewFactory { get; set; }
    }
}
