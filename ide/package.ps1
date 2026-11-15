param([switch]$SkipTests, [switch]$SkipNpm)

# Produce local packages only. No marketplace upload or IDE installation.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$vscode = Join-Path $PSScriptRoot 'vscode'
$version = (Get-Content (Join-Path $vscode 'package.json') -Raw | ConvertFrom-Json).version
$output = Join-Path $repo "out\ide-release\$version"
[IO.Directory]::CreateDirectory($output) | Out-Null

if (-not $SkipTests) {
    foreach ($suite in @('EclipseUI.Compiler.Tests', 'EclipseUI.Ide.Tests')) {
        dotnet test (Join-Path $repo "tests\$suite") -c Release --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "$suite failed ($LASTEXITCODE)." }
    }
}
& (Join-Path $PSScriptRoot 'build.ps1') -SkipNpm:$SkipNpm
& (Join-Path $PSScriptRoot 'visualstudio\verify-server.ps1') -ServerPath (Join-Path $vscode 'server\EclipseUI.LanguageServer.dll')
& (Join-Path $PSScriptRoot 'visualstudio\verify-server.ps1') -ServerPath (Join-Path $vscode 'server\EclipseUI.LanguageServer.dll') -MaterializeDecompiled

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
    $tasks = @('verifyShell', 'buildPlugin', 'verifyPluginStructure', '--console=plain')
    if (-not $SkipTests) { $tasks = @('test') + $tasks }
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
