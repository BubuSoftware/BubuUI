using Xunit;

namespace BubuUI.Platform.Linux.Tests;

public sealed class PopupShapeTests
{
    [Fact]
    public void RoundedContentExcludesCornersAndShadowMarginFromNativeInput()
    {
        var pixels = LinuxPopupShape.CreateMask(120, 80, new(10, 10, 100, 60), 12, 1, 1);
        byte Alpha(int x, int y) => pixels[(y * 120 + x) * 4 + 3];
        Assert.Equal(0, Alpha(0, 40));
        Assert.Equal(0, Alpha(9, 40));
        Assert.Equal(0, Alpha(10, 10));
        Assert.Equal(0, Alpha(109, 69));
        Assert.Equal(255, Alpha(10, 40));
        Assert.Equal(255, Alpha(60, 10));
        Assert.Equal(255, Alpha(60, 40));
        Assert.Equal(0, Alpha(110, 40));
    }

    [Fact]
    public void NativeInputShapeUsesTheWindowsDpiAndClampsOversizedRadius()
    {
        var pixels = LinuxPopupShape.CreateMask(100, 60, new(5, 5, 40, 20), 200, 2, 2);
        byte Alpha(int x, int y) => pixels[(y * 100 + x) * 4 + 3];
        Assert.Equal(0, Alpha(5, 20));
        Assert.Equal(0, Alpha(10, 10));
        Assert.Equal(255, Alpha(50, 10));
        Assert.Equal(255, Alpha(10, 30));
        Assert.Equal(255, Alpha(50, 49));
        Assert.Equal(0, Alpha(50, 50));
        Assert.Equal(0, Alpha(90, 30));
    }
}
