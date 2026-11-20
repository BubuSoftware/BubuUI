using System.Runtime.InteropServices;
using EclipseUI.Rendering;
using Xunit;

namespace EclipseUI.Platform.MacOS.Tests;

internal static class CocoaPopupAssertions
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendPointer(nint target, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(nint target, nint selector);

    public static void Transparent(NativeWindowHandle surface)
    {
        Assert.True(surface.IsTransparent);
        var window = SendPointer(surface.Handle, sel_registerName("window"));
        var layer = SendPointer(surface.Handle, sel_registerName("layer"));
        Assert.NotEqual(0, window);
        Assert.NotEqual(0, layer);
        Assert.False(SendBool(window, sel_registerName("isOpaque")));
        Assert.False(SendBool(layer, sel_registerName("isOpaque")));
        Assert.False(SendBool(window, sel_registerName("hasShadow"))); // EclipseUI draws its own shadow.
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(double X, double Y, double Width, double Height);

    [DllImport(CoreGraphics)] private static extern nint CGWindowListCreateImage(NativeRect bounds, uint options, uint window, uint imageOptions);
    [DllImport(CoreGraphics)] private static extern nint CGWindowListCreate(uint options, uint window);
    [DllImport(CoreGraphics)] private static extern nint CGWindowListCreateImageFromArray(NativeRect bounds, nint windows, uint imageOptions);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern nint CFArrayCreate(nint allocator, nint[] values, nint count, nint callbacks);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint value);
    [DllImport(CoreGraphics)] private static extern nuint CGImageGetWidth(nint image);
    [DllImport(CoreGraphics)] private static extern nuint CGImageGetHeight(nint image);
    [DllImport(CoreGraphics)] private static extern nint CGColorSpaceCreateDeviceRGB();
    [DllImport(CoreGraphics)] private static extern nint CGBitmapContextCreate(nint data, nuint width, nuint height, nuint bits, nuint stride, nint colors, uint bitmapInfo);
    [DllImport(CoreGraphics)] private static extern void CGContextDrawImage(nint context, NativeRect rect, nint image);
    [DllImport(CoreGraphics)] private static extern void CGContextRelease(nint context);
    [DllImport(CoreGraphics)] private static extern void CGImageRelease(nint image);
    [DllImport(CoreGraphics)] private static extern void CGColorSpaceRelease(nint colorSpace);

    // Captures this process's own window without asking for screen-recording access.
    // Some macOS configurations disable the legacy capture API; opacity checks above still run there.
    public static RenderTarget? Capture(NativeWindowHandle surface)
    {
        var window = SendPointer(surface.Handle, sel_registerName("window"));
        var number = unchecked((uint)SendPointer(window, sel_registerName("windowNumber")).ToInt64());
        var image = CGWindowListCreateImage(new(double.PositiveInfinity, double.PositiveInfinity, 0, 0), 8, number, 1);
        return CopyImage(image);
    }

    // Includes only this process's two windows, composited by WindowServer over the actual owner backdrop.
    public static RenderTarget? CaptureComposite(NativeWindowHandle owner, NativeWindowHandle popup, Rect screenBounds)
    {
        uint Number(NativeWindowHandle surface) => unchecked((uint)SendPointer(SendPointer(surface.Handle, sel_registerName("window")), sel_registerName("windowNumber")).ToInt64());
        var ownerArray = CGWindowListCreate(8, Number(owner));
        var popupArray = CGWindowListCreate(8, Number(popup));
        nint combined = 0;
        try
        {
            if (ownerArray == 0 || popupArray == 0) return null;
            combined = CFArrayCreate(0, [CFArrayGetValueAtIndex(popupArray, 0), CFArrayGetValueAtIndex(ownerArray, 0)], 2, 0);
            return CopyImage(CGWindowListCreateImageFromArray(new(screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height), combined, 1));
        }
        finally
        {
            if (combined != 0) CFRelease(combined);
            if (ownerArray != 0) CFRelease(ownerArray);
            if (popupArray != 0) CFRelease(popupArray);
        }
    }

    private static RenderTarget? CopyImage(nint image)
    {
        if (image == 0) return null;
        nint pixels = 0, colors = 0, context = 0;
        try
        {
            var width = checked((int)CGImageGetWidth(image));
            var height = checked((int)CGImageGetHeight(image));
            var target = new RenderTarget(width, height);
            pixels = Marshal.AllocHGlobal(target.Pixels.Length);
            Marshal.Copy(target.Pixels, 0, pixels, target.Pixels.Length);
            colors = CGColorSpaceCreateDeviceRGB();
            context = CGBitmapContextCreate(pixels, (nuint)width, (nuint)height, 8, (nuint)target.Stride, colors, 0x4001);
            Assert.NotEqual(0, context);
            CGContextDrawImage(context, new(0, 0, width, height), image);
            Marshal.Copy(pixels, target.Pixels, 0, target.Pixels.Length);
            // CoreGraphics emits premultiplied RGBA; RenderTarget and PNG store straight RGBA.
            for (var i = 0; i < target.Pixels.Length; i += 4)
            {
                var alpha = target.Pixels[i + 3];
                if (alpha is 0 or 255) continue;
                for (var channel = 0; channel < 3; channel++)
                    target.Pixels[i + channel] = (byte)Math.Min(255, (target.Pixels[i + channel] * 255 + alpha / 2) / alpha);
            }
            return target;
        }
        finally
        {
            if (context != 0) CGContextRelease(context);
            if (colors != 0) CGColorSpaceRelease(colors);
            if (pixels != 0) Marshal.FreeHGlobal(pixels);
            CGImageRelease(image);
        }
    }
}
