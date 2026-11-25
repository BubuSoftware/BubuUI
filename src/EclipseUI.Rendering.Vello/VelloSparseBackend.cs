using System.Buffers.Binary;
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

/// <summary>Rasterizes display lists with the native Sparse CPU engine, without a GPU device.</summary>
public sealed class VelloSparseBackend : IRenderBackend
{
    private SparseRenderContext? _context;
    private bool _disposed;
    private readonly bool _validateDisplayList;

    public VelloSparseBackend(bool validateDisplayList = false) => _validateDisplayList = validateDisplayList;

    public string Name => "Vello Sparse (CPU)";
    public RenderCapabilities Capabilities => RenderCapabilities.All;

    public void Render(DisplayList list, RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (target.Width > ushort.MaxValue || target.Height > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(target), "Sparse targets cannot exceed 65535 pixels per dimension.");
        if (_validateDisplayList)
        {
            var errors = list.Validate();
            if (errors.Count > 0)
                throw new InvalidOperationException("invalid display list: " + string.Join("; ", errors));
        }
        if (_context is null || _context.Width != target.Width || _context.Height != target.Height)
        {
            _context?.Dispose();
            _context = null;
            _context = new SparseRenderContext((ushort)target.Width, (ushort)target.Height);
        }
        _context.Reset();
        Build(_context, list, target.Size);
        _context.RenderTo(target.Pixels);
    }

    private readonly Dictionary<FontFace, Font> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<FontFace> _sbixFonts = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<FontFace> _colrFonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ImageHandle, Image> _images = new(ReferenceEqualityComparer.Instance);
    private readonly SparseShadowCache _shadows = new();

    private void Build(SparseRenderContext scene, DisplayList list, Size targetSize)
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
                    DrawShadow(scene, shadow, transform);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported drawing command: {command.GetType().Name}.");
            }
        }
    }

    private void DrawShadow(SparseRenderContext scene, DropShadow shadow, Transform transform)
    {
        if (shadow.Color.A == 0 || shadow.Rect.Size.IsEmpty) return;
        if (shadow.BlurRadius <= 0)
        {
            var bounds = new Rect(shadow.Rect.X + shadow.Offset.X, shadow.Rect.Y + shadow.Offset.Y,
                shadow.Rect.Width, shadow.Rect.Height);
            scene.FillPath(ToPath(PathGeometry.RoundedRectangle(bounds, shadow.CornerRadius)),
                VFillRule.NonZero, Matrix(transform), ToVelloColor(shadow.Color));
            return;
        }
        var cached = _shadows.Get(shadow, transform);
        var placement = Transform.Scale(cached.LogicalWidth / cached.Width, cached.LogicalHeight / cached.Height)
            * Transform.Translation(shadow.Rect.X + shadow.Offset.X - cached.Padding,
                shadow.Rect.Y + shadow.Offset.Y - cached.Padding)
            * transform;
        scene.DrawImage(new ImageBrush(cached.Image) { Quality = ImageQuality.Medium }, Matrix(placement));
    }

    private void DrawGlyphRun(SparseRenderContext scene, DrawGlyphs command, Transform transform)
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

        // The native COLR path sizes an intermediate bitmap using the affine
        // scale, then applies that scale again when drawing it. Move the scale
        // into FontSize and normalize the affine basis to avoid double scaling.
        // Put glyph positions in the affine origin so shaping stays unchanged.
        if (_colrFonts.Contains(command.Font))
        {
            var scale = Math.Max(Math.Sqrt((double)glyphOrigin.M11 * glyphOrigin.M11 + (double)glyphOrigin.M12 * glyphOrigin.M12),
                Math.Sqrt((double)glyphOrigin.M21 * glyphOrigin.M21 + (double)glyphOrigin.M22 * glyphOrigin.M22));
            if (scale == 0) return;
            options.FontSize = (float)(shaped.FontSize * scale);
            foreach (var glyph in glyphs)
            {
                var origin = Transform.Translation(command.Origin.X + glyph.X, command.Origin.Y + glyph.Y);
                if (command.Transform is { } glyphLocal) origin *= glyphLocal;
                var placement = origin * transform;
                options.Transform = Matrix(new Transform(
                    (float)(placement.M11 / scale), (float)(placement.M12 / scale),
                    (float)(placement.M21 / scale), (float)(placement.M22 / scale),
                    placement.M31, placement.M32));
                scene.DrawGlyphRun(font, [new Glyph(glyph.Id, 0, 0)], options);
            }
            return;
        }

        // The native sbix path scales bitmap images but leaves per-glyph positions
        // untransformed. Put each position into its affine origin so Retina scale,
        // rotation and shear apply to both the image and its placement.
        if (_sbixFonts.Contains(command.Font) &&
            (glyphOrigin.M11 != 1 || glyphOrigin.M12 != 0 || glyphOrigin.M21 != 0 || glyphOrigin.M22 != 1))
        {
            foreach (var glyph in glyphs)
            {
                var origin = Transform.Translation(command.Origin.X + glyph.X, command.Origin.Y + glyph.Y);
                if (command.Transform is { } glyphLocal) origin *= glyphLocal;
                options.Transform = Matrix(origin * transform);
                scene.DrawGlyphRun(font, [new Glyph(glyph.Id, 0, 0)], options);
            }
            return;
        }

        scene.DrawGlyphRun(font, glyphs, options);
    }

    private void DrawImageCommand(SparseRenderContext scene, DrawImage command, Transform transform)
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
        if (HasTable(face, 0x73626978)) _sbixFonts.Add(face); // sbix
        if (HasTable(face, 0x434F4C52)) _colrFonts.Add(face); // COLR
        return font;
    }

    private static bool HasTable(FontFace face, uint tag)
    {
        var data = face.Data.Span;
        if (data.Length < 12) return false;
        var offset = 0;
        if (BinaryPrimitives.ReadUInt32BigEndian(data) == 0x74746366) // ttcf
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
            if (face.FaceIndex < 0 || (uint)face.FaceIndex >= count || face.FaceIndex > (data.Length - 16) / 4) return false;
            var tableOffset = BinaryPrimitives.ReadUInt32BigEndian(data[(12 + face.FaceIndex * 4)..]);
            if (tableOffset > data.Length - 12) return false;
            offset = (int)tableOffset;
        }
        var tables = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 4)..]);
        if (tables > (data.Length - offset - 12) / 16) return false;
        for (var index = 0; index < tables; index++)
            if (BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 12 + index * 16)..]) == tag) return true;
        return false;
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
        if (_disposed) return;
        _disposed = true;
        _context?.Dispose();
        _context = null;
        foreach (var font in _fonts.Values) font.Dispose();
        _fonts.Clear();
        _sbixFonts.Clear();
        _colrFonts.Clear();
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
        _shadows.Dispose();
    }
}
