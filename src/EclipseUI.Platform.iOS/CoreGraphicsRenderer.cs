using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CoreGraphics;
using CoreImage;
using CoreText;
using EclipseUI.Rendering;
using Foundation;
using NFloat = System.Runtime.InteropServices.NFloat;

namespace EclipseUI.Platform.iOS;

/// <summary>Replays a display list in UIKit's point coordinate system.</summary>
internal static class CoreGraphicsRenderer
{
    private static readonly ConditionalWeakTable<ImageHandle, CGImage> Images = new();
    private static readonly ConditionalWeakTable<FontFace, CTFontDescriptor> Fonts = new();
    private sealed record ShadowBitmap(CGImage Image, CGRect Bounds);
    private static readonly ConditionalWeakTable<DropShadow, ShadowBitmap> Shadows = new();
    private static readonly Lazy<CIContext> Effects = new(() => CIContext.FromOptions(null));

    internal static void Draw(CGContext context, DisplayList list)
    {
        context.SaveState();
        foreach (var command in list.Commands)
        {
            switch (command)
            {
                case PushTransform transform:
                    context.SaveState();
                    context.ConcatCTM(Native(transform.Transform));
                    break;
                case PushClip clip:
                    context.SaveState();
                    context.ClipToRect(Native(clip.Rect));
                    break;
                case PushClipPath clip:
                    context.SaveState();
                    AddPath(context, clip.Path);
                    Clip(context, clip.Path.FillRule);
                    break;
                case PopClip or PopTransform:
                    context.RestoreState();
                    break;
                case PushLayer layer:
                    context.SaveState();
                    context.SetAlpha((NFloat)Math.Clamp(layer.Opacity, 0, 1));
                    context.SetBlendMode(layer.Blend switch
                    {
                        LayerBlend.Multiply => CGBlendMode.Multiply,
                        LayerBlend.Screen => CGBlendMode.Screen,
                        LayerBlend.Overlay => CGBlendMode.Overlay,
                        LayerBlend.Darken => CGBlendMode.Darken,
                        LayerBlend.Lighten => CGBlendMode.Lighten,
                        LayerBlend.Difference => CGBlendMode.Difference,
                        LayerBlend.Plus => CGBlendMode.PlusLighter,
                        _ => CGBlendMode.Normal
                    });
                    context.BeginTransparencyLayer(null);
                    break;
                case PopLayer:
                    context.EndTransparencyLayer();
                    context.RestoreState();
                    break;
                case FillRect fill:
                    context.AddRect(Native(fill.Rect));
                    Fill(context, fill.Brush);
                    break;
                case FillRoundedRect fill:
                    AddRoundedRect(context, fill.Rect, fill.Radius);
                    Fill(context, fill.Brush);
                    break;
                case FillEllipse fill:
                    context.AddEllipseInRect(Native(fill.Bounds));
                    Fill(context, fill.Brush);
                    break;
                case FillPath fill:
                    AddPath(context, fill.Path);
                    Fill(context, fill.Brush, fill.Path.FillRule);
                    break;
                case StrokeRect stroke:
                    context.AddRect(Native(stroke.Rect));
                    Stroke(context, stroke.Brush, stroke.Width);
                    break;
                case StrokePath stroke:
                    AddPath(context, stroke.Path);
                    Stroke(context, stroke.Brush, stroke.Width, stroke.DashPattern, stroke.DashPhase);
                    break;
                case DrawImage image:
                    context.SaveState();
                    context.SetAlpha((NFloat)Math.Clamp(image.Opacity, 0, 1));
                    DrawImage(context, Images.GetValue(image.Image, CreateImage), Native(image.Destination));
                    context.RestoreState();
                    break;
                case DropShadow shadow when !shadow.Rect.Size.IsEmpty && shadow.Color.A > 0:
                    DrawShadow(context, shadow);
                    break;
                case CoreTextLine text:
                    context.SaveState();
                    SetFillColor(context, text.Color);
                    BeginText(context, text.Baseline);
                    text.Line.Draw(context);
                    context.RestoreState();
                    break;
                case DrawGlyphs glyphs:
                    DrawGlyphs(context, glyphs);
                    break;
            }
        }
        context.RestoreState();
    }

