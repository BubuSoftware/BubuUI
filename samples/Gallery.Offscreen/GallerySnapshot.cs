using EclipseUI;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;

namespace Gallery;

/// <summary>离屏快照（平台无关）：走与窗口相同的显示列表管线，输出 PNG 并打印内容哈希。</summary>
public static class GallerySnapshot
{
    public static void SavePng(
        Element root, Size logicalSize, double scale, string path,
        ElementTheme theme, ITextPainter textPainter)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var painter = new ElementPainter(theme, textPainter);
        var list = painter.Build(root, logicalSize, scale);
        var errors = list.Validate();
        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"display list invalid: {string.Join("; ", errors)}");
            return;
        }

        var widthPx = Math.Max(1, (int)Math.Round(logicalSize.Width * scale));
        var heightPx = Math.Max(1, (int)Math.Round(logicalSize.Height * scale));

        using var backend = new VelloCpuBackend(new VelloBackendOptions { UseCpu = true, ValidateDisplayList = true });
        var target = new RenderTarget(widthPx, heightPx);
        backend.Render(list, target);
        PngEncoder.Write(path, target);

        Console.WriteLine($"shot: {path} ({widthPx}x{heightPx} px, {list.Commands.Count} commands, hash={target.ContentHash():X16})");
    }
}
