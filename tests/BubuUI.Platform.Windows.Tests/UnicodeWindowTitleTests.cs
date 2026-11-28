using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Xunit;

namespace BubuUI.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public class UnicodeWindowTitleTests
{
    [Fact]
    public void NativeTitlePreservesSurrogatePairsOnCreationAndUpdate()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string initial = "你好 Bubu UI🐻";
        using var window = new Win32Window(initial, 200, 150, quitOnDestroy: false);
        var buffer = new StringBuilder(256);
        GetWindowTextW(window.Handle, buffer, buffer.Capacity);
        Assert.Equal(initial, buffer.ToString());
        const string updated = "更新 👩‍💻🐻😀";
        window.SetTitle(updated);
        GetWindowTextW(window.Handle, buffer, buffer.Capacity);
        Assert.Equal(updated, buffer.ToString());
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int capacity);
}
