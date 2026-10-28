using EclipseUI;
using EclipseUI.Rendering;
using EclipseUI.Rendering.Vello;
using Xunit;

namespace EclipseUI.Rendering.Tests;

/// <summary>所有像素测试共用同一后端配置（CPU，确定性输出）。</summary>
public static class Render
{
    public static RenderTarget Frame(DisplayList list, int width, int height)
    {
        using var backend = new VelloCpuBackend(new VelloBackendOptions
        {
            UseCpu = true,
            ValidateDisplayList = true,
        });
        var target = new RenderTarget(width, height);
        backend.Render(list, target);
        return target;
    }

    public static RenderTarget Frame(DisplayList list) => Frame(list, (int)list.Size.Width, (int)list.Size.Height);

    /// <summary>颜色近似比较（覆盖/抗锯齿带来的 ±通道误差）。</summary>
    public static void AssertColor(Color expected, Color actual, int tolerance = 2, string? because = null)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance &&
            Math.Abs(expected.G - actual.G) <= tolerance &&
            Math.Abs(expected.B - actual.B) <= tolerance &&
            Math.Abs(expected.A - actual.A) <= tolerance,
            $"expected {expected} ±{tolerance} but got {actual}{(because is null ? "" : " (" + because + ")")}");
    }
}
