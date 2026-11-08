using EclipseUI;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using Xunit;

namespace EclipseUI.Rendering.Tests;

/// <summary>
/// <see cref="VelloSceneBuilder"/> 是 CPU 与 GPU 后端共用的唯一翻译层。
/// 这里验证同一 display list 经两条路径得到等价像素，防止重构时两条路径漂移。
/// </summary>
public class SceneBuilderTests
{
    private static DisplayList BuildScene(int width, int height)
    {
        var list = new DisplayList(new Size(width, height));
        list.Add(new FillRect(new Rect(0, 0, width, height), new LinearGradient(
            new Point(0, 0), new Point(width, height),
            new[] { GradientStop.At(0, new Color(20, 24, 40)), GradientStop.At(1, new Color(60, 30, 70)) })));

        list.Add(new FillRoundedRect(new Rect(12, 12, 80, 48), 10, new Color(240, 200, 90)));
        list.Add(new StrokeRect(new Rect(12, 72, 80, 32), new Color(255, 255, 255, 180), 2));

        list.Add(new PushClip(new Rect(110, 12, 70, 70)));
        list.Add(new FillRect(new Rect(100, 5, 100, 100), new Color(0, 200, 255)));
        list.Add(new PopClip());

        list.Add(new PushLayer(0.5));
        list.Add(new FillEllipse(new Rect(120, 40, 100, 60), new Color(255, 80, 160)));
        list.Add(new PopLayer());

        list.Add(new PushTransform(Transform.RotationAround(Math.PI / 8, new Point(250, 60))));
        list.Add(new FillRect(new Rect(220, 30, 60, 60), new Color(120, 255, 160)));
        list.Add(new PopTransform());

        return list;
    }

    [Fact]
    public void Cpu_render_is_deterministic_across_backend_instances()
    {
        const int width = 300;
        const int height = 140;
        var list = BuildScene(width, height);

        var first = Render.Frame(list, width, height);
        var second = Render.Frame(list, width, height);

        Assert.Equal(first.ContentHash(), second.ContentHash());
    }

    [Fact]
    public void Gpu_backend_matches_cpu_backend_within_tolerance()
    {
        if (!Render.TryCreateGpuBackend(out var backend))
            return; // 无可用 GPU（CI 容器/无驱动）时跳过：CPU 路径仍被上面的确定性测试覆盖。

        const int width = 300;
        const int height = 140;
        var list = BuildScene(width, height);

        var cpu = Render.Frame(list, width, height);
        var gpu = new RenderTarget(width, height);
        backend.Render(list, gpu);
        backend.Dispose();

        var mismatches = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var a = cpu.GetPixel(x, y);
                var b = gpu.GetPixel(x, y);
                if (Math.Abs(a.R - b.R) > 2 || Math.Abs(a.G - b.G) > 2 ||
                    Math.Abs(a.B - b.B) > 2 || Math.Abs(a.A - b.A) > 2)
                    mismatches++;
            }
        }

        // G1 结论：圆角/抗锯齿边缘允许 <0.5% 像素差异。
        var ratio = mismatches / (double)(width * height);
        Assert.True(ratio < 0.0005, $"gpu/cpu pixel mismatch {ratio:P2} ({mismatches} px) exceeds 0.05%");
    }
}
