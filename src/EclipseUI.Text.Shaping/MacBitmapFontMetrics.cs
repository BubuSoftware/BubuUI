using System.Runtime.InteropServices;
using System.Text;
using EclipseUI.Rendering;

namespace EclipseUI.Text.Shaping;

/// <summary>
/// CoreText applies optical sizing and rounded advances to Apple Color Emoji that
/// the OpenType HarfBuzz font functions do not expose. Resolve the actual file/face
/// so a similarly named installed font cannot silently supply different metrics.
/// </summary>
internal sealed class MacBitmapFontMetrics : IDisposable
{
    private const int MaxCachedFonts = 16;
    private const uint Utf8 = 0x08000100;
    private readonly object _gate = new();
    private readonly Dictionary<double, nint> _fonts = new();
    private nint _fontName, _fileUrl, _fontUrlKey;

    private MacBitmapFontMetrics() { }

    internal static MacBitmapFontMetrics? TryCreate(string path, int faceIndex)
    {
        if (!OperatingSystem.IsMacOS() || faceIndex < 0) return null;
        nint pathString = 0, url = 0, descriptors = 0, probe = 0, named = 0, name = 0, urlKey = 0;
        try
        {
            Native.EnsureInitialized();
            var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path));
            pathString = Native.CFStringCreateWithBytes(0, bytes, bytes.Length, Utf8, false);
            if (pathString == 0) return null;
            url = Native.CFURLCreateWithFileSystemPath(0, pathString, 0, false);
            if (url == 0) return null;
            descriptors = Native.CTFontManagerCreateFontDescriptorsFromURL(url);
            if (descriptors == 0 || faceIndex >= Native.CFArrayGetCount(descriptors)) return null;
            var descriptor = Native.CFArrayGetValueAtIndex(descriptors, faceIndex);
            probe = Native.CTFontCreateWithFontDescriptor(descriptor, 12, 0);
            if (probe == 0) return null;
            name = Native.CTFontCopyPostScriptName(probe);
            if (name == 0 || StringValue(name) != "AppleColorEmoji") return null;
            // A raw file descriptor bypasses CoreText's Apple emoji optical policy.
            // Resolve by its validated PostScript name, then require the same file.
            named = Native.CTFontCreateWithName(name, 12, 0);
            urlKey = Native.CopyFontUrlKey();
            if (named == 0 || urlKey == 0 || !SameFile(named, url, urlKey)) return null;
            var result = new MacBitmapFontMetrics();
            result._fontName = Native.CFRetain(name);
            result._fileUrl = Native.CFRetain(url);
            result._fontUrlKey = Native.CFRetain(urlKey);
            return result;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        finally
        {
            Release(urlKey); Release(name); Release(named); Release(probe);
            Release(descriptors); Release(url); Release(pathString);
        }
    }

    internal TextLineMetrics LineMetrics(double fontSize)
    {
        lock (_gate)
        {
            if (!double.IsFinite(fontSize) || fontSize <= 0) return default;
            var font = GetFont(fontSize);
            return new(Native.CTFontGetAscent(font), Native.CTFontGetDescent(font));
        }
    }

    /// <summary>Nominal advances only; callers retain zero-advance shaping controls.</summary>
    internal double[] GlyphAdvances(ushort[] glyphs, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        var advances = new double[glyphs.Length];
        if (glyphs.Length == 0 || !double.IsFinite(fontSize) || fontSize <= 0) return advances;
        lock (_gate)
        {
            var sizes = new NativeSize[glyphs.Length];
            Native.CTFontGetAdvancesForGlyphs(GetFont(fontSize), 1, glyphs, sizes, glyphs.Length);
            for (var index = 0; index < sizes.Length; index++) advances[index] = sizes[index].Width;
        }
        return advances;
    }

    private nint GetFont(double fontSize)
    {
        ObjectDisposedException.ThrowIf(_fontName == 0, this);
        if (_fonts.TryGetValue(fontSize, out var existing)) return existing;
        var font = Native.CTFontCreateWithName(_fontName, fontSize, 0);
        if (font == 0) throw new InvalidOperationException("CoreText could not create the bitmap font at the requested size.");
        if (!SameFile(font, _fileUrl, _fontUrlKey))
        {
            Release(font);
            throw new InvalidOperationException("CoreText resolved the bitmap font to a different file.");
        }
        if (_fonts.Count >= MaxCachedFonts)
        {
            var first = _fonts.First();
            Release(first.Value);
            _fonts.Remove(first.Key);
        }
        _fonts.Add(fontSize, font);
        return font;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var font in _fonts.Values) Release(font);
            _fonts.Clear();
            Release(_fontName); Release(_fileUrl); Release(_fontUrlKey);
            _fontName = _fileUrl = _fontUrlKey = 0;
        }
    }

    private static string StringValue(nint value)
    {
        var length = checked((int)Native.CFStringGetLength(value));
        var bytes = new byte[checked(length * 4 + 1)];
        if (!Native.CFStringGetCString(value, bytes, bytes.Length, Utf8)) return "";
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    private static void Release(nint value) { if (value != 0) Native.CFRelease(value); }

    private static bool SameFile(nint font, nint expectedUrl, nint urlKey)
    {
        var actual = Native.CTFontCopyAttribute(font, urlKey);
        try { return actual != 0 && Native.CFEqual(actual, expectedUrl); }
        finally { Release(actual); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        internal double Width;
        internal double Height;
        internal NativeSize(double width, double height) { Width = width; Height = height; }
    }

    private static class Native
    {
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CoreText = "/System/Library/Frameworks/CoreText.framework/CoreText";
        private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
        // NSFont bridges CoreText's cached font objects when AppKit loads. Loading
        // it after creating CTFont objects leaves incompatible cached CF types on
        // macOS. Load AppKit first without creating NSApplication, and keep both
        // framework references for the process lifetime rather than dlclose them.
        private static readonly Lazy<(nint AppKit, nint CoreText)> Frameworks = new(() =>
            (NativeLibrary.Load(AppKit), NativeLibrary.Load(CoreText)));
        internal static void EnsureInitialized() => _ = Frameworks.Value;
        internal static nint CopyFontUrlKey()
        {
            var library = Frameworks.Value.CoreText;
            return CFRetain(Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCTFontURLAttribute")));
        }
        [DllImport(CoreFoundation)] internal static extern nint CFStringCreateWithBytes(nint allocator, byte[] bytes, nint count, uint encoding, [MarshalAs(UnmanagedType.I1)] bool externalRepresentation);
        [DllImport(CoreFoundation)] internal static extern nint CFURLCreateWithFileSystemPath(nint allocator, nint path, int pathStyle, [MarshalAs(UnmanagedType.I1)] bool isDirectory);
        [DllImport(CoreFoundation)] internal static extern nint CFArrayGetCount(nint array);
        [DllImport(CoreFoundation)] internal static extern nint CFArrayGetValueAtIndex(nint array, nint index);
        [DllImport(CoreFoundation)] internal static extern nint CFRetain(nint value);
        [DllImport(CoreFoundation)] internal static extern void CFRelease(nint value);
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFEqual(nint first, nint second);
        [DllImport(CoreFoundation)] internal static extern nint CFStringGetLength(nint value);
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CFStringGetCString(nint value, [Out] byte[] buffer, nint size, uint encoding);
        [DllImport(CoreText)] internal static extern nint CTFontManagerCreateFontDescriptorsFromURL(nint url);
        [DllImport(CoreText)] internal static extern nint CTFontCreateWithFontDescriptor(nint descriptor, double size, nint matrix);
        [DllImport(CoreText)] internal static extern nint CTFontCreateWithName(nint name, double size, nint matrix);
        [DllImport(CoreText)] internal static extern nint CTFontCopyPostScriptName(nint font);
        [DllImport(CoreText)] internal static extern nint CTFontCopyAttribute(nint font, nint key);
        [DllImport(CoreText)] internal static extern double CTFontGetAscent(nint font);
        [DllImport(CoreText)] internal static extern double CTFontGetDescent(nint font);
        [DllImport(CoreText)] internal static extern double CTFontGetAdvancesForGlyphs(nint font, uint orientation, [In] ushort[] glyphs, [Out] NativeSize[] advances, nint count);
    }
}
