using System.Runtime.CompilerServices;
using BubuUI.LanguageServer.Workspace;
using Microsoft.Build.Locator;
using Xunit;

namespace BubuUI.Ide.Tests;

/// <summary>
/// R1 的回归：ExcludeAssets=runtime 下输出目录没有 Microsoft.Build.dll，全靠 Locator 的
/// 解析回调。如果注册晚于任何引用 MSBuild 类型的方法被 JIT，启动即 FileNotFoundException。
/// 这里刻意把类型加载放进独立的 NoInlining 方法，模拟 server 的启动顺序。
/// </summary>
public class MsBuildEnvironmentTests
{
    [Fact]
    public void Ensure_RegistersBeforeAnyMicrosoftBuildTypeIsJitted()
    {
        MsBuildEnvironment.Ensure();
        Assert.True(MSBuildLocator.IsRegistered);
        Assert.Contains("Microsoft.Build", LoadMicrosoftBuildAssembly());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string LoadMicrosoftBuildAssembly() =>
        typeof(Microsoft.Build.Evaluation.Project).Assembly.FullName!;
}
