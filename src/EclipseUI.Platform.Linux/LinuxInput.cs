using System.Text;

namespace EclipseUI.Platform.Linux;

internal static class LinuxInput
{
    // SDL keycodes are Unicode scalars or scancodes with bit 30 set. They are not Win32 VK codes.
    internal static Key Key(uint key) => key switch
    {
        >= 'a' and <= 'z' => (Key)(key - 'a' + (uint)EclipseUI.Key.A),
        >= 'A' and <= 'Z' => (Key)key,
        8 => EclipseUI.Key.Backspace, 9 => EclipseUI.Key.Tab, 13 or 0x40000058 => EclipseUI.Key.Enter,
        27 => EclipseUI.Key.Escape, 32 => EclipseUI.Key.Space, 127 => EclipseUI.Key.Delete,
        0x4000003b => EclipseUI.Key.F2,
        0x4000004a => EclipseUI.Key.Home, 0x4000004b => EclipseUI.Key.PageUp,
        0x4000004d => EclipseUI.Key.End, 0x4000004e => EclipseUI.Key.PageDown,
        0x4000004f => EclipseUI.Key.Right, 0x40000050 => EclipseUI.Key.Left,
        0x40000051 => EclipseUI.Key.Down, 0x40000052 => EclipseUI.Key.Up,
        _ => EclipseUI.Key.None,
    };

    internal static KeyModifiers Modifiers(ushort value)
        => ((value & 0x0003) != 0 ? KeyModifiers.Shift : 0)
         | ((value & 0x00c0) != 0 ? KeyModifiers.Control : 0)
         | ((value & 0x0300) != 0 ? KeyModifiers.Alt : 0);

    internal static PointerButton? Button(byte button) => button switch
    {
        1 => PointerButton.Left, 2 => PointerButton.Middle, 3 => PointerButton.Right, _ => null,
    };

    internal static Point ToLogical(double x, double y, double densityX, double densityY, double scale)
        => new(x * densityX / scale, y * densityY / scale);

    internal static double WheelDelta(float y, uint direction) => y * (direction == 1 ? -120.0 : 120.0);

    internal static string? BasicText(uint keycode, ushort modifiers)
    {
        // Use SDL's layout-resolved Unicode keycode. GUI and Ctrl/Alt shortcuts
        // must not insert text; MODE (AltGr) still allows layout characters.
        if ((modifiers & 0x0c00) != 0 || ((modifiers & 0x4000) == 0 && (modifiers & 0x03c0) != 0)) return null;
        if (!Rune.IsValid(keycode)) return null;
        var rune = new Rune(keycode);
        return Rune.IsControl(rune) ? null : rune.ToString();
    }
}
