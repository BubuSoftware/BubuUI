using System.Numerics;
using EclipseUI.Rendering;
using VelloSharp;

using VBrush = VelloSharp.Brush;
using VFillRule = VelloSharp.FillRule;
using VGradientStop = VelloSharp.GradientStop;
using VLayerBlend = VelloSharp.LayerBlend;
using VPathBuilder = VelloSharp.PathBuilder;
using VStrokeStyle = VelloSharp.StrokeStyle;

namespace EclipseUI.Rendering.Vello;

/// <summary>
/// <see cref="DisplayList"/> → <see cref="Scene"/> 的唯一翻译层，CPU 与 GPU 后端共用。
/// 所有栈式指令（transform / clip / layer）在托管侧维护，逐条指令携带合成后的矩阵。
/// 持有字体与图像句柄的 native 缓存，因此需要 Dispose。
/// </summary>
public sealed class VelloSceneBuilder : IDisposable
{
    private readonly Dictionary<FontFace, Font> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ImageHandle, Image> _images = new(ReferenceEqualityComparer.Instance);

    /// <summary>把整个 display list 追加到 <paramref name="scene"/>。scene 须由调用方负责 Reset/Dispose。</summary>
    public void Build(Scene scene, DisplayList list, Size targetSize)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(list);

        var transform = Transform.Identity;
        var stack = new Stack<Transform>();

