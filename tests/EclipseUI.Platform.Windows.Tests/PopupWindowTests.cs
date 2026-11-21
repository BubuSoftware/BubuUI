using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class PopupWindowTests
{
    [WindowsFact]
    public void OwnedPopupExtendsPastOwnerRoutesInputAndClosesWithoutQuitting()
    {
        using var owner = new Win32Window("EclipseUI popup test", 240, 180, quitOnDestroy: false);
        owner.Show();
        var factory = new Win32PopupWindowFactory(owner);
        using var popup = factory.Create(interactive: true);
        var actual = popup.SetBounds(new(180, 160, 160, 100));
        popup.Show();
        Assert.True(factory.IsOwnerFocused);
        Assert.False(popup.IsFocused);
        Assert.True(actual.Right > owner.LogicalClientSize.Width);
        Assert.True(actual.Bottom > owner.LogicalClientSize.Height);
        Assert.Equal(owner.Handle, PopupNative.GetWindow(popup.SurfaceHandle.Handle, 4)); // GW_OWNER
        Assert.Equal(0, PopupNative.GetWindowLongW(popup.SurfaceHandle.Handle, -16) & 0x00C00000); // no caption

        Point? pressed = null;
        Key? key = null;
        string? text = null;
        popup.PointerPressed += (point, _, button) => { Assert.Equal(PointerButton.Right, button); pressed = point; };
        popup.KeyDown += (value, _, _) => key = value;
        popup.TextInput += value => text = value;
        var pixels = (nint)((20 << 16) | 10);
        Native.SendMessageW(popup.SurfaceHandle.Handle, 0x0204, 2, pixels);
        Native.SendMessageW(popup.SurfaceHandle.Handle, 0x0205, 0, pixels);
        Assert.Equal(new Point(10 / popup.ScaleFactor, 20 / popup.ScaleFactor), pressed);
        Assert.True(popup.IsFocused);
        Native.SendMessageW(popup.SurfaceHandle.Handle, 0x0100, 13, 1);
        Assert.Equal(Key.Enter, key);
        Native.SendMessageW(popup.SurfaceHandle.Handle, 0x0102, '中', 1);
        Assert.Equal("中", text);

        var handle = popup.SurfaceHandle.Handle;
        Native.SendMessageW(handle, 0x0010, 0, 0);
        Assert.True(popup.IsClosed);
        Assert.True(PopupNative.IsWindow(handle)); // The surface must be released before HWND destruction.
        popup.Dispose();
        Assert.False(PopupNative.IsWindow(handle));
        Assert.True(owner.PumpMessages());
        Assert.False(owner.IsClosed);
    }

    [WindowsFact]
    public void NestedPopupHasNativeParentAndBoundsStayWithinTheWorkArea()
    {
        using var owner = new Win32Window("EclipseUI nested popup test", 240, 180, quitOnDestroy: false);
        owner.Show();
        var factory = new Win32PopupWindowFactory(owner);
        using var parent = factory.Create(interactive: true);
        parent.SetBounds(new(40, 40, 120, 80));
        parent.Show();
        using var child = factory.Create(interactive: true, parent);
        var actual = child.SetBounds(new(-100000, -100000, 100000, 100000));
        Assert.Equal(parent.SurfaceHandle.Handle, PopupNative.GetWindow(child.SurfaceHandle.Handle, 4));
        Assert.True(actual.Width <= factory.AvailableSize.Width + 1);
        Assert.True(actual.Height <= factory.AvailableSize.Height + 1);
        Assert.True(child.ClientWidth > 0);
        Assert.True(child.ClientHeight > 0);
        Assert.True(owner.PumpMessages());
    }

    [WindowsFact]
    public void NativePopupShapeExcludesRoundedCornersAndTracksSizeAndRadiusChanges()
    {
        using var owner = new Win32Window("EclipseUI rounded popup test", 240, 180, quitOnDestroy: false);
        owner.Show();
        var factory = new Win32PopupWindowFactory(owner);
        using var popup = factory.Create(interactive: true);
        var actual = popup.SetBounds(new(20, 20, 160, 100));
        popup.SetShape(new(Point.Zero, actual.Size), 16);
        popup.Show();
        var region = PopupNative.CreateRectRgn(0, 0, 0, 0);
        Assert.NotEqual(0, region);
        try
        {
            Assert.NotEqual(0, PopupNative.GetWindowRgn(popup.SurfaceHandle.Handle, region));
            Assert.False(PopupNative.PtInRegion(region, 0, 0));
            Assert.False(PopupNative.PtInRegion(region, popup.ClientWidth - 1, popup.ClientHeight - 1));
            Assert.True(PopupNative.PtInRegion(region, popup.ClientWidth / 2, popup.ClientHeight / 2));
            Assert.True(PopupNative.PtInRegion(region, popup.ClientWidth / 2, 1));

            // A zero radius restores the rectangle, including the previous corner cutouts.
            popup.SetShape(new(Point.Zero, actual.Size), 0);
            Assert.NotEqual(0, PopupNative.GetWindowRgn(popup.SurfaceHandle.Handle, region));
            Assert.True(PopupNative.PtInRegion(region, 0, 0));
            Assert.True(PopupNative.PtInRegion(region, popup.ClientWidth - 1, popup.ClientHeight - 1));

            actual = popup.SetBounds(new(20, 20, 200, 120));
            popup.SetShape(new(Point.Zero, actual.Size), 20);
            Assert.NotEqual(0, PopupNative.GetWindowRgn(popup.SurfaceHandle.Handle, region));
            Assert.False(PopupNative.PtInRegion(region, 0, 0));
            Assert.True(PopupNative.PtInRegion(region, popup.ClientWidth - 2, popup.ClientHeight / 2));
            Assert.True(owner.PumpMessages());
        }
        finally { PopupNative.DeleteObject(region); }
    }

    private static class PopupNative
    {
        [DllImport("user32.dll")] public static extern nint GetWindow(nint hwnd, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowLongW(nint hwnd, int index);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(nint hwnd);
        [DllImport("gdi32.dll")] public static extern nint CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(nint handle);
        [DllImport("user32.dll")] public static extern int GetWindowRgn(nint hwnd, nint region);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PtInRegion(nint region, int x, int y);
    }
}
