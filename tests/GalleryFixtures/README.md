# Gallery regression fixtures

These former Gallery pages are kept only as test inputs for compiler, layout, styles and IDE integration. They are not referenced by the Gallery application or included in its package.

The original `Gallery` namespace and filenames preserve generated component, navigation and rename contracts. `GalleryFixtureInteractions` supplies the old application wiring to regression tests. `GalleryFixtures.csproj` isolates this input project from test-runner dependencies so the IDE can evaluate it like an ordinary application project.

The maintained, runnable examples are the `.bui` files in `samples/Gallery`.
