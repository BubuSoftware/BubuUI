using System.Diagnostics;
using BubuUI;
using BubuUI.Rendering;
using BubuUI.Rendering.Vello;
using BubuUI.Text;
using BubuUI.Text.Shaping;
using RenderSpike;

// G1 渲染 gate 的可运行入口。
//   dotnet run --project sandbox/RenderSpike -c Release          # 输出 PNG
//   dotnet run --project sandbox/RenderSpike -c Release -- bench # 性能表

var mode = args.Length > 0 && args[0] is "bench" or "gpu" ? args[0] : "render";
var outputDirectory = args.Length > 1
    ? args[1]
    : args.Length == 1 && mode == "render"
        ? args[0]
        : Path.Combine(Environment.CurrentDirectory, "out");
Directory.CreateDirectory(outputDirectory);

if (mode == "bench")
{
    Bench.Run(outputDirectory);
    return;
}

using var backend = new VelloCpuBackend(new VelloBackendOptions
{
    UseCpu = true,
    ValidateDisplayList = true,
});

WriteBasic(outputDirectory, backend);
WriteDemo(outputDirectory, backend);
WriteFeatures(outputDirectory, backend);

static void WriteBasic(string directory, IRenderBackend backend)
{
    var target = new RenderTarget(256, 256);
    var list = new DisplayList(new Size(256, 256))
        .Add(new FillRect(new Rect(24, 24, 208, 208), new Color(220, 30, 30)));

    backend.Render(list, target);

    var path = Path.Combine(directory, "basic.png");
    PngEncoder.Write(path, target);
    var center = target.GetPixel(128, 128);
    Console.WriteLine($"{Path.GetFileName(path)}: 256x256, center={center}, hash={target.ContentHash():X16}");
}

static void WriteDemo(string directory, IRenderBackend backend)
{
    const int width = 640;
    const int height = 400;
    var target = new RenderTarget(width, height);
    var list = new DisplayList(new Size(width, height));

    // 背景：线性渐变
    list.Add(new FillRect(
        new Rect(0, 0, width, height),
        new LinearGradient(
            new Point(0, 0),
            new Point(width, height),
            new[]
            {
                GradientStop.At(0, new Color(24, 32, 56)),
                GradientStop.At(1, new Color(64, 24, 72)),
            })));

    // 径向渐变卡片 + 圆角
    list.Add(new FillRoundedRect(new Rect(32, 32, 260, 160), 20,
        new RadialGradient(new Point(162, 112), 150, new[]
        {
            GradientStop.At(0, new Color(255, 214, 102)),
            GradientStop.At(1, new Color(220, 96, 32)),
        })));

    // 描边矩形 + 虚线
    list.Add(new StrokeRect(new Rect(32, 216, 260, 56), new Color(255, 255, 255, 160), 2));
    list.Add(new StrokePath(
        PathGeometry.Line(new Point(32, 292), new Point(292, 292)),
        new Color(120, 220, 255),
        3,
        new[] { 12d, 6d }));

    // 裁剪 + 图层透明度
    list.Add(new PushClip(new Rect(320, 32, 280, 160)));
    list.Add(new FillRect(new Rect(300, 20, 320, 200), new Color(0, 200, 255)));
    for (int i = 0; i < 6; i++)
        list.Add(new FillRect(new Rect(330 + i * 44, 60, 24, 100), new Color(20, 20, 30, 120)));
    list.Add(new PopClip());

    list.Add(new PushLayer(0.65));
    list.Add(new FillEllipse(new Rect(320, 212, 130, 60), new Color(255, 80, 160)));
    list.Add(new FillEllipse(new Rect(400, 240, 130, 60), new Color(80, 255, 160)));
    list.Add(new PopLayer());

    // 图像缩放：棋盘测试图
    var checker = ImageHandle.Checkerboard(16, 16, 4, new Color(255, 255, 255), new Color(40, 60, 90));
    list.Add(new DrawImage(checker, new Rect(520, 220, 88, 88), 0.9));

    // 文本：HarfBuzz shaping → glyph run
    var fontPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");
    if (File.Exists(fontPath))
    {
        using var shaper = new HarfBuzzTextShaper(fontPath);
        var font = FontFace.Load(fontPath);
        var title = shaper.Shape("BubuUI render gate", 28);
        var body = shaper.Shape("HarfBuzz shaping -> Vello glyph run", 16);
        list.Add(new DrawGlyphs(font, title, new Point(32, 360), new Color(255, 255, 255)));
        list.Add(new DrawGlyphs(font, body, new Point(32, 384), new Color(180, 200, 255)));
        Console.WriteLine($"text: '{title.Text}' width={title.Width:F2}px, {title.Glyphs.Count} glyphs");
    }
    else
    {
        Console.WriteLine("text: arial.ttf not found, glyph run skipped");
    }

    backend.Render(list, target);

    var path = Path.Combine(directory, "demo.png");
    PngEncoder.Write(path, target);
    Console.WriteLine($"{Path.GetFileName(path)}: {width}x{height}, {list.Commands.Count} commands, hash={target.ContentHash():X16}");
}

