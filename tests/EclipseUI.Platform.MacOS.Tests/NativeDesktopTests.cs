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
    [MacOSDesktopFact]
    public Task ModalPopupInAChildWindowRestrictsInputAndRestoresOwnerFocus()
        => RunNativeScenario("popup-modal");

    [MacOSDesktopFact]
    public Task NestedModalPopupsAllowTheirDropdownAndRestoreThePreviousModal()
        => RunNativeScenario("popup-modal-nested");

    [MacOSDesktopFact]
    public Task PopupModalityCanChangeAndSurvivesFocusLossUntilExplicitShutdown()
        => RunNativeScenario("popup-modal-toggle");

    [MacOSDesktopFact]
    public Task ClosingTheMainWindowKeepsItsIndependentDialogRunning()
        => RunNativeScenario("managed-windows");

    [MacOSDesktopFact]
    public Task ApplicationShutdownClosesEveryManagedNativeWindow()
        => RunNativeScenario("managed-window-quit");

    [MacOSDesktopFact]
    public Task ClosingAMiddleWindowRestoresTheNearestLiveHostAndTheme()
        => RunNativeScenario("managed-window-three-level");

    [MacOSDesktopFact]
    public Task OwnedPopupPresentsOutsideOwnerAndRoutesNestedInput()
        => RunNativeScenario("popup");

    [MacOSDesktopFact]
    public Task PopupHostPreservesFocusEditsTextAndClosesTemplatedDropdown()
        => RunNativeScenario("popup-host");

    [MacOSDesktopFact]
    public Task RoundedPopupKeepsNativeWindowAndMetalLayerTransparentAcrossResize()
        => RunNativeScenario("popup-alpha");

    [MacOSDesktopFact]
    public Task DropdownShadowHasSmoothFalloffOnLightAndDarkNativeBackdrops()
        => RunNativeScenario("popup-shadow");

    [MacOSDesktopFact]
    public Task SoftwareDropdownShadowHasSmoothFalloffOnLightAndDarkNativeBackdrops()
        => RunNativeScenario("popup-shadow-software");

    [MacOSDesktopFact]
    public Task SoftwareRasterUploadsAndPresentsThroughMetalAcrossResize()
        => RunNativeScenario("software-raster");

    [MacOSDesktopFact]
    public Task MetalRetainsPixelsAndRasterizesOnlyDamagedRegions()
        => RunNativeScenario("partial-raster");

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

    [MacOSDesktopFact]
    public Task ExposeWatchReflowsAndPresentsWithoutPollingAndIgnoresOtherThreadsAndWindows()
        => RunNativeScenario("live-resize");

    private static async Task RunNativeScenario(string scenario)
    {
        if (scenario.StartsWith("popup-modal", StringComparison.Ordinal))
        {
            await RunApplicationScenario(scenario);
            return;
        }
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

    private static async Task RunApplicationScenario(string scenario)
    {
        // Unbundled dotnet subprocesses can be denied foreground activation by Cocoa.
        // Launch the focus/modal scenarios with their own application identity, as users do.
        var folder = Path.Combine(Path.GetTempPath(), "eclipseui-native-" + Guid.NewGuid().ToString("N"));
        var bundle = Path.Combine(folder, "Modal Popup Probe.app");
        var contents = Path.Combine(bundle, "Contents");
        var executable = Path.Combine(contents, "MacOS", "EclipseUINativeTests");
        var outputFile = Path.Combine(folder, "stdout.log");
        var errorFile = Path.Combine(folder, "stderr.log");
        var pidFile = Path.Combine(folder, "pid");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        try
        {
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                ?? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                    .Select(path => Path.Combine(path, "dotnet")).First(File.Exists);
            File.CreateSymbolicLink(executable, Path.GetFullPath(dotnet));
            await File.WriteAllTextAsync(Path.Combine(contents, "Info.plist"), """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict>
                  <key>CFBundleExecutable</key><string>EclipseUINativeTests</string>
                  <key>CFBundleIdentifier</key><string>local.eclipseui.tests.modalpopup</string>
                  <key>CFBundleName</key><string>Modal Popup Probe</string>
                  <key>CFBundlePackageType</key><string>APPL</string>
                  <key>NSHighResolutionCapable</key><true/>
                </dict></plist>
                """);
            var start = new ProcessStartInfo("/usr/bin/open")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-n", "-W", "--stdout", outputFile, "--stderr", errorFile,
                "--env", "ECLIPSEUI_NATIVE_PID_FILE=" + pidFile, bundle, "--args",
                typeof(NativeDesktopTests).Assembly.Location, "--native-smoke", scenario })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var launchOutput = process.StandardOutput.ReadToEndAsync();
            var launchErrors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (File.Exists(pidFile) && int.TryParse(await File.ReadAllTextAsync(pidFile), out var ownPid))
                {
                    try { using var ownApp = Process.GetProcessById(ownPid); ownApp.Kill(entireProcessTree: true); }
                    catch (ArgumentException) { }
                }
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                Assert.Fail($"Native application scenario '{scenario}' timed out.");
            }
            var output = File.Exists(outputFile) ? await File.ReadAllTextAsync(outputFile) : "";
            var errors = File.Exists(errorFile) ? await File.ReadAllTextAsync(errorFile) : "";
            Assert.True(process.ExitCode == 0 && output.Contains($"macOS native {scenario}: passed", StringComparison.Ordinal),
                $"Native application scenario '{scenario}' failed.\n{await launchOutput}\n{await launchErrors}\n{output}\n{errors}");
        }
        finally
        {
            // Remove the temporary application from LaunchServices before deleting its files.
            using var unregister = Process.Start(new ProcessStartInfo(
                "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister")
                { UseShellExecute = false, ArgumentList = { "-u", bundle }, RedirectStandardOutput = true, RedirectStandardError = true });
            if (unregister is not null) await unregister.WaitForExitAsync();
            Directory.Delete(folder, recursive: true);
        }
    }
}
