# BubuUI

Language support for BubuUI `.bui` templates and `.bss` stylesheets.

- Diagnostics, completion, hover, definition, rename and document symbols.
- Component `@style` blocks, inline `Style` declarations, variables and registered states.
- Control themes, typed selectors, template boundaries, template factories and theme resources.
- Embedded C# editing through the shared BubuUI language server.

Version 0.2.5 uses the same template palette as Visual Studio and Rider: teal
types/tags, blue attributes, purple directives, red literals, green comments and
gray punctuation, with separate light/dark colours. `@code` and expressions keep
the host's C# syntax and semantic highlighting. The palette is bundled with the
extension and does not change your global editor theme.

Requires the .NET 11 SDK for project analysis. The language server and standalone
metadata catalog are bundled. Open the folder containing your `.csproj` and include
`.bui` and `.bss` sources as `AdditionalFiles` in the project.

Version 0.2.6 reduces completion latency while typing: full generation/diagnostics
are deferred for markup requests, unchanged compiler inputs and generated mappings
are reused, C# completion keeps immutable document snapshots with stable IDs, and
template colours reuse unchanged line tokens. Initial project discovery and the
first C# completion still need to warm up.

Install the local release with **Extensions: Install from VSIX**. Server path and
runtime metadata overrides are available in the BubuUI settings.

See the [repository](https://github.com/CeSun/BubuUI) for language documentation.
