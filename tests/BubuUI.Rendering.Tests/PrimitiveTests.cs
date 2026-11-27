using BubuUI;
using BubuUI.Rendering;
using Xunit;

namespace BubuUI.Rendering.Tests;

public class PrimitiveTests
{
    [Fact]
    public void ColorInterpolation_PreservesByteChannelsAndAlpha()
    {
        var start = new Color(200, 10, 60, 20);
        var end = new Color(0, 110, 40, 180);

        Assert.Equal(start, Color.Lerp(start, end, 0));
        Assert.Equal(new Color(100, 60, 50, 100), Color.Lerp(start, end, 0.5));
        Assert.Equal(end, Color.Lerp(start, end, 1));
    }

    [Fact]
    public void FillRect_CenterIsExact_OutsideIsTransparent()
    {
        var red = new Color(220, 30, 30);
        var list = new DisplayList(new Size(64, 64))
            .Add(new FillRect(new Rect(8, 8, 48, 48), red));

        var frame = Render.Frame(list);

        Render.AssertColor(red, frame.GetPixel(32, 32));
        Assert.Equal(0, frame.GetPixel(2, 2).A);
        Assert.Equal(255, frame.GetPixel(8, 8).A);
    }

    [Fact]
    public void RoundedRect_CornerIsTransparent_InsideIsFilled()
    {
        var color = new Color(40, 120, 220);
        var list = new DisplayList(new Size(80, 80))
            .Add(new FillRoundedRect(new Rect(8, 8, 64, 64), 20, color));

        var frame = Render.Frame(list);

        // 圆角外接点（矩形角）应透明；圆心方向应实心。
        Assert.Equal(0, frame.GetPixel(9, 9).A);
        Render.AssertColor(color, frame.GetPixel(40, 40));
        Render.AssertColor(color, frame.GetPixel(40, 10)); // 上边缘中点
    }

    [Fact]
    public void Ellipse_CenterFilled_CornerOfBoundsTransparent()
    {
        var color = new Color(200, 200, 40);
        var list = new DisplayList(new Size(80, 80))
            .Add(new FillEllipse(new Rect(10, 10, 60, 60), color));

        var frame = Render.Frame(list);

        Render.AssertColor(color, frame.GetPixel(40, 40));
        Assert.Equal(0, frame.GetPixel(11, 11).A);
        Render.AssertColor(color, frame.GetPixel(40, 12));
    }

    [Fact]
    public void StrokeRect_DrawsBorder_LeavesInteriorEmpty()
    {
        var list = new DisplayList(new Size(64, 64))
            .Add(new StrokeRect(new Rect(10, 10, 44, 44), new Color(0, 0, 0), 4));

        var frame = Render.Frame(list);

        Assert.Equal(255, frame.GetPixel(10, 32).A);   // 左边框
        Assert.Equal(255, frame.GetPixel(32, 10).A);   // 上边框
        Assert.Equal(0, frame.GetPixel(32, 32).A);     // 内部
        Assert.Equal(0, frame.GetPixel(2, 2).A);       // 外部
    }

    [Fact]
    public void DashPattern_LeavesGaps()
    {
        var path = PathGeometry.Line(new Point(4, 32), new Point(124, 32));
        var list = new DisplayList(new Size(128, 64))
            .Add(new StrokePath(path, new Color(255, 255, 255), 4, new[] { 10d, 10d }));

        var frame = Render.Frame(list);

        int opaque = 0, transparent = 0;
        for (int x = 6; x < 122; x++)
        {
            if (frame.GetPixel(x, 32).A > 200) opaque++;
            else if (frame.GetPixel(x, 32).A == 0) transparent++;
        }

        Assert.True(opaque > 20, $"expected dash segments, got {opaque}");
        Assert.True(transparent > 20, $"expected gaps, got {transparent}");
    }

    [Fact]
    public void PushClip_HidesOutside_PreservesInside()
    {
        var list = new DisplayList(new Size(64, 64));
        list.Add(new PushClip(new Rect(16, 16, 32, 32)));
        list.Add(new FillRect(new Rect(0, 0, 64, 64), new Color(10, 200, 10)));
        list.Add(new PopClip());

        var frame = Render.Frame(list);

        Render.AssertColor(new Color(10, 200, 10), frame.GetPixel(32, 32));
        Assert.Equal(0, frame.GetPixel(8, 32).A);
        Assert.Equal(0, frame.GetPixel(48, 32).A);
        Assert.Equal(0, frame.GetPixel(32, 8).A);
    }

