using EclipseUI;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Rendering.Tests;

/// <summary>嵌套栈语义：变换 + 裁剪 + 图层必须与绘制内容处于同一坐标系。</summary>
public class ScopeTests
{
    [Fact]
    public void NestedTransforms_ComposeInnerThenOuter()
    {
        var list = new DisplayList(new Size(64, 64));
        list.Add(new PushTransform(Transform.Scale(2)));
        list.Add(new PushTransform(Transform.Translation(4, 4)));
        list.Add(new FillRect(new Rect(0, 0, 4, 4), Color.White));
        list.Add(new PopTransform());
        list.Add(new PopTransform());

        var frame = Render.Frame(list);

        // 先平移 4，再放大 2 → (8,8)-(16,16)
        var bounds = frame.OpaqueBounds();
        Assert.Equal(new Rect(8, 8, 8, 8), bounds);
    }

    [Fact]
    public void PushClip_UnderTransform_AlignsWithDrawnContent()
    {
        var list = new DisplayList(new Size(64, 64));
        list.Add(new PushTransform(Transform.Translation(16, 0)));
        list.Add(new PushClip(new Rect(0, 0, 8, 8)));
        list.Add(new FillRect(new Rect(0, 0, 64, 64), new Color(0, 200, 0)));
        list.Add(new PopClip());
        list.Add(new PopTransform());

        var frame = Render.Frame(list);

        var bounds = frame.OpaqueBounds();
        Assert.Equal(new Rect(16, 0, 8, 8), bounds);
    }

    [Fact]
    public void PushLayer_UnderTransform_StillCoversWholeSurface()
    {
        var list = new DisplayList(new Size(32, 32));
        list.Add(new PushTransform(Transform.Translation(64, 64)));
        list.Add(new PushLayer(0.5));
        list.Add(new FillRect(new Rect(0, 0, 8, 8), new Color(255, 0, 0)));
        list.Add(new PopLayer());
        list.Add(new PopTransform());

        var frame = Render.Frame(list);

        // 内容被变换到 (64,64)，超出 surface；图层裁剪不应额外裁掉可见区域。
        Assert.Equal(0, frame.CountPixelsDifferentFrom(Color.Transparent));
    }

    [Fact]
    public void PathClip_HidesOutsideShape()
    {
        var circle = PathGeometry.Ellipse(new Rect(8, 8, 48, 48));
        var list = new DisplayList(new Size(64, 64));
        list.Add(new PushClipPath(circle));
        list.Add(new FillRect(new Rect(0, 0, 64, 64), new Color(255, 200, 0)));
        list.Add(new PopClip());

        var frame = Render.Frame(list);

        Render.AssertColor(new Color(255, 200, 0), frame.GetPixel(32, 32));
        Assert.Equal(0, frame.GetPixel(9, 9).A);
        Assert.Equal(0, frame.GetPixel(63, 63).A);
    }

    [Fact]
    public void NestedLayers_MultiplyOpacity()
    {
        var list = new DisplayList(new Size(16, 16));
        list.Add(new FillRect(new Rect(0, 0, 16, 16), Color.White));
        list.Add(new PushLayer(0.5));
        list.Add(new PushLayer(0.5));
        list.Add(new FillRect(new Rect(0, 0, 16, 16), Color.Black));
        list.Add(new PopLayer());
        list.Add(new PopLayer());

        var frame = Render.Frame(list);

        // 黑 25% 覆盖白 → 约 191
        Assert.InRange(frame.GetPixel(8, 8).R, 180, 200);
    }

    [Fact]
    public void MultiplyBlend_DarkensBackground()
    {
        var list = new DisplayList(new Size(16, 16));
        list.Add(new FillRect(new Rect(0, 0, 16, 16), new Color(128, 128, 128)));
        list.Add(new PushLayer(1, LayerBlend.Multiply));
        list.Add(new FillRect(new Rect(0, 0, 16, 16), new Color(255, 0, 0)));
        list.Add(new PopLayer());

        var frame = Render.Frame(list);

        var pixel = frame.GetPixel(8, 8);
        Assert.InRange(pixel.R, 115, 140);
        Assert.InRange(pixel.G, 0, 20);
    }
}
