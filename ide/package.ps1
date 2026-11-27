param([switch]$SkipTests, [switch]$SkipNpm, [switch]$CheckOnly)

# Produce local packages only. No marketplace upload or IDE installation.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vscode = Join-Path $PSScriptRoot 'vscode'
$version = (Get-Content (Join-Path $vscode 'package.json') -Raw | ConvertFrom-Json).version
$output = Join-Path $repo "out\ide-release\$version"

# Fail before running tests or building any packages when prerequisites differ.
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Building all three installers requires Windows and Visual Studio MSBuild.'
}
foreach ($command in @('dotnet', 'node', 'npm.cmd')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "Required command not found: $command" }
}
Push-Location $repo
try {
    $sdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^11\.') { throw 'Install the .NET 11 SDK selected by global.json.' }
}
finally { Pop-Location }
$java = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin/java.exe' } else { 'java.exe' }
if (-not (Get-Command $java -ErrorAction SilentlyContinue)) { throw 'Install JDK 21-24 and set JAVA_HOME to its directory.' }
# Java writes its version to stderr; use stdout to support Windows PowerShell 5.1.
$javaVersion = & $java --version
if ($LASTEXITCODE -ne 0 -or ($javaVersion -join "`n") -notmatch '(?m)^(?:openjdk|java) (21|22|23|24)(?:[.\s-])') {
    throw 'Rider packaging requires JDK 21-24. Set JAVA_HOME to a supported JDK.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio 2022 17.14+ or 2026 with the .NET desktop development workload.' }
$msbuild = & $vswhere -latest -products '*' -version '[17.14,)' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild was not found.' }
[xml]$vsManifest = Get-Content (Join-Path $PSScriptRoot 'visualstudio/source.extension.vsixmanifest') -Raw
$riderVersion = (Select-String -LiteralPath (Join-Path $PSScriptRoot 'rider/gradle.properties') -Pattern '^pluginVersion=(.+)$').Matches.Groups[1].Value.Trim()
if ($vsManifest.PackageManifest.Metadata.Identity.Version -ne $version -or $riderVersion -ne $version) {
    throw 'Version mismatch: align vscode/package.json, visualstudio/source.extension.vsixmanifest and rider/gradle.properties before packaging.'
}
if ($SkipNpm -and -not (Test-Path (Join-Path $vscode 'node_modules/.bin/vsce.cmd'))) {
    throw 'VS Code dependencies are missing. Run again without -SkipNpm.'
}
Write-Host "Prerequisites checked. Release $version -> $output" -ForegroundColor Cyan
if ($CheckOnly) { return }
[IO.Directory]::CreateDirectory($output) | Out-Null

if (-not $SkipTests) {
    foreach ($suite in @('EclipseUI.Compiler.Tests', 'EclipseUI.Ide.Tests')) {
        dotnet test (Join-Path $repo "tests\$suite") -c Release --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "$suite failed ($LASTEXITCODE)." }
    }
}
& (Join-Path $PSScriptRoot 'build.ps1') -SkipNpm:$SkipNpm -SkipTests:$SkipTests
if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'visualstudio\verify-server.ps1') -ServerPath (Join-Path $vscode 'server\EclipseUI.LanguageServer.dll')
    & (Join-Path $PSScriptRoot 'visualstudio\verify-server.ps1') -ServerPath (Join-Path $vscode 'server\EclipseUI.LanguageServer.dll') -MaterializeDecompiled
}

Push-Location $vscode
try {
    & '.\node_modules\.bin\vsce.cmd' package --out (Join-Path $output "EclipseUI-VSCode-$version.vsix") --skip-license
    if ($LASTEXITCODE -ne 0) { throw "VS Code packaging failed ($LASTEXITCODE)." }
}
finally { Pop-Location }

# Reuse precisely the same published server in all three shells.
$vsServer = Join-Path $PSScriptRoot 'visualstudio\server'
[IO.Directory]::CreateDirectory($vsServer) | Out-Null
Copy-Item -Path (Join-Path $vscode 'server\*') -Destination $vsServer -Recurse -Force
& (Join-Path $PSScriptRoot 'visualstudio\build.ps1') -SkipPublish -SkipTests:$SkipTests
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'visualstudio\bin\Release\net472\EclipseUI.VisualStudio.vsix') -Destination (Join-Path $output "EclipseUI-VisualStudio-$version.vsix") -Force

Push-Location (Join-Path $PSScriptRoot 'rider')
try {
    $tasks = @('buildPlugin', 'verifyPluginStructure', '--console=plain')
    if (-not $SkipTests) { $tasks = @('test', 'verifyShell') + $tasks }
    & '.\gradlew.bat' @tasks
    if ($LASTEXITCODE -ne 0) { throw "Rider packaging failed ($LASTEXITCODE)." }
    $zip = Join-Path $PSScriptRoot "rider\build\distributions\eclipseui-rider-$version.zip"
    if (-not (Test-Path -LiteralPath $zip)) { throw "Missing Rider package: $zip" }
    Copy-Item -LiteralPath $zip -Destination (Join-Path $output "EclipseUI-Rider-$version.zip") -Force
}
finally { Pop-Location }

& (Join-Path $PSScriptRoot 'verify-packages.ps1') -Directory $output
Get-ChildItem -LiteralPath $output -File | Where-Object Extension -in '.vsix', '.zip' |
    ForEach-Object { $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256; "$($hash.Hash.ToLowerInvariant())  $($_.Name)" } |
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
Write-Host "Local release $version ready: $output" -ForegroundColor Green
