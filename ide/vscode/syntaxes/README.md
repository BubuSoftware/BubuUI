# Embedded C# grammar

`csharp.tmLanguage.json` is vendored from the MIT-licensed
[dotnet/csharp-tmLanguage](https://github.com/dotnet/csharp-tmLanguage/tree/61acf50cb1c7fcac8097249983dddfba56023608)
at commit `61acf50cb1c7fcac8097249983dddfba56023608`.
The original license is retained in `csharp-LICENSE.txt` and shipped in all IDE packages.

The upstream `grammars/csharp.tmLanguage` plist is converted to JSON. Only its
display name and registration scope are changed, its `fileTypes` registration
is removed, and provenance metadata is added. The private
`source.bubuui-csharp` scope avoids taking over the host's `.cs` documents;
the original `.cs` token scopes still use C# theme rules.

The template grammar includes `class-or-struct-members` for `@code` and
`expression` for expression islands. Nested blocks, comments, interpolations,
verbatim strings and raw strings are handled by the C# grammar. Roslyn semantic
tokens supply symbol classification when project information is available.

VS Code and Rider register the bundled grammar alongside the template and
stylesheet grammars. Visual Studio additionally maps its C# scopes to native
classifications in `ide/visualstudio/Grammars/bubuui.tmLanguage.tmTheme`.

After updating the pinned grammar, run `npm test` in `ide/vscode`, the Rider
shell contract checks, and the Visual Studio package verification.
