using System.Runtime.InteropServices;

namespace BubuUI.Platform.MacOS;

// Typed Objective-C entry points work in NativeAOT without reflection or dynamic dispatch.
internal static class CocoaWindowAppearance
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly nint AppearanceClass = objc_getClass("NSAppearance");
    private static readonly nint StringClass = objc_getClass("NSString");
    private static readonly nint StringWithUtf8 = sel_registerName("stringWithUTF8String:");
    private static readonly nint AppearanceNamed = sel_registerName("appearanceNamed:");
    private static readonly nint SetAppearance = sel_registerName("setAppearance:");

    internal static void SetDark(nint window, bool dark)
    {
        if (window == 0) throw new InvalidOperationException("SDL did not expose its Cocoa window.");
        var name = SendString(StringClass, StringWithUtf8, dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua");
        var appearance = SendPointer(AppearanceClass, AppearanceNamed, name);
        if (appearance == 0) throw new InvalidOperationException("AppKit could not resolve the window appearance.");
        // Window-scoped: differently themed windows remain independent. Never alter NSApp or focus.
        SendVoid(window, SetAppearance, appearance);
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendString(nint target, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendPointer(nint target, nint selector, nint argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint target, nint selector, nint argument);
}
