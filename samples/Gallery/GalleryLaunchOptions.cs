using EclipseUI;

namespace Gallery;

/// <summary>
/// 画廊启动选项（D101）：CLI 解析结果作为服务进容器，<see cref="GalleryApp"/> 经 DI 消费——
/// 演示「应用配置走依赖注入」的典型形态。解析在平台壳（Gallery.Desktop），消费在应用层。
/// </summary>
public sealed record GalleryLaunchOptions
{
    public ThemeVariant Theme { get; init; } = ThemeVariant.System;
    public int Width { get; init; } = 720;

    public int Height { get; init; } = 960;
    public bool Legacy { get; init; }

    /// <summary>--frames N：最多跑 N 个循环 tick 后自动退出（默认跑到窗口关闭）。</summary>
    public int? Frames { get; init; }

    /// <summary>--shot path：离屏快照输出路径（与 --cpu 同走离屏路径）。</summary>
    public string? ShotPath { get; init; }

    /// <summary>--max-fps N：主动限帧；0 表示交给 vsync（60）。</summary>
    public int MaxFps { get; init; }

    /// <summary>vsync 开关（--no-vsync 关）。</summary>
    public bool Vsync { get; init; } = true;

    /// <summary>--always-render：忽略脏区判定，每帧都上屏。</summary>
    public bool AlwaysRender { get; init; }

    /// <summary>--cpu / --shot：走离屏 CPU 快照路径（不开窗口）。</summary>
    public bool Offscreen { get; init; }

    /// <summary>解析 CLI；未知参数抛 <see cref="ArgumentException"/>（调用方打印用法并返回退出码）。</summary>
    public static GalleryLaunchOptions Parse(string[] args)
    {
        int width = 720;
        int height = 960;
        bool legacy = false;
        int? frames = null;
        string? shotPath = null;
        bool vsync = true;
        bool useCpu = false;
        bool alwaysRender = false;
        int maxFps = 0;
        var theme = ThemeVariant.System;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--legacy":
                    legacy = true;
                    break;
                case "--theme" when i + 1 < args.Length:
                    theme = args[++i].ToLowerInvariant() switch
                    {
                        "system" => ThemeVariant.System, "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark,
                        _ => throw new ArgumentException("--theme must be system, light or dark."),
                    };
                    break;
                case "--frames" when i + 1 < args.Length:
                    frames = int.Parse(args[++i]);
                    break;
                case "--width" when i + 1 < args.Length:
                    width = int.Parse(args[++i]);
                    break;
                case "--height" when i + 1 < args.Length:
                    height = int.Parse(args[++i]);
                    break;
                case "--shot" when i + 1 < args.Length:
                    shotPath = args[++i];
                    break;
                case "--no-vsync":
                    vsync = false;
                    break;
                case "--max-fps" when i + 1 < args.Length:
                    maxFps = int.Parse(args[++i]);
                    break;
                case "--cpu":
                    useCpu = true;
                    break;
                case "--always-render":
                    alwaysRender = true;
                    break;
                default:
                    throw new ArgumentException($"unknown argument: {args[i]}");
            }
        }

        if (maxFps == 0 && vsync) maxFps = 60;

        return new GalleryLaunchOptions
        {
            Theme = theme,
            Legacy = legacy,
            Width = width,
            Height = height,
            Frames = frames,
            ShotPath = shotPath,
            MaxFps = maxFps,
            Vsync = vsync,
            AlwaysRender = alwaysRender,
            Offscreen = useCpu || shotPath is not null,
        };
    }
}
