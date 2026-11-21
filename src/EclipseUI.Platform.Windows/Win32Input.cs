using System.Runtime.InteropServices;
using System.Text;

namespace EclipseUI.Platform.Windows;

public sealed partial class Win32Window
{
    public event EventHandler<TextCompositionEventArgs>? TextComposition;
    private bool _textInputEnabled = true;
    public void SetTextInputEnabled(bool enabled)
    {
        if (IsClosed || _textInputEnabled == enabled) return;
        // Restore the default IME context when an editable control gains focus.
        ImmAssociateContextEx(_hwnd, 0, enabled ? 0x10u : 0u);
        _textInputEnabled = enabled;
    }
    public void PostText(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var character in text) Win32Native.PostMessageW(_hwnd, 0x0102, character, 0);
    }
    public void PostKey(Key key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Win32Native.PostMessageW(_hwnd, 0x0100, (int)key, 1);
        Win32Native.PostMessageW(_hwnd, 0x0101, (int)key, (nint)unchecked((int)0xC0000001));
    }
    private char? _pendingSurrogate;
    private void EmitCharacter(char character)
    {
        if (char.IsHighSurrogate(character)) { _pendingSurrogate = character; return; }
        if (char.IsLowSurrogate(character))
        {
            if (_pendingSurrogate is { } high) CharacterTyped?.Invoke(this, new Win32TextEventArgs(new string([high, character])));
            _pendingSurrogate = null; return;
        }
        _pendingSurrogate = null; CharacterTyped?.Invoke(this, new Win32TextEventArgs(character));
    }
    private bool ProcessIme(uint message, nint lParam)
    {
        if (!InputEnabled) return false;
        if (message == 0x010D) { TextComposition?.Invoke(this, new("", CompositionStage.Started)); return false; }
        if (message == 0x010E) { TextComposition?.Invoke(this, new("", CompositionStage.Cancelled)); return false; }
        if (message != 0x010F) return false;
        var context = ImmGetContext(_hwnd);
        if (context == 0) return false;
        try
        {
            string Read(uint flag)
            {
                var length = ImmGetCompositionStringW(context, flag, null, 0);
                if (length <= 0) return "";
                var buffer = new byte[length]; var copied = ImmGetCompositionStringW(context, flag, buffer, length);
                return copied > 0 ? Encoding.Unicode.GetString(buffer, 0, copied) : "";
            }
            if ((lParam.ToInt64() & 0x800) != 0) TextComposition?.Invoke(this, new(Read(0x800), CompositionStage.Committed));
            else if ((lParam.ToInt64() & 8) != 0) TextComposition?.Invoke(this, new(Read(8), CompositionStage.Updated));
        }
        finally { ImmReleaseContext(_hwnd, context); }
        return true;
    }
    public void SetImeCaret(Rect logicalCaret)
    {
        var context = ImmGetContext(_hwnd); if (context == 0) return;
        try
        {
            var x = (int)Math.Round(logicalCaret.X * ScaleFactor); var y = (int)Math.Round(logicalCaret.Bottom * ScaleFactor);
            var composition = new CompositionForm { Style = 2, Position = new() { X = x, Y = y } };
            ImmSetCompositionWindow(context, ref composition);
            var candidate = new CandidateForm { Style = 0x40, Position = new() { X = x, Y = y } };
            ImmSetCandidateWindow(context, ref candidate);
        }
        finally { ImmReleaseContext(_hwnd, context); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionForm { public uint Style; public Win32Native.POINT Position; public Win32Native.RECT Area; }
    [StructLayout(LayoutKind.Sequential)] private struct CandidateForm { public uint Index, Style; public Win32Native.POINT Position; public Win32Native.RECT Area; }
    [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint hwnd);
    [DllImport("imm32.dll")] private static extern bool ImmAssociateContextEx(nint hwnd, nint context, uint flags);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(nint hwnd, nint context);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern int ImmGetCompositionStringW(nint context, uint index, [Out] byte[]? buffer, int length);
    [DllImport("imm32.dll")] private static extern bool ImmSetCompositionWindow(nint context, ref CompositionForm form);
    [DllImport("imm32.dll")] private static extern bool ImmSetCandidateWindow(nint context, ref CandidateForm form);
    public static KeyModifiers CurrentModifiers =>
        ((Win32Native.GetKeyState(0x10) & 0x8000) != 0 ? KeyModifiers.Shift : 0) |
        ((Win32Native.GetKeyState(0x11) & 0x8000) != 0 ? KeyModifiers.Control : 0) |
        ((Win32Native.GetKeyState(0x12) & 0x8000) != 0 ? KeyModifiers.Alt : 0);
}

internal sealed class Win32Clipboard(nint owner) : IClipboard
{
    public string GetText()
    {
        if (!OpenClipboard(owner)) return "";
        try
        {
            var memory = GetClipboardData(13); if (memory == 0) return "";
            var pointer = GlobalLock(memory); if (pointer == 0) return "";
            try { return Marshal.PtrToStringUni(pointer) ?? ""; } finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }
    public void SetText(string text)
    {
        if (!OpenClipboard(owner)) return;
        nint memory = 0;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(text + '\0'); memory = GlobalAlloc(2, (nuint)bytes.Length);
            if (memory == 0) return;
            var pointer = GlobalLock(memory); if (pointer == 0) return;
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); } finally { GlobalUnlock(memory); }
            if (EmptyClipboard() && SetClipboardData(13, memory) != 0) memory = 0;
        }
        finally { if (memory != 0) GlobalFree(memory); CloseClipboard(); }
    }
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
}
