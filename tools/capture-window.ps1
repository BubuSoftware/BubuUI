# 截取指定窗口的客户区，用于人工/自动核对真实窗口像素。
#   powershell -ExecutionPolicy Bypass -File tools/capture-window.ps1 -Class BubuUI.Win32Window -Out out/window-screen.png
param(
    [string]$Title,
    [string]$Class,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$WaitSeconds = 10
)

if (-not $Title -and -not $Class) {
    Write-Error "provide -Class and/or -Title"
    exit 2
}

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class CaptureNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int length);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public static IntPtr Find(string className, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            var cls = new StringBuilder(256); GetClassName(h, cls, 256);
            var txt = new StringBuilder(256); GetWindowText(h, txt, 256);
            if (!string.IsNullOrEmpty(className) && cls.ToString() != className) return true;
            if (!string.IsNullOrEmpty(title) && txt.ToString() != title) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
"@

[CaptureNative]::SetProcessDPIAware() | Out-Null

$hwnd = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ($hwnd -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline) {
    $hwnd = [CaptureNative]::Find($Class, $Title)
    if ($hwnd -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 250 }
}

if ($hwnd -eq [IntPtr]::Zero) {
    Write-Error "window not found: class='$Class' title='$Title'"
    exit 1
}

[CaptureNative]::ShowWindow($hwnd, 5) | Out-Null
[CaptureNative]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 700

$rect = New-Object CaptureNative+RECT
[CaptureNative]::GetClientRect($hwnd, [ref]$rect) | Out-Null
$origin = New-Object CaptureNative+POINT
[CaptureNative]::ClientToScreen($hwnd, [ref]$origin) | Out-Null

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    Write-Error "window client area is empty: ${width}x${height}"
    exit 1
}

$directory = Split-Path -Parent $Out
if ($directory -and -not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($origin.X, $origin.Y, 0, 0, $bitmap.Size)
$graphics.Dispose()
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

Write-Output "captured: $Out (${width}x${height} at $($origin.X),$($origin.Y))"
