using System.Diagnostics;
using Xunit;

namespace EclipseUI.Platform.MacOS.Tests;

public sealed class MacOSDesktopFactAttribute : FactAttribute
{
    public MacOSDesktopFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires macOS, Cocoa and Metal.";
    }
}

public class NativeDesktopTests
{
    // Test runners use worker threads. A fresh executable gives Cocoa the real OS main thread.
    [MacOSDesktopFact]
    public Task CocoaWindowPresentsResizesAndRoutesUnicodeAndCommandInput()
        => RunNativeScenario("window");

    [MacOSDesktopFact]
    public Task BuilderRunsFullLifetimeAndDefaultFontsDrawCjkAndEmoji()
        => RunNativeScenario("lifetime");

    [MacOSDesktopFact]
    public Task NativeWindowRejectsWorkerThreadCreationAndAccess()
        => RunNativeScenario("thread");

    private static async Task RunNativeScenario(string scenario)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(NativeDesktopTests).Assembly.Location);
        start.ArgumentList.Add("--native-smoke");
        start.ArgumentList.Add(scenario);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail($"Native scenario '{scenario}' timed out.\n{await output}\n{await errors}");
        }
        Assert.True(process.ExitCode == 0,
            $"Native scenario '{scenario}' exited {process.ExitCode}.\n{await output}\n{await errors}");
    }
}
