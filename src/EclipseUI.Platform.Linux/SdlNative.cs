using System.Runtime.InteropServices;

namespace EclipseUI.Platform.Linux;

// SDL 3.2 ABI: https://github.com/libsdl-org/SDL/tree/release-3.2.28/include/SDL3
// SDL bool is one byte; SDL_Event is a 128-byte union on both Linux x64 and arm64.
internal static class SdlNative
{
    private const string Library = "libSDL3.so.0";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SDL_GetSystemTheme();
    internal const uint InitVideo = 0x20;
    internal const ulong WindowFlags = 0x8 | 0x20 | 0x2000 | 0x10000000; // hidden, resizable, high density, Vulkan
    internal const ulong PopupMenu = 0x80000, Tooltip = 0x40000, InputFocus = 0x200, Transparent = 0x40000000;
    internal const uint PixelFormatAbgr8888 = 0x16762004;
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
    internal static extern nint SDL_CreatePopupWindow(nint parent, int x, int y, int width, int height, ulong flags);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_SetWindowParent(nint window, nint parent);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_SetWindowModal(nint window, [MarshalAs(UnmanagedType.I1)] bool modal);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SDL_RaiseWindow(nint window);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong SDL_GetWindowFlags(nint window);
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

    internal static string Utf8(nint value) => Marshal.PtrToStringUTF8(value) ?? "";
    internal static void Check(bool result)
    {
        if (!result) throw new InvalidOperationException("SDL3: " + Utf8(SDL_GetError()));
    }
}
