using EclipseUI.Text.Shaping;

namespace Gallery;

/// <summary>主字体创建：按候选顺序加载；混合文字的中文/emoji 回退由 HarfBuzzTextPainter 按字素处理。</summary>
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
