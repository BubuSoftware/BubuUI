param([Parameter(Mandatory = $true)][string]$Directory)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$version = (Get-Content (Join-Path $PSScriptRoot 'vscode\package.json') -Raw | ConvertFrom-Json).version
$server = Join-Path $PSScriptRoot 'vscode\server'
$expected = @{}
Get-ChildItem -LiteralPath $server -Recurse -File | Where-Object Extension -ne '.pdb' | ForEach-Object {
    $relative = $_.FullName.Substring($server.Length + 1).Replace('\', '/')
    $expected[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}

function Read-Entry($entry) {
    $reader = New-Object IO.StreamReader($entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}
function Check-Grammar($archive, [string]$prefix) {
    foreach ($name in @('bubuui', 'bss')) {
        $entry = $archive.GetEntry("$prefix$name.tmLanguage.json")
        if (-not $entry) { throw "Missing $name grammar." }
        $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "vscode\syntaxes\$name.tmLanguage.json"))
        if ((Read-Entry $entry) -cne $source) { throw "Stale $name grammar." }
    }
}

foreach ($ide in @('VSCode', 'VisualStudio', 'Rider')) {
    $extension = if ($ide -eq 'Rider') { 'zip' } else { 'vsix' }
    $path = Join-Path $Directory "BubuUI-$ide-$version.$extension"
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $dll = @($zip.Entries | Where-Object FullName -like '*server/BubuUI.LanguageServer.dll')
        if ($dll.Count -ne 1) { throw "$ide must contain exactly one bundled server." }
        $prefix = $dll[0].FullName.Substring(0, $dll[0].FullName.Length - 'BubuUI.LanguageServer.dll'.Length)
        foreach ($relative in $expected.Keys) {
            $entry = $zip.Entries | Where-Object { [Uri]::UnescapeDataString($_.FullName) -ceq ($prefix + $relative) } | Select-Object -First 1
            if (-not $entry) { throw "$ide missing server dependency: $relative" }
            $stream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $sha.Dispose() }
            if ($hash -ne $expected[$relative]) { throw "$ide contains stale server file: $relative" }
        }
        if ($ide -eq 'VSCode') {
            $manifest = Read-Entry ($zip.GetEntry('extension/package.json')) | ConvertFrom-Json
            if ($manifest.version -ne $version) { throw 'VS Code version mismatch.' }
            if (-not $zip.GetEntry('extension/node_modules/vscode-languageclient/lib/node/main.js')) { throw 'VS Code client dependency is missing.' }
            Check-Grammar $zip 'extension/syntaxes/'
        }
        elseif ($ide -eq 'VisualStudio') {
            [xml]$manifest = Read-Entry ($zip.GetEntry('extension.vsixmanifest'))
            if ($manifest.PackageManifest.Metadata.Identity.Version -ne $version) { throw 'VS version mismatch.' }
            Check-Grammar $zip 'Grammars/'
        }
        else {
            $jar = $zip.Entries | Where-Object FullName -like "*/lib/bubuui-rider-$version.jar" | Select-Object -First 1
            if (-not $jar) { throw 'Rider plugin JAR is missing.' }
            $memory = New-Object IO.MemoryStream
            $stream = $jar.Open()
            try { $stream.CopyTo($memory) } finally { $stream.Dispose() }
            $memory.Position = 0
            $nested = New-Object IO.Compression.ZipArchive($memory, [IO.Compression.ZipArchiveMode]::Read)
            try {
                [xml]$manifest = Read-Entry ($nested.GetEntry('META-INF/plugin.xml'))
                if ($manifest.'idea-plugin'.version -ne $version) { throw 'Rider version mismatch.' }
                Check-Grammar $nested 'textmate/syntaxes/'
            }
            finally { $nested.Dispose(); $memory.Dispose() }
        }
        Write-Host "$ide $version verified: shared grammars and $($expected.Count) matching server files." -ForegroundColor Green
    }
    finally { $zip.Dispose() }
}
