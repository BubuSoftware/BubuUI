using EclipseUI.Text.Shaping;

namespace Gallery;

/// <summary>字体创建（平台无关）：优先西文字体，回落 CJK；都没有时由调用方降级为只测量。</summary>
public static class TextShaperFactory
{
    /// <summary>尝试创建 HarfBuzz shaper；失败返回 null（调用方使用 MeasuringTextPainter）。</summary>
    public static HarfBuzzTextShaper? TryCreate()
    {
        var candidates = new[]
        {
            @"C:\Windows\Fonts\segoeui.ttf",
            @"C:\Windows\Fonts\msyh.ttc",
            @"C:\Windows\Fonts\arial.ttf",
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var shaper = new HarfBuzzTextShaper(path);
                Console.WriteLine($"font: {path}");
                return shaper;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"font failed: {path}: {ex.Message}");
            }
        }

        Console.WriteLine("font: none (measuring-only text painter)");
        return null;
    }
}
