using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace EclipseUI.LanguageServer.Workspace;

/// <summary>
/// L1 的启动前提（design/06 §6.9.3）：MSBuildLocator 必须在**任何** Microsoft.Build 类型
/// 被 JIT 解析之前注册一次。server 由 <c>dotnet</c> 启动，SDK 必在；求值用的是 SDK 里
/// 那份 Microsoft.Build.dll，编译期契约锁 17.11.4（最老支持 SDK），版本倒挂靠
/// <c>ExcludeAssets=runtime</c> 规避（R1，2026-11-11 spike 实测通过）。
///
/// <para><c>ExcludeAssets=runtime</c> 意味着输出目录里<strong>没有</strong>
/// Microsoft.Build.dll，全靠 Locator 注册的解析回调从 SDK 目录加载。CLR 在 JIT 编译
/// 一个方法时就会解析该方法体内用到的类型标记，所以注册必须发生在**任何引用
/// Microsoft.Build 类型的方法被 JIT 之前**：调用点放在单独的方法（构造函数）里，
/// 且 <see cref="Ensure"/> 自身禁止内联——否则它会被内联进 <c>Discover()</c>，
/// 解析仍发生在注册之前，启动时直接 FileNotFoundException。</para>
/// </summary>
public static class MsBuildEnvironment
{
    private static readonly object Gate = new();
    private static bool _registered;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ensure()
    {
        lock (Gate)
        {
            if (_registered) return;
            if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
            _registered = true;
        }
    }
}