// 功能矩阵：把 G1-07 的每一项画到一张图上，便于人工核对 + 报告截图。
static void WriteFeatures(string directory, IRenderBackend backend)
{
    const int width = 900;
    const int height = 520;
    var target = new RenderTarget(width, height);
    var list = new DisplayList(new Size(width, height));
    var fonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));

    list.Add(new FillRect(new Rect(0, 0, width, height), new Color(28, 30, 38)));

    // 1. 阴影
    list.Add(new DropShadow(new Rect(40, 40, 160, 90), 12, 24, new Point(0, 8), new Color(0, 0, 0, 170)));
    list.Add(new FillRoundedRect(new Rect(40, 40, 160, 90), 12, new Color(250, 250, 250)));

    // 2. 三种渐变
    list.Add(new FillRect(new Rect(240, 40, 180, 90), new LinearGradient(
        new Point(240, 40), new Point(420, 130),
        new[] { GradientStop.At(0, new Color(255, 90, 90)), GradientStop.At(1, new Color(90, 90, 255)) })));
    list.Add(new FillRect(new Rect(440, 40, 180, 90), new RadialGradient(
        new Point(530, 85), 90,
        new[] { GradientStop.At(0, Color.White), GradientStop.At(1, new Color(20, 120, 80)) })));
    list.Add(new FillRect(new Rect(640, 40, 180, 90), new SweepGradient(
        new Point(730, 85), 0, Math.PI * 2,
        new[]
        {
            GradientStop.At(0, new Color(255, 0, 0)),
            GradientStop.At(0.33, new Color(0, 255, 0)),
            GradientStop.At(0.66, new Color(0, 0, 255)),
            GradientStop.At(1, new Color(255, 0, 0)),
        })));

    // 3. 虚线 / 圆角 / 描边
    list.Add(new StrokePath(
        PathGeometry.Line(new Point(40, 180), new Point(420, 180)),
        new Color(120, 220, 255), 4, new[] { 16d, 8d }));
    list.Add(new StrokeRect(new Rect(40, 200, 120, 60), new Color(255, 200, 120), 3));
    list.Add(new FillRoundedRect(new Rect(180, 200, 120, 60), 24, new Color(120, 255, 200)));

    // 4. 裁剪 + 混合
    list.Add(new PushClip(new Rect(320, 200, 120, 60)));
    list.Add(new FillRect(new Rect(320, 200, 120, 60), new Color(60, 60, 90)));
    list.Add(new PushLayer(0.7, LayerBlend.Screen));
    list.Add(new FillEllipse(new Rect(340, 210, 90, 40), new Color(255, 100, 0)));
    list.Add(new PopLayer());
    list.Add(new PopClip());

    // 5. 图像缩放：4x 放大与 1024→96 缩小
    var small = ImageHandle.Checkerboard(8, 8, 2, Color.White, new Color(30, 60, 120));
    list.Add(new DrawImage(small, new Rect(470, 180, 80, 80)));
    list.Add(new DrawImage(small, new Rect(570, 180, 200, 80), 0.8));

    // 6. 旋转图像（3D 视口 readback 的最小替代）
    list.Add(new PushTransform(Transform.RotationAround(Math.PI / 8, new Point(820, 220))));
    list.Add(new DrawImage(small, new Rect(780, 180, 80, 80)));
    list.Add(new PopTransform());

    // 7. 彩色 emoji
    var emojiFont = Path.Combine(fonts, "seguiemj.ttf");
    if (File.Exists(emojiFont))
    {
        using var emojiShaper = new HarfBuzzTextShaper(emojiFont);
        list.Add(new DrawGlyphs(FontFace.Load(emojiFont), emojiShaper.Shape("😀🎉🚀", 48), new Point(40, 320), Color.White));
    }

    // 8. 文本：拉丁 + CJK（后者需显式选择字体，见 fallback 结论）
    var arial = Path.Combine(fonts, "arial.ttf");
    if (File.Exists(arial))
    {
        using var shaper = new HarfBuzzTextShaper(arial);
        var text = shaper.Shape("Latin text, shaping + rasterization", 24);
        list.Add(new DrawGlyphs(FontFace.Load(arial), text, new Point(40, 370), new Color(230, 230, 240)));
        Console.WriteLine($"features text width={text.Width:F2}px");
    }

    var cjkFont = Path.Combine(fonts, "msyh.ttc");
    if (File.Exists(cjkFont))
    {
        using var shaper = new HarfBuzzTextShaper(cjkFont, 0);
        var text = shaper.Shape("中文渲染：字形来自显式字体", 24);
        list.Add(new DrawGlyphs(FontFace.Load(cjkFont, 0), text, new Point(40, 410), new Color(230, 230, 240)));
    }

    backend.Render(list, target);

    var path = Path.Combine(directory, "features.png");
    PngEncoder.Write(path, target);
    Console.WriteLine($"{Path.GetFileName(path)}: {width}x{height}, {list.Commands.Count} commands, hash={target.ContentHash():X16}");
}
