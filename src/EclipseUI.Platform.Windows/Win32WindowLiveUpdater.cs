using System;
using System.Runtime.Versioning;
using EclipseUI;

namespace EclipseUI.Platform.Windows;

/// <summary>
/// design/20：窗口模板动态属性的回推实现——把 <see cref="WindowComponent"/> 的 U 推送落到
/// 已材料化的 Win32 窗口（<c>SetTitle</c>/<c>SetClientSize</c>，均为既有 Win32Window 能力，
/// 零新平台机制）。窗口关闭后 no-op（IsClosed 守卫）；生命周期随 ClassicDesktopLifetime.Run
/// 的 <c>using</c> 作用域结束自然失效。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32WindowLiveUpdater : IWindowLiveUpdater
{
    private readonly Win32Window _window;

    public Win32WindowLiveUpdater(Win32Window window) => _window = window;

    public void SetTitle(string title)
    {
        if (!_window.IsClosed)
            _window.SetTitle(title);
    }

    public void SetClientSize(double width, double height)
    {
        if (_window.IsClosed) return;
        // 描述口径是逻辑单位（double）；SetClientSize 接收逻辑整数（与创建路径同口径，内部换算 DPI）。
        _window.SetClientSize((int)Math.Round(width), (int)Math.Round(height));
    }
}
