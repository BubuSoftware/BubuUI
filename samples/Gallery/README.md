# Fluent control gallery

Gallery is a shared UI library used by the Gallery.Desktop and Gallery.Android entry projects. All layout and fourteen control examples are declared in `.ui` files; `@code` contains interaction and state logic.

```sh
dotnet run --project samples/Gallery.Desktop
dotnet run --project samples/Gallery.Desktop -- --theme light
dotnet run --project samples/Gallery.Desktop -- --cpu --theme dark --shot out/fluent-gallery/dark.png
```

Build the Android entry project (requires the .NET 11 Android workload, SDK, JDK and `ANDROID_NDK_HOME`):

```sh
dotnet publish samples/Gallery.Android -c Release
```

`Gallery.Desktop/Program.cs` selects the Windows, Linux or macOS platform at runtime. `Gallery.Android/MainApplication.cs` and `MainActivity.cs` provide the Android lifecycle. Both entry projects reference `Gallery.csproj`; shared UI compiles once as a `net11.0` library. Building the desktop entry does not require Android tools.

In Visual Studio, set Gallery.Desktop as the startup project for desktop debugging and hot reload, or Gallery.Android for Android device deployment. Reload the solution after migrating from the single entry project. Gallery itself is a library and cannot be launched. Entry projects use native SDK capabilities rather than advertising MAUI compatibility.

`MainWindow.ui` hosts `FluentGalleryPage.ui`. `FluentControlExamples.ui` declares the controls, and `FluentExampleRow.ui` provides each label and control slot. Both platform builders apply the Fluent theme.

`FluentGalleryPage.ui` also owns the theme button event, current mode and System → Light → Dark cycle. `GalleryApp` supplies the initial mode and a host theme callback; it contains no theme switching logic.

Buttons report clicks, fields accept edits, radios share a group, switches update their captions, and the slider updates the progress bar. Reset restores initial values and releases removed controls, including open popups. Keyboard focus reveals controls when needed. Theme cycles through System, Light and Dark.

The layout fits a 360 DIP viewport without horizontal scrolling. The default window is 720×960. Hover, press and focus states are shown by interacting with the controls directly.

The old compiler, reactivity, styles and layout pages have been removed from the app. Their regression fixtures now live under `tests/GalleryFixtures`, where existing framework and IDE tests continue to exercise them.

Gallery regression tests cover input, reset, focus, theme changes, image-free drawing and narrow-window layout. `sandbox/GalleryCheck` validates the Fluent page and window resize lifecycle, `sandbox/ControlCheck` checks input and reset, and `sandbox/FluentCheck` exports light/dark control states from the same UI descriptions.
