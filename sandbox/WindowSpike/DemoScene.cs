using BubuUI;
using BubuUI.Rendering;
using BubuUI.Rendering.Vello;
using BubuUI.Text;
using BubuUI.Text.Shaping;

namespace WindowSpike;

/// <summary>场景分块开关：用于逐项测量 GPU 成本（阴影/图层是全屏开销的大头）。</summary>
[Flags]
public enum SceneParts
{
    None = 0,
    Background = 1 << 0,
    Text = 1 << 1,
    Cards = 1 << 2,
    Shadows = 1 << 3,
    Spinner = 1 << 4,
    Strip = 1 << 5,
    Layers = 1 << 6,
    All = Background | Text | Cards | Shadows | Spinner | Strip | Layers,
}

/// <summary>
/// 一帧 UI 的快照：尺寸（设备无关单位）、时间、指针与交互状态。
/// 场景完全由状态决定，因此同一状态在 CPU 与 GPU 后端应产出等价像素。
/// </summary>
public readonly record struct FrameState(Size Size, double Time, Point Pointer, bool PointerInside, int ClickCount, string LastKey);

/// <summary>
/// G2 垂直切片的临时“元素树”：手写 display list，验证从场景到窗口像素的整条链路。
/// 后续 G2-03 会用真正的 scene graph 遍历替换 <see cref="Build"/>。
/// </summary>
public sealed class DemoScene : IDisposable
{
    private readonly string _fontDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts));

    private readonly Dictionary<(string Path, int Face, double Size, string Text), ShapedBuffer> _shaped = new();
    private readonly Dictionary<(string Path, int Face), (HarfBuzzTextShaper Shaper, FontFace Face)> _fonts = new();

    public DisplayList Build(FrameState state, SceneParts parts = SceneParts.All)
    {
        var width = state.Size.Width;
        var height = state.Size.Height;
        var list = new DisplayList(state.Size);
        var fonts = _fontDirectory;
        var arial = Path.Combine(fonts, "arial.ttf");
        var cjk = Path.Combine(fonts, "msyh.ttc");
        var hasLatin = File.Exists(arial) && parts.HasFlag(SceneParts.Text);

        // 背景：斜向渐变 + 顶部高光，验证多 stop 渐变与全屏绘制。
        if (parts.HasFlag(SceneParts.Background))
        {
            list.Add(new FillRect(new Rect(0, 0, width, height), new LinearGradient(
                new Point(0, 0),
                new Point(width * 0.35, height),
                new[]
                {
                    GradientStop.At(0, new Color(18, 22, 36)),
                    GradientStop.At(0.55, new Color(28, 34, 58)),
                    GradientStop.At(1, new Color(52, 26, 60)),
                })));
        }

        // 页头
        if (hasLatin)
        {
            list.Add(new DrawGlyphs(Font(arial), Shape(arial, "BubuUI · G2 surface slice", 26),
                new Point(40, 64), new Color(236, 240, 250)));
            list.Add(new DrawGlyphs(Font(arial), Shape(arial, "Win32 host + Vello GPU present", 14),
                new Point(40, 90), new Color(140, 158, 190)));

            var note = $"frame {state.ClickCount} · pointer {(state.PointerInside ? "inside" : "outside")} · key {state.LastKey}";
            list.Add(new DrawGlyphs(Font(arial), Shape(arial, note, 12),
                new Point(40, 110), new Color(120, 136, 168)));
        }

        // 三张卡片：圆角 + 阴影 + 各自不同的画刷
        var cardTop = 140.0;
        var cardHeight = 180.0;
        var cardWidth = Math.Max(160.0, (width - 40 * 2 - 24 * 2) / 3);
        if (parts.HasFlag(SceneParts.Cards))
        {
            for (int i = 0; i < 3; i++)
            {
                var x = 40 + i * (cardWidth + 24);
                var rect = new Rect(x, cardTop, cardWidth, cardHeight);
                if (parts.HasFlag(SceneParts.Shadows))
                    list.Add(new DropShadow(rect, 18, 22, new Point(0, 6), new Color(0, 0, 0, 150)));

                list.Add(new FillRoundedRect(rect, 14, i switch
                {
                    0 => new SolidColorBrush(new Color(248, 249, 252)),
                    1 => new LinearGradient(new Point(x, cardTop), new Point(x + cardWidth, cardTop + cardHeight), new[]
                    {
                        GradientStop.At(0, new Color(255, 196, 92)),
                        GradientStop.At(1, new Color(226, 92, 60)),
                    }),
                    _ => new RadialGradient(new Point(x + cardWidth / 2, cardTop + cardHeight / 2), (float)(cardWidth * 0.7), new[]
                    {
                        GradientStop.At(0, new Color(120, 226, 255)),
                        GradientStop.At(1, new Color(28, 92, 150)),
                    }),
                }));

                if (!hasLatin) continue;

                var title = i switch { 0 => "Solid", 1 => "Linear", _ => "Radial" };
                var ink = i == 0 ? new Color(26, 32, 48) : new Color(255, 255, 255);
                list.Add(new DrawGlyphs(Font(arial), Shape(arial, title, 20), new Point(x + 20, cardTop + 46), ink));
                list.Add(new DrawGlyphs(Font(arial), Shape(arial, "rounded · shadow · clipped", 12),
                    new Point(x + 20, cardTop + 68), i == 0 ? new Color(110, 120, 140) : new Color(255, 255, 255, 190)));
            }
        }

        // 交互动画：旋转方块（验证 transform 栈）+ 呼吸圆点（验证 opacity 图层）
        var spinnerCenter = new Point(width - 110, 84);
        var angle = state.Time * 1.6;
        if (parts.HasFlag(SceneParts.Spinner))
        {
            for (int i = 0; i < 4; i++)
            {
                var rotation = Transform.RotationAround(angle + i * Math.PI / 8, spinnerCenter);
                list.Add(new PushTransform(rotation));
                list.Add(new FillRoundedRect(
                    new Rect(spinnerCenter.X - 34, spinnerCenter.Y - 8, 68, 16), 8,
                    new Color(140, 200, 255, (byte)(90 + i * 40))));
                list.Add(new PopTransform());
            }
        }

        if (parts.HasFlag(SceneParts.Layers))
        {
            var pulse = 0.35 + 0.25 * Math.Sin(state.Time * 3);
            list.Add(new PushLayer(pulse));
            list.Add(new FillEllipse(
                new Rect(spinnerCenter.X - 44, spinnerCenter.Y - 44, 88, 88),
                new Color(255, 120, 190)));
            list.Add(new PopLayer());
        }

        // 裁剪区域：内容越界不溢出
        var strip = new Rect(40, cardTop + cardHeight + 40, width - 80, 56);
        if (parts.HasFlag(SceneParts.Strip))
        {
            list.Add(new PushClip(strip));
            list.Add(new FillRect(strip, new Color(255, 255, 255, 12)));
            var offset = (state.Time * 90) % (strip.Width + 120);
            list.Add(new FillRoundedRect(new Rect(strip.X + offset - 120, strip.Y + 12, 120, 32), 16,
                new Color(120, 220, 255, 200)));
            list.Add(new PopClip());
        }

        // 虚线 + CJK 文本，验证中文字体与 dash
        var dividerY = strip.Bottom + 28;
        list.Add(new StrokePath(PathGeometry.Line(new Point(40, dividerY), new Point(width - 40, dividerY)),
            new Color(255, 255, 255, 60), 1.5, new[] { 8d, 6d }));

        if (hasLatin && File.Exists(cjk))
        {
            list.Add(new DrawGlyphs(Font(cjk, 0), Shape(cjk, 0, "窗口像素链路：布局 → 场景 → display list → GPU", 16),
                new Point(40, dividerY + 30), new Color(200, 214, 240)));
        }

        // 底部状态
        if (hasLatin)
        {
            list.Add(new DrawGlyphs(Font(arial), Shape(arial, "resize / DPI / click / key via real Win32 messages", 12),
                new Point(40, height - 24), new Color(120, 136, 168)));
        }

        return list;
    }

    private FontFace Font(string path, int faceIndex = 0)
        => GetFont(path, faceIndex).Face;

    private ShapedBuffer Shape(string path, string text, double size, int faceIndex = 0)
        => Shape(path, faceIndex, text, size);

    private ShapedBuffer Shape(string path, int faceIndex, string text, double size)
    {
        if (_shaped.TryGetValue((path, faceIndex, size, text), out var cached)) return cached;
        var shaped = GetFont(path, faceIndex).Shaper.Shape(text, size);
        _shaped[(path, faceIndex, size, text)] = shaped;
        return shaped;
    }

    private (HarfBuzzTextShaper Shaper, FontFace Face) GetFont(string path, int faceIndex)
    {
        if (_fonts.TryGetValue((path, faceIndex), out var cached)) return cached;
        var shaper = new HarfBuzzTextShaper(path, faceIndex);
        var face = FontFace.Load(path, faceIndex);
        var entry = (shaper, face);
        _fonts[(path, faceIndex)] = entry;
        return entry;
    }

    public void Dispose()
    {
        foreach (var (shaper, _) in _fonts.Values) shaper.Dispose();
        _fonts.Clear();
        _shaped.Clear();
    }
}
