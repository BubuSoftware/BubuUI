using BubuUI;
using BubuUI.Platform.Linux;
using BubuUI.Platform.MacOS;
using BubuUI.Platform.Windows;
using BubuUI.Rendering.Vello;
using BubuUI.Text.Shaping;
using BubuUI.Themes.Fluent;
using BubuApp;

var builder = AppBuilder.Configure<App>()
    .UseVello()
    .UseHarfBuzzText()
    .UseFluentTheme();

if (OperatingSystem.IsWindows()) builder.UseWindows();
else if (OperatingSystem.IsLinux()) builder.UseLinux();
else if (OperatingSystem.IsMacOS()) builder.UseMacOS();
else throw new PlatformNotSupportedException("BubuApp supports Windows, Linux and macOS.");

return builder.Start(args);
