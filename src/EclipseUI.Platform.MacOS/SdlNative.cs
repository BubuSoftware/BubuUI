using System.Reflection;
using System.Runtime.InteropServices;

namespace EclipseUI.Platform.MacOS;

// SDL 3.2 ABI: https://github.com/libsdl-org/SDL/tree/release-3.2.28/include/SDL3
// SDL bool is one byte; SDL_Event is a 128-byte union on both macOS x64 and arm64.
internal static class SdlNative
{
    private const string Library = "EclipseUI.SDL3.MacOS";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SDL_GetSystemTheme();

    static SdlNative() => NativeLibrary.SetDllImportResolver(typeof(SdlNative).Assembly, ResolveLibrary);

    private static nint ResolveLibrary(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library) return 0;
        // App-local files support self-contained deployments; Homebrew and framework
        // locations support development without requiring DYLD environment variables.
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "libSDL3.dylib"),
            Path.Combine(AppContext.BaseDirectory, "libSDL3.0.dylib"),
            Path.Combine(AppContext.BaseDirectory, "SDL3.framework", "SDL3"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks", "SDL3.framework", "SDL3")),
            "/opt/homebrew/lib/libSDL3.dylib",
            "/usr/local/lib/libSDL3.dylib",
            "/Library/Frameworks/SDL3.framework/SDL3",
            "SDL3.framework/SDL3",
            "libSDL3.dylib",
            "libSDL3.0.dylib",
        ];
        foreach (var candidate in candidates)
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var library)) return library;
        return 0;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_main_np")]
    private static extern int PthreadMainNp();

    internal static void VerifyMainThread()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS desktop requires macOS.");
        if (PthreadMainNp() == 0)
            throw new InvalidOperationException("macOS window operations must run on the process main thread. Call AppBuilder.Start before awaiting asynchronous work.");
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_SetMainReady();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint SDL_Metal_CreateView(nint window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_Metal_DestroyView(nint view);
    internal const uint InitVideo = 0x20;
    internal const ulong WindowFlags = 0x8 | 0x20 | 0x2000 | 0x20000000; // hidden, resizable, high density, Metal
    internal const uint Quit = 0x100, WindowExposed = 0x204, WindowResized = 0x206,
        WindowPixelSizeChanged = 0x207, WindowMinimized = 0x209, WindowRestored = 0x20b,
        WindowMouseLeave = 0x20d, WindowFocusGained = 0x20e, WindowFocusLost = 0x20f,
        WindowCloseRequested = 0x210, WindowDisplayChanged = 0x213, WindowDisplayScaleChanged = 0x214,
        KeyDown = 0x300, KeyUp = 0x301, TextEditing = 0x302, TextInput = 0x303,
        MouseMotion = 0x400, MouseButtonDown = 0x401, MouseButtonUp = 0x402, MouseWheel = 0x403;

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    internal struct Event
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(16)] public uint WindowId;
        [FieldOffset(0)] public KeyboardEvent Key;
        [FieldOffset(0)] public TextEvent Text;
        [FieldOffset(0)] public MotionEvent Motion;
        [FieldOffset(0)] public ButtonEvent Button;
        [FieldOffset(0)] public WheelEvent Wheel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardEvent
    {
        public uint Type, Reserved;
        public ulong Timestamp;
        public uint WindowId, Which, Scancode, Key;
        public ushort Mod, Raw;
        public byte Down, Repeat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TextEvent
    {
        public uint Type, Reserved;
        public ulong Timestamp;
        public uint WindowId;
        public nint Text;
        public int Start, Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MotionEvent
    {
        public uint Type, Reserved;
        public ulong Timestamp;
        public uint WindowId, Which, State;
        public float X, Y, XRel, YRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ButtonEvent
    {
        public uint Type, Reserved;
        public ulong Timestamp;
        public uint WindowId, Which;
        public byte Button, Down, Clicks, Padding;
        public float X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WheelEvent
    {
        public uint Type, Reserved;
        public ulong Timestamp;
        public uint WindowId, Which;
        public float X, Y;
        public uint Direction;
        public float MouseX, MouseY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int X, Y, Width, Height; }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_InitSubSystem(uint flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_QuitSubSystem(uint flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint SDL_GetError();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint SDL_GetCurrentVideoDriver();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint SDL_CreateWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string title, int width, int height, ulong flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint SDL_CreatePopupWindow(nint parent, int offsetX, int offsetY, int width, int height, ulong flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint SDL_GetKeyboardFocus();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint SDL_GetDisplayForWindow(nint window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_GetDisplayUsableBounds(uint display, out Rect bounds);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_GetWindowPosition(nint window, out int x, out int y);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_SetWindowPosition(nint window, int x, int y);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint SDL_CreateSurfaceFrom(int width, int height, uint format, nint pixels, int pitch);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_DestroySurface(nint surface);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_SetWindowShape(nint window, nint shape);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_ReadSurfacePixel(nint surface, int x, int y, out byte r, out byte g, out byte b, out byte a);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_DestroyWindow(nint window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint SDL_GetWindowID(nint window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint SDL_GetWindowProperties(nint window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint SDL_GetPointerProperty(uint props, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint defaultValue);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long SDL_GetNumberProperty(uint props, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, long defaultValue);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_ShowWindow(nint window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_HideWindow(nint window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_GetWindowSize(nint window, out int width, out int height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_GetWindowSizeInPixels(nint window, out int width, out int height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern float SDL_GetWindowDisplayScale(nint window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_SetWindowTitle(nint window, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_SetWindowSize(nint window, int width, int height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_PollEvent(out Event value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_PushEvent(ref Event value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ushort SDL_GetModState();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint SDL_GetKeyFromScancode(uint scancode, ushort modifiers, [MarshalAs(UnmanagedType.I1)] bool keyEvent);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_CaptureMouse([MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_StartTextInput(nint window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_StopTextInput(nint window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_SetTextInputArea(nint window, in Rect rect, int cursor);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint SDL_GetClipboardText();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_SetClipboardText([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_free(nint memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal delegate bool EventFilter(nint userdata, ref Event value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_AddEventWatch(EventFilter filter, nint userdata);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SDL_RemoveEventWatch(EventFilter filter, nint userdata);

    internal static string Utf8(nint value) => Marshal.PtrToStringUTF8(value) ?? "";
    internal static void Check(bool result)
    {
        if (!result) throw new InvalidOperationException("SDL3: " + Utf8(SDL_GetError()));
    }
}
