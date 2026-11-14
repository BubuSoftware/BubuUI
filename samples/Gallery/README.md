# Fluent control gallery

The desktop app opens a Fluent interactive gallery. Fourteen rows contain real controls, with no comparison images or cached state previews. The factories are also used by the separate `sandbox/FluentCheck` export tool.

```powershell
dotnet run --project samples/Gallery.Desktop
dotnet run --project samples/Gallery.Desktop -- --theme light
dotnet run --project samples/Gallery.Desktop -- --legacy
dotnet run --project samples/Gallery.Desktop -- --cpu --theme dark --shot out/fluent-gallery/dark.png
```

Buttons report clicks, fields accept edits, radios share a group, switches update their captions, and the slider updates the progress bar. Reset restores initial values and disposes removed controls, including open popups. Keyboard focus reveals controls when needed. Theme cycles through System, Light and Dark.

The label/control layout fits a 360 DIP viewport without horizontal scrolling. The default window is 720×960. Hover, press and focus states are shown by interacting with the controls directly.

`MainWindow.ui` hosts `FluentGalleryPage`. The original compiler, reactivity, styles and layout demos remain in `LegacyWindow.ui` and are accessible with `--legacy`; `sandbox/GalleryCheck` continues to validate that page.

Gallery regression tests cover input, reset, focus, theme changes, image-free drawing and narrow-window layout. Current light/dark and narrow-window screenshots are in `out/fluent-gallery/`.
