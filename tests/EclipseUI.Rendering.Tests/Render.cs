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

    /// <summary>
    /// 探测 GPU 后端可用性（成功则返回已初始化的后端，调用方负责 Dispose）。
    /// 无 GPU（CI 容器/无驱动）时返回 false，调用方自动跳过并留痕。
    /// </summary>
    public static bool TryCreateGpuBackend(out IRenderBackend backend)
    {
        try
        {
            var gpu = new VelloCpuBackend(new VelloBackendOptions { UseCpu = false, ValidateDisplayList = true });
            var probe = new RenderTarget(32, 32);
            gpu.Render(new DisplayList(new Size(32, 32)), probe);
            backend = gpu;
            return true;
        }
        catch
        {
            backend = null!;
            return false;
        }
    }

    /// <summary>仓库根目录（含 EclipseUI.slnx 的最近祖先）。</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EclipseUI.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根目录（未发现 EclipseUI.slnx）。");
    }
}
