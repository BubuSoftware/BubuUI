# BubuApp

A BubuUI desktop application for Windows, Linux and macOS. Requires the .NET 11 SDK.

```sh
dotnet run
```

- `Program.cs` selects the platform and configures Vello rendering, HarfBuzz text and the Fluent theme.
- `App.cs` creates the main window.
- `MainWindow.bui` defines the window and an interactive counter. The source generator automatically discovers `.bui` and `.bss` files.
- Update `BubuUIVersion` in the project file to upgrade all BubuUI packages together.

Linux requires SDL3 3.2+, the Vulkan loader and a working Vulkan driver, and fonts such as DejaVu Sans or Noto.
On macOS, install the native dependencies with `brew install sdl3 harfbuzz`. Windows uses the packaged native dependencies.

See the [BubuUI documentation](https://github.com/BubuSoftware/BubuUI) for platform setup and controls.
