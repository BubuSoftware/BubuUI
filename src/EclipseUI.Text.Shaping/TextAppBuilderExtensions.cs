using EclipseUI;
using EclipseUI.Elements;
using EclipseUI.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace EclipseUI.Text.Shaping;

/// <summary>HarfBuzz 文本管线选项（D101）：字体候选路径（按序尝试，第一个可加载者生效）。</summary>
public sealed class HarfBuzzTextOptions
{
    /// <summary>字体候选路径；全部失败时宿主回落 <see cref="MeasuringTextPainter"/>（只测量）。</summary>
    public IReadOnlyList<string> FontPaths { get; set; } =
    [
        @"C:\Windows\Fonts\segoeui.ttf",
        @"C:\Windows\Fonts\msyh.ttc",
        @"C:\Windows\Fonts\arial.ttf",
    ];
}

/// <summary>
/// AppBuilder 扩展（D101）：注册 HarfBuzz shaping 文本管线——shape 一次、布局与绘制共用同一进距
/// （M1 口径）。加载成功时注册 <see cref="ITextPainter"/> 并把 <see cref="TextMeasurer.Current"/>
/// 指向同一 painter；失败（无候选字体）时不注册，宿主回落只测量路径。
/// </summary>
public static class TextAppBuilderExtensions
{
    /// <summary>使用 HarfBuzz 文本管线（字体候选可用 <see cref="HarfBuzzTextOptions.FontPaths"/> 覆盖）。</summary>
    public static AppBuilder UseHarfBuzzText(this AppBuilder builder, Action<HarfBuzzTextOptions>? configure = null)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        var options = new HarfBuzzTextOptions();
        configure?.Invoke(options);
        return builder.UseSetup(new HarfBuzzTextSetup(options));
    }

    private sealed class HarfBuzzTextSetup(HarfBuzzTextOptions options) : IPlatformSetup
    {
        public void RegisterServices(IServiceCollection services)
        {
            var shaper = TryCreateShaper(options);
            if (shaper is null)
            {
                Console.WriteLine("text: no usable font found, falling back to measuring-only painter");
                return;
            }

            // shaper 注册为独立单例：容器释放时随之关闭字体句柄。
            services.AddSingleton(shaper);
            services.AddSingleton<ITextPainter>(sp =>
            {
                var painter = new HarfBuzzTextPainter(sp.GetRequiredService<HarfBuzzTextShaper>());
                TextMeasurer.Current = painter; // 布局与绘制共用同一 shaping 缓存
                return painter;
            });
        }

        private static HarfBuzzTextShaper? TryCreateShaper(HarfBuzzTextOptions options)
        {
            foreach (var path in options.FontPaths)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    Console.WriteLine($"font: {path}");
                    return new HarfBuzzTextShaper(path);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"font failed: {path}: {ex.Message}");
                }
            }

            return null;
        }
    }
}
