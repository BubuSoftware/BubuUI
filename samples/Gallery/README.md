# Fluent control gallery

Gallery is a shared UI library used by the Gallery.Desktop, Gallery.Android and Gallery.iOS entry projects. All layout and fourteen control examples are declared in `.ui` files; `@code` contains interaction and state logic.

```sh
dotnet run --project samples/Gallery.Desktop
dotnet run --project samples/Gallery.Desktop -- --theme light
dotnet run --project samples/Gallery.Desktop -- --cpu --theme dark --shot out/fluent-gallery/dark.png
```

Build the Android entry project (requires the .NET 11 Android workload, SDK, JDK and `ANDROID_NDK_HOME`):

```sh
dotnet publish samples/Gallery.Android -c Release
```

Run Gallery on an Apple silicon Mac with matching Xcode and an iOS simulator. Replace `YOUR_SIMULATOR_UDID` with the identifier of a simulator from the list.

```sh
dotnet workload install ios
cd samples/Gallery.iOS
xcrun simctl list devices available
dotnet run -r iossimulator-arm64 --device YOUR_SIMULATOR_UDID
```

`Gallery.Desktop/Program.cs` selects Windows, Linux or macOS. `Gallery.Android/MainApplication.cs` and `MainActivity.cs` provide the Android lifecycle. `Gallery.iOS/Program.cs` configures the UIKit scene host. Select the entry project for the platform you want to debug. Each entry references the same `net11.0` Gallery library. The desktop entry and verification tools use `Gallery.Offscreen` for snapshots.

`MainWindow.ui` hosts `FluentGalleryPage.ui`. `FluentControlExamples.ui` declares the controls, and `FluentExampleRow.ui` provides each label and control slot. All three platform entries apply the Fluent theme.

`FluentGalleryPage.ui` also owns the theme button event, current mode and System → Light → Dark cycle. `GalleryApp` supplies the initial mode and a host theme callback; it contains no theme switching logic.

Buttons report clicks, fields accept edits, radios share a group, switches update their captions, and the slider updates the progress bar. Reset restores initial values and releases removed controls, including open popups. Keyboard focus reveals controls when needed. Theme cycles through System, Light and Dark.

The layout fits a 360 DIP viewport without horizontal scrolling. The default window is 720×960. Hover, press and focus states are shown by interacting with the controls directly.

The old compiler, reactivity, styles and layout pages have been removed from the app. Their regression fixtures now live under `tests/GalleryFixtures`, where existing framework and IDE tests continue to exercise them.

Gallery regression tests cover input, reset, focus, theme changes, image-free drawing and narrow-window layout. `sandbox/GalleryCheck` validates the Fluent page and window resize lifecycle, `sandbox/ControlCheck` checks input and reset, and `sandbox/FluentCheck` exports light/dark control states from the same UI descriptions.
