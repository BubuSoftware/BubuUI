# Fluent control gallery

The desktop app opens one Fluent interactive gallery. All layout and fourteen control examples are declared in `.ui` files; `@code` contains interaction and state logic.

```sh
dotnet run --project samples/Gallery.Desktop
dotnet run --project samples/Gallery.Desktop -- --theme light
dotnet run --project samples/Gallery.Desktop -- --cpu --theme dark --shot out/fluent-gallery/dark.png
```

`MainWindow.ui` hosts `FluentGalleryPage.ui`. `FluentControlExamples.ui` declares the controls, and `FluentExampleRow.ui` provides each label and control slot. The Fluent theme is applied by the desktop app builder.

`FluentGalleryPage.ui` also owns the theme button event, current mode and System → Light → Dark cycle. `GalleryApp` supplies the initial mode and a host theme callback; it contains no theme switching logic.

Buttons report clicks, fields accept edits, radios share a group, switches update their captions, and the slider updates the progress bar. Reset restores initial values and releases removed controls, including open popups. Keyboard focus reveals controls when needed. Theme cycles through System, Light and Dark.

The layout fits a 360 DIP viewport without horizontal scrolling. The default window is 720×960. Hover, press and focus states are shown by interacting with the controls directly.

The old compiler, reactivity, styles and layout pages have been removed from the app. Their regression fixtures now live under `tests/GalleryFixtures`, where existing framework and IDE tests continue to exercise them.

Gallery regression tests cover input, reset, focus, theme changes, image-free drawing and narrow-window layout. `sandbox/GalleryCheck` validates the Fluent page and window resize lifecycle, `sandbox/ControlCheck` checks input and reset, and `sandbox/FluentCheck` exports light/dark control states from the same UI descriptions.
