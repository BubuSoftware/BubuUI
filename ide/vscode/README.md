# EclipseUI

Language support for EclipseUI `.ui` templates and `.eustyle` stylesheets.

- Diagnostics, completion, hover, definition, rename and document symbols.
- Component `@style` blocks, inline `Style` declarations, variables and registered states.
- Control themes, typed selectors, template boundaries, template factories and theme resources.
- Embedded C# editing through the shared EclipseUI language server.

Requires the .NET 10 SDK for project analysis. The language server and standalone
metadata catalog are bundled. Open the folder containing your `.csproj` and include
`.ui` and `.eustyle` sources as `AdditionalFiles` in the project.

Install the local release with **Extensions: Install from VSIX**. Server path and
runtime metadata overrides are available in the EclipseUI settings.

See the [repository](https://github.com/CeSun/EclipseUI) for language documentation.