    [Fact]
    public void PushTransform_TranslatesSubsequentCommands()
    {
        var list = new DisplayList(new Size(64, 64));
        list.Add(new PushTransform(Transform.Translation(32, 0)));
        list.Add(new FillRect(new Rect(8, 8, 16, 48), new Color(255, 0, 0)));
        list.Add(new PopTransform());

        var frame = Render.Frame(list);

        Render.AssertColor(new Color(255, 0, 0), frame.GetPixel(40, 32));
        Assert.Equal(0, frame.GetPixel(16, 32).A);
    }

    [Fact]
    public void LayerOpacity_BlendsHalfTransparentOverBackground()
    {
        var list = new DisplayList(new Size(32, 32));
        list.Add(new FillRect(new Rect(0, 0, 32, 32), Color.White));
        list.Add(new PushLayer(0.5));
        list.Add(new FillRect(new Rect(0, 0, 32, 32), new Color(255, 0, 0)));
        list.Add(new PopLayer());

        var frame = Render.Frame(list);

        var pixel = frame.GetPixel(16, 16);
        Assert.InRange(pixel.R, 250, 255);
        Assert.InRange(pixel.G, 120, 135);
        Assert.InRange(pixel.B, 120, 135);
    }

    [Fact]
    public void LinearGradient_InterpolatesBetweenStops()
    {
        var list = new DisplayList(new Size(100, 20))
            .Add(new FillRect(new Rect(0, 0, 100, 20), new LinearGradient(
                new Point(0, 0),
                new Point(100, 0),
                new[] { GradientStop.At(0, new Color(255, 0, 0)), GradientStop.At(1, new Color(0, 0, 255)) })));

        var frame = Render.Frame(list);

        var left = frame.GetPixel(2, 10);
        var middle = frame.GetPixel(50, 10);
        var right = frame.GetPixel(97, 10);

        Assert.True(left.R > 230 && left.B < 25, $"left {left}");
        Assert.True(right.B > 230 && right.R < 25, $"right {right}");
        Assert.InRange(middle.R, 110, 145);
        Assert.InRange(middle.B, 110, 145);
    }

    [Fact]
    public void RadialGradient_IsBrightAtCenter_DarkAtEdge()
    {
        var list = new DisplayList(new Size(64, 64))
            .Add(new FillEllipse(new Rect(0, 0, 64, 64), new RadialGradient(
                new Point(32, 32),
                32,
                new[] { GradientStop.At(0, Color.White), GradientStop.At(1, Color.Black) })));

        var frame = Render.Frame(list);

        Assert.True(frame.GetPixel(32, 32).R > 230);
        Assert.True(frame.GetPixel(32, 60).R < 40);
    }

    [Fact]
    public void DrawImage_ScaledCheckerboard_ProducesAlternatingCells()
    {
        var checker = ImageHandle.Checkerboard(
            4, 4, 1, new Color(255, 255, 255), new Color(0, 0, 0));
        var list = new DisplayList(new Size(64, 64))
            .Add(new DrawImage(checker, new Rect(0, 0, 64, 64)));

        var frame = Render.Frame(list);

        var clean = frame.GetPixel(8, 8);
        var dark = frame.GetPixel(24, 8);

        Assert.Equal(255, clean.A);
        Assert.True(clean.R > 200, $"expected white cell, got {clean}");
        Assert.True(dark.R < 40, $"expected black cell, got {dark}");
    }

    [Fact]
    public void DrawImage_StraightRgba_PreservesColor()
    {
        var image = ImageHandle.Solid(4, 4, new Color(30, 200, 90));
        var list = new DisplayList(new Size(32, 32))
            .Add(new DrawImage(image, new Rect(8, 8, 16, 16)));

        var frame = Render.Frame(list);

        Render.AssertColor(new Color(30, 200, 90), frame.GetPixel(16, 16));
        Assert.Equal(0, frame.GetPixel(2, 2).A);
    }
}
