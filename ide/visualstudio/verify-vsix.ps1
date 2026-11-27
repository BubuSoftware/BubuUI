param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
try {
    $entries = @($archive.Entries | ForEach-Object { [Uri]::UnescapeDataString($_.FullName.Replace('\', '/')) })
    foreach ($required in @(
        'extension.vsixmanifest', 'BubuUI.VisualStudio.dll', 'BubuUI.pkgdef',
        'Grammars/bubuui.tmLanguage.json', 'Grammars/bss.tmLanguage.json',
        'Grammars/bubuui.tmLanguage.tmTheme',
        'Grammars/bss.tmLanguage.tmTheme',
        'Grammars/csharp.tmLanguage.json', 'Grammars/csharp-LICENSE.txt',
        'Grammars/language-configuration.json',
        'Grammars/bss-language-configuration.json',
        'server/BubuUI.LanguageServer.dll', 'server/BubuUI.LanguageServer.deps.json',
        'server/BubuUI.LanguageServer.runtimeconfig.json', 'server/BubuUI.Runtime.dll',
        'server/BubuUI.Rendering.dll', 'server/Microsoft.CodeAnalysis.CSharp.Features.dll'
    )) {
        if ($entries -notcontains $required) { throw "Incomplete VSIX: missing $required" }
    }
    $hostAssemblies = $entries | Where-Object { $_ -notmatch '/' -and $_ -match '^(Microsoft\.VisualStudio\.|StreamJsonRpc\.|Newtonsoft\.Json\.)' }
    if ($hostAssemblies) { throw "VS host assemblies must not be bundled: $($hostAssemblies -join ', ')" }
    $manifestReader = New-Object IO.StreamReader($archive.GetEntry('extension.vsixmanifest').Open())
    try { [xml]$manifest = $manifestReader.ReadToEnd() } finally { $manifestReader.Dispose() }
    [xml]$sourceManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source.extension.vsixmanifest') -Raw -Encoding UTF8
    foreach ($field in @('Id', 'Version')) {
        if ($manifest.PackageManifest.Metadata.Identity.$field -cne $sourceManifest.PackageManifest.Metadata.Identity.$field) {
            throw "Stale VSIX manifest: $field differs from source.extension.vsixmanifest. Rebuild the VSIX."
        }
    }
    foreach ($name in @('bubuui', 'bss', 'csharp')) {
        $entry = $archive.GetEntry("Grammars/$name.tmLanguage.json")
        $reader = New-Object IO.StreamReader($entry.Open())
        try { $packagedGrammar = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\vscode\syntaxes\$name.tmLanguage.json") -Raw -Encoding UTF8
        if ($packagedGrammar -cne $source) { throw "Packaged $name grammar differs from the shared source." }
    }
    $themeReader = New-Object IO.StreamReader($archive.GetEntry('Grammars/bubuui.tmLanguage.tmTheme').Open())
    try { $packagedTheme = $themeReader.ReadToEnd() } finally { $themeReader.Dispose() }
    $sourceTheme = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Grammars/bubuui.tmLanguage.tmTheme') -Raw -Encoding UTF8
    if ($packagedTheme -cne $sourceTheme) { throw 'Packaged BubuUI color mappings differ from source. Rebuild the VSIX.' }
    $bssThemeReader = New-Object IO.StreamReader($archive.GetEntry('Grammars/bss.tmLanguage.tmTheme').Open())
    try { $bssTheme = $bssThemeReader.ReadToEnd() } finally { $bssThemeReader.Dispose() }
    if ($bssTheme -cne $sourceTheme) { throw 'Bss and BubuUI color mappings must match.' }
    $assemblyBytes = New-Object IO.MemoryStream
    $assemblyStream = $archive.GetEntry('BubuUI.VisualStudio.dll').Open()
    try { $assemblyStream.CopyTo($assemblyBytes) } finally { $assemblyStream.Dispose() }
    try { $assembly = [Reflection.Assembly]::Load($assemblyBytes.ToArray()) } finally { $assemblyBytes.Dispose() }
    $paletteStream = $assembly.GetManifestResourceStream('BubuUI.VisualStudio.palette.json')
    if (-not $paletteStream) { throw 'Missing embedded template palette in VS shell.' }
    $paletteReader = New-Object IO.StreamReader($paletteStream)
    try { $packagedPalette = $paletteReader.ReadToEnd() } finally { $paletteReader.Dispose() }
    $sourcePalette = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../vscode/highlighting/palette.json'))
    if ($packagedPalette -cne $sourcePalette) { throw 'Stale embedded template palette in VS shell.' }
    Write-Host "VSIX $($manifest.PackageManifest.Metadata.Identity.Version) verified: $($entries.Count) entries, shared grammars and bundled server present." -ForegroundColor Green
}
finally { $archive.Dispose() }