        foreach (var command in list.Commands)
        {
            switch (command)
            {
                case PushTransform push:
                    stack.Push(transform);
                    transform = push.Transform * transform;
                    break;
                case PopTransform:
                    transform = stack.Count > 0 ? stack.Pop() : Transform.Identity;
                    break;

                case PushClip clip:
                    scene.PushLayer(RectPath(clip.Rect), NoBlend(), Matrix(transform), 1f);
                    break;

                case PushClipPath clipPath:
                    scene.PushLayer(ToPath(clipPath.Path), NoBlend(), Matrix(transform), 1f);
                    break;

                case PopClip:
                    scene.PopLayer();
                    break;

                case PushLayer layer:
                    // 图层覆盖整个 surface，clip 用绝对坐标，不受当前变换影响。
                    scene.PushLayer(
                        SurfacePath(targetSize),
                        MapBlend(layer.Blend),
                        Matrix3x2.Identity,
                        (float)Math.Clamp(layer.Opacity, 0, 1));
                    break;

                case PopLayer:
                    scene.PopLayer();
                    break;

                case FillRect fill:
                    scene.FillPath(RectPath(fill.Rect), VFillRule.NonZero, Matrix(transform), ToBrush(fill.Brush));
                    break;

                case FillRoundedRect fill:
                    scene.FillPath(
                        ToPath(PathGeometry.RoundedRectangle(fill.Rect, fill.Radius)),
                        VFillRule.NonZero,
                        Matrix(transform),
                        ToBrush(fill.Brush));
                    break;

                case FillEllipse fill:
                    scene.FillPath(
                        ToPath(PathGeometry.Ellipse(fill.Bounds)),
                        VFillRule.NonZero,
                        Matrix(transform),
                        ToBrush(fill.Brush));
                    break;

                case StrokeRect stroke:
                    scene.StrokePath(
                        RectPath(stroke.Rect),
                        new VStrokeStyle { Width = stroke.Width },
                        Matrix(transform),
                        ToBrush(stroke.Brush));
                    break;

                case FillPath fill:
                    scene.FillPath(ToPath(fill.Path), MapFillRule(fill.Path.FillRule), Matrix(transform), ToBrush(fill.Brush));
                    break;

                case StrokePath stroke:
                    scene.StrokePath(ToPath(stroke.Path), ToStrokeStyle(stroke), Matrix(transform), ToBrush(stroke.Brush));
                    break;

                case DrawGlyphs glyphs:
                    DrawGlyphRun(scene, glyphs, transform);
                    break;

                case DrawImage image:
                    DrawImageCommand(scene, image, transform);
                    break;

                case DropShadow shadow:
                    scene.DrawBlurredRoundedRect(
                        new Vector2((float)(shadow.Rect.X + shadow.Offset.X), (float)(shadow.Rect.Y + shadow.Offset.Y)),
                        new Vector2((float)shadow.Rect.Width, (float)shadow.Rect.Height),
                        Matrix(transform),
                        ToVelloColor(shadow.Color),
                        shadow.CornerRadius,
                        Math.Max(shadow.BlurRadius, 0) / 2);
                    break;
            }
        }
    }

    private void DrawGlyphRun(Scene scene, DrawGlyphs command, Transform transform)
    {
        var shaped = command.Glyphs;
        if (shaped.Glyphs.Count == 0) return;

        var font = GetFont(command.Font);
        var glyphs = new Glyph[shaped.Glyphs.Count];
        double penX = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            var glyph = shaped.Glyphs[i];
            glyphs[i] = new Glyph(
                glyph.GlyphId,
                (float)(penX + glyph.XOffset),
                (float)-glyph.YOffset);
            penX += glyph.XAdvance;
        }

        var glyphOrigin = Transform.Translation(command.Origin.X, command.Origin.Y);
        if (command.Transform is { } local) glyphOrigin *= local;
        glyphOrigin *= transform;

        var options = new GlyphRunOptions
        {
            FontSize = (float)shaped.FontSize,
            Brush = ToBrush(command.Brush),
            Transform = Matrix(glyphOrigin),
            BrushAlpha = (float)Math.Clamp(command.Opacity, 0, 1),
        };

        scene.DrawGlyphRun(font, glyphs, options);
    }

    private void DrawImageCommand(Scene scene, DrawImage command, Transform transform)
    {
        var image = GetImage(command.Image);
        var scale = Transform.Scale(
            command.Destination.Width / command.Image.Width,
            command.Destination.Height / command.Image.Height);
        var placement = scale
            * Transform.Translation(command.Destination.X, command.Destination.Y)
            * transform;

        var opacity = (float)Math.Clamp(command.Opacity, 0, 1);
        if (opacity >= 1f)
        {
            scene.DrawImage(image, Matrix(placement));
        }
        else
        {
            var brush = new ImageBrush(image) { Alpha = opacity, Quality = ImageQuality.Medium };
            scene.DrawImage(brush, Matrix(placement));
        }
    }

    private Font GetFont(FontFace face)
    {
        if (_fonts.TryGetValue(face, out var font)) return font;
        font = Font.Load(face.Data.Span, (uint)face.FaceIndex);
        _fonts[face] = font;
        return font;
    }

    private Image GetImage(ImageHandle handle)
    {
        if (_images.TryGetValue(handle, out var image)) return image;
        image = Image.FromPixels(
            handle.Pixels.Span,
            handle.Width,
            handle.Height,
            RenderFormat.Rgba8,
            ImageAlphaMode.Straight,
            handle.Stride);
        _images[handle] = image;
        return image;
    }

    private static VPathBuilder RectPath(Rect rect)
    {
        return new VPathBuilder()
            .MoveTo(rect.Left, rect.Top)
            .LineTo(rect.Right, rect.Top)
            .LineTo(rect.Right, rect.Bottom)
            .LineTo(rect.Left, rect.Bottom)
            .Close();
    }

    private static VPathBuilder SurfacePath(Size size)
    {
        return new VPathBuilder()
            .MoveTo(0, 0)
            .LineTo(size.Width, 0)
            .LineTo(size.Width, size.Height)
            .LineTo(0, size.Height)
            .Close();
    }

    private static VPathBuilder ToPath(PathGeometry geometry)
    {
        var builder = new VPathBuilder();
        foreach (var segment in geometry.Segments)
        {
            switch (segment.Verb)
            {
                case PathVerb.MoveTo:
                    builder.MoveTo(segment.X0, segment.Y0);
                    break;
                case PathVerb.LineTo:
                    builder.LineTo(segment.X0, segment.Y0);
                    break;
                case PathVerb.QuadraticTo:
                    builder.QuadraticTo(segment.X1, segment.Y1, segment.X0, segment.Y0);
                    break;
                case PathVerb.CubicTo:
                    builder.CubicTo(segment.X1, segment.Y1, segment.X2, segment.Y2, segment.X0, segment.Y0);
                    break;
                case PathVerb.Close:
                    builder.Close();
                    break;
            }
        }
        return builder;
    }

    private static VStrokeStyle ToStrokeStyle(StrokePath stroke)
    {
        var style = new VStrokeStyle { Width = stroke.Width };
        if (stroke.DashPattern is { Count: > 0 } pattern)
        {
            style.DashPattern = pattern.ToArray();
            style.DashPhase = stroke.DashPhase;
        }
        return style;
    }

    private static VBrush ToBrush(Rendering.Brush brush)
    {
        return brush switch
        {
            SolidColorBrush solid => new VelloSharp.SolidColorBrush(ToVelloColor(solid.Color)),
            LinearGradient linear => new VelloSharp.LinearGradientBrush(
                Vector(linear.Start),
                Vector(linear.End),
                ToStops(linear.Stops),
                MapExtend(linear.Extend)),
            RadialGradient radial => new VelloSharp.RadialGradientBrush(
                Vector(radial.Center),
                0f,
                Vector(radial.Center),
                (float)radial.Radius,
                ToStops(radial.Stops),
                MapExtend(radial.Extend)),
            SweepGradient sweep => new VelloSharp.SweepGradientBrush(
                Vector(sweep.Center),
                (float)sweep.StartAngle,
                (float)sweep.EndAngle,
                ToStops(sweep.Stops),
                MapExtend(sweep.Extend)),
            _ => throw new NotSupportedException($"unsupported brush: {brush.GetType().Name}"),
        };
    }

    private static VGradientStop[] ToStops(IReadOnlyList<Rendering.GradientStop> stops)
    {
        var result = new VGradientStop[stops.Count];
        for (int i = 0; i < stops.Count; i++)
            result[i] = new VGradientStop((float)stops[i].Offset, ToVelloColor(stops[i].Color));
        return result;
    }

    private static Vector2 Vector(Point point) => new((float)point.X, (float)point.Y);

    private static RgbaColor ToVelloColor(Color color) => RgbaColor.FromBytes(color.R, color.G, color.B, color.A);

    private static Matrix3x2 Matrix(Transform t) => new(t.M11, t.M12, t.M21, t.M22, t.M31, t.M32);

    private static VFillRule MapFillRule(Rendering.FillRule rule)
        => rule == Rendering.FillRule.EvenOdd ? VFillRule.EvenOdd : VFillRule.NonZero;

    private static ExtendMode MapExtend(GradientExtend extend) => extend switch
    {
        GradientExtend.Repeat => ExtendMode.Repeat,
        GradientExtend.Reflect => ExtendMode.Reflect,
        _ => ExtendMode.Pad,
    };

    private static VLayerBlend NoBlend() => new(LayerMix.Normal, LayerCompose.SrcOver);

    private static VLayerBlend MapBlend(Rendering.LayerBlend blend)
    {
        if (blend == Rendering.LayerBlend.Plus)
            return new VLayerBlend(LayerMix.Normal, LayerCompose.Plus);

        var mix = blend switch
        {
            Rendering.LayerBlend.Multiply => LayerMix.Multiply,
            Rendering.LayerBlend.Screen => LayerMix.Screen,
            Rendering.LayerBlend.Overlay => LayerMix.Overlay,
            Rendering.LayerBlend.Darken => LayerMix.Darken,
            Rendering.LayerBlend.Lighten => LayerMix.Lighten,
            Rendering.LayerBlend.Difference => LayerMix.Difference,
            _ => LayerMix.Normal,
        };
        return new VLayerBlend(mix, LayerCompose.SrcOver);
    }

    public void Dispose()
    {
        foreach (var font in _fonts.Values) font.Dispose();
        _fonts.Clear();
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
    }
}