    private static CGRect Native(Rect r) => new(r.X, r.Y, r.Width, r.Height);
    private static CGAffineTransform Native(Transform t) => new(t.M11, t.M12, t.M21, t.M22, t.M31, t.M32);

    private static void AddRoundedRect(CGContext context, Rect rect, double radius)
    {
        if (rect.Size.IsEmpty) return;
        var r = (NFloat)Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2);
        if (r == 0) context.AddRect(Native(rect));
        else
        {
            using var path = CGPath.FromRoundedRect(Native(rect), r, r);
            context.AddPath(path);
        }
    }

    private static void AddPath(CGContext context, PathGeometry path)
    {
        foreach (var s in path.Segments)
        {
            switch (s.Verb)
            {
                case PathVerb.MoveTo: context.MoveTo((NFloat)s.X0, (NFloat)s.Y0); break;
                case PathVerb.LineTo: context.AddLineToPoint((NFloat)s.X0, (NFloat)s.Y0); break;
                case PathVerb.QuadraticTo:
                    context.AddQuadCurveToPoint((NFloat)s.X0, (NFloat)s.Y0, (NFloat)s.X1, (NFloat)s.Y1);
                    break;
                case PathVerb.CubicTo:
                    context.AddCurveToPoint((NFloat)s.X0, (NFloat)s.Y0, (NFloat)s.X1, (NFloat)s.Y1, (NFloat)s.X2, (NFloat)s.Y2);
                    break;
                case PathVerb.Close: context.ClosePath(); break;
            }
        }
    }

    private static void SetFillColor(CGContext context, Color color) =>
        context.SetFillColor(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

    private static void Fill(CGContext context, Brush brush, FillRule rule = FillRule.NonZero)
    {
        if (brush is SolidColorBrush solid)
        {
            SetFillColor(context, solid.Color);
            if (rule == FillRule.EvenOdd) context.EOFillPath();
            else context.FillPath();
            return;
        }

        context.SaveState();
        Clip(context, rule);
        CoreGraphicsGradients.Draw(context, brush);
        context.RestoreState();
    }

    private static void Clip(CGContext context, FillRule rule)
    {
        if (rule == FillRule.EvenOdd) context.EOClip();
        else context.Clip();
    }

    private static void Stroke(CGContext context, Brush brush, double width, IReadOnlyList<double>? dash = null, double phase = 0)
    {
        context.SaveState();
        context.SetLineWidth((NFloat)width);
        if (dash is { Count: > 0 })
            context.SetLineDash((NFloat)phase, dash.Select(d => (NFloat)d).ToArray());
        context.ReplacePathWithStrokedPath();
        Fill(context, brush);
        context.RestoreState();
    }

    internal static CGImage CreateImage(ImageHandle image)
    {
        using var space = CGColorSpace.CreateSrgb();
        using var provider = MemoryMarshal.TryGetArray(image.Pixels, out var bytes)
            ? new CGDataProvider(bytes.Array!, bytes.Offset, bytes.Count)
            : new CGDataProvider(image.Pixels.ToArray());
        return new CGImage(image.Width, image.Height, 8, 32, image.Stride, space,
            CGBitmapFlags.ByteOrder32Big | CGBitmapFlags.Last, provider, null, true, CGColorRenderingIntent.Default);
    }

    internal static void DrawImage(CGContext context, CGImage image, CGRect destination)
    {
        context.SaveState();
        context.TranslateCTM(destination.X, destination.Y + destination.Height);
        context.ScaleCTM(1, -1);
        context.DrawImage(new CGRect(CGPoint.Empty, destination.Size), image);
        context.RestoreState();
    }

    private static void DrawShadow(CGContext context, DropShadow shadow)
    {
        var rect = shadow.Rect;
        var sigma = Math.Max(0, shadow.BlurRadius) / 2;
        var at = new Rect(rect.X + shadow.Offset.X, rect.Y + shadow.Offset.Y, rect.Width, rect.Height);
        if (sigma == 0)
        {
            AddRoundedRect(context, at, shadow.CornerRadius);
            SetFillColor(context, shadow.Color);
            context.FillPath();
            return;
        }
        var bitmap = Shadows.GetValue(shadow, _ =>
        {
            var padding = Math.Ceiling(3 * sigma);
            var width = rect.Width + 2 * padding;
            var height = rect.Height + 2 * padding;
            var scale = RasterScale(context, width, height);
            var area = new CGRect(0, 0, Math.Ceiling(width * scale), Math.Ceiling(height * scale));
            var c = shadow.Color;
            using var color = CIColor.FromRgba(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
            using var generator = new CIRoundedRectangleGenerator
            {
                InputExtent = new CGRect(padding * scale, padding * scale, rect.Width * scale, rect.Height * scale),
                Radius = (float)(scale * Math.Clamp(shadow.CornerRadius, 0, Math.Min(rect.Width, rect.Height) / 2)),
                Color = color
            };
            using var shape = generator.OutputImage!;
            using var blur = new CIGaussianBlur { InputImage = shape, Radius = (float)(sigma * scale) };
            using var blurred = blur.OutputImage!;
            // Core Image coordinates run up from the lower edge of the extent.
            var bounds = new CGRect(at.X - padding, at.Bottom + padding - area.Height / scale,
                area.Width / scale, area.Height / scale);
            return new ShadowBitmap(Effects.Value.CreateCGImage(blurred, area)!, bounds);
        });
        DrawImage(context, bitmap.Image, bitmap.Bounds);
    }

    // Work in device pixels, with bounded offscreen storage for oversized/transformed shapes.
    internal static double RasterScale(CGContext context, double width, double height)
    {
        var unit = context.ConvertRectToDeviceSpace(new CGRect(0, 0, 1, 1));
        var density = Math.Max(unit.Width, unit.Height);
        var dimensionLimit = 2048 / Math.Max(width, height);
        var areaLimit = Math.Sqrt(1_048_576 / width / height);
        return Math.Min(density, Math.Min(dimensionLimit, areaLimit));
    }

    private static void BeginText(CGContext context, Point origin)
    {
        context.TranslateCTM((NFloat)origin.X, (NFloat)origin.Y);
        context.ScaleCTM(1, -1);
        context.TextMatrix = CGAffineTransform.MakeIdentity();
        context.TextPosition = CGPoint.Empty;
    }

    private static CTFontDescriptor GetFontDescriptor(FontFace face)
    {
        using var data = NSData.FromArray(face.Data.ToArray());
        var faces = CTFontManager.CreateFontDescriptors(data) ?? [];
        if ((uint)face.FaceIndex >= (uint)faces.Length)
        {
            foreach (var descriptor in faces) descriptor.Dispose();
            throw new ArgumentException($"Cannot load face {face.FaceIndex} of {face.Name}.");
        }
        for (var i = 0; i < faces.Length; i++)
            if (i != face.FaceIndex) faces[i].Dispose();
        return faces[face.FaceIndex];
    }

    private static void DrawGlyphs(CGContext context, DrawGlyphs run)
    {
        var glyphs = run.Glyphs.Glyphs;
        if (glyphs.Count == 0) return;
        var ids = new ushort[glyphs.Count];
        var positions = new CGPoint[glyphs.Count];
        double pen = 0;
        for (var i = 0; i < glyphs.Count; i++)
        {
            var glyph = glyphs[i];
            ids[i] = glyph.GlyphId;
            positions[i] = new CGPoint(pen + glyph.XOffset, glyph.YOffset);
            pen += glyph.XAdvance;
        }

        using var font = new CTFont(Fonts.GetValue(run.Font, GetFontDescriptor), (NFloat)run.Glyphs.FontSize);
        context.SaveState();
        if (run.Transform is { } local) context.ConcatCTM(Native(local));
        context.SetAlpha((NFloat)Math.Clamp(run.Opacity, 0, 1));
        if (run.Brush is SolidColorBrush solid)
        {
            BeginText(context, run.Origin);
            SetFillColor(context, solid.Color);
            font.DrawGlyphs(context, ids, positions);
        }
        else
        {
            // Form an outline in display-list coordinates so gradient coordinates
            // stay fixed while Core Text's glyph paths are flipped to UIKit space.
            for (var i = 0; i < glyphs.Count; i++)
            {
                var placement = new CGAffineTransform(1, 0, 0, -1,
                    (NFloat)run.Origin.X + positions[i].X, (NFloat)run.Origin.Y - positions[i].Y);
                using var path = font.GetPathForGlyph(ids[i], ref placement);
                if (path is not null) context.AddPath(path);
            }
            Fill(context, run.Brush);
        }
        context.RestoreState();
    }
}
