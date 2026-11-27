param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipPublish,
    [switch]$SkipTests
)

# ASCII-only so Windows PowerShell 5.1 can read this file without a BOM.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = [IO.Path]::GetFullPath((Join-Path $here '..\..'))
$serverOutput = Join-Path $here 'server'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio 2022 17.14+ or Visual Studio 2026 with MSBuild.' }
$msbuild = & $vswhere -latest -products '*' -version '[17.14,)' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild was not found. Install the .NET desktop development workload.' }

# CLI SDK selection starts at the working directory. Server and tests need
# the repository SDK, even when this script is invoked from visualstudio/.
Push-Location $repo
try {
if (-not $SkipPublish) {
    Write-Host 'Publishing the shared BubuUI language server...' -ForegroundColor Cyan
    dotnet publish (Join-Path $repo 'ide\BubuUI.LanguageServer') -c $Configuration -o $serverOutput --nologo
    if ($LASTEXITCODE -ne 0) { throw "Language server publish failed ($LASTEXITCODE). Close the VS experimental instance if it is locking server files." }

    # These are metadata inputs for standalone documents, not executable server dependencies.
    dotnet build (Join-Path $repo 'src\BubuUI.Rendering') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Runtime/rendering build failed ($LASTEXITCODE)." }
    foreach ($name in @('BubuUI.Runtime', 'BubuUI.Rendering')) {
        $assembly = Join-Path $repo "src\$name\bin\$Configuration\net11.0\$name.dll"
        if (-not (Test-Path -LiteralPath $assembly)) { throw "Required metadata assembly not found: $assembly" }
        Copy-Item -LiteralPath $assembly -Destination $serverOutput -Force
    }
}

if (-not $SkipTests) {
    dotnet test (Join-Path $repo 'tests\BubuUI.VisualStudio.Tests') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Visual Studio shell tests failed ($LASTEXITCODE)." }
    & (Join-Path $here 'verify-server.ps1') -ServerPath (Join-Path $serverOutput 'BubuUI.LanguageServer.dll')
}
}
finally { Pop-Location }

Write-Host 'Building the VSIX with Visual Studio MSBuild...' -ForegroundColor Cyan
# Recreate the manifest too: older VSSDK targets can reuse a stale intermediate
# manifest during an incremental SDK-style build, leaving the VSIX version unchanged.
Push-Location $here
try {
    # Only the net472 VSIX shell uses the .NET 8 SDK from local global.json.
    & $msbuild (Join-Path $here 'BubuUI.VisualStudio.csproj') /restore /t:Rebuild "/p:Configuration=$Configuration" /p:DeployExtension=false /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "VSIX build failed ($LASTEXITCODE)." }
}
finally { Pop-Location }

$vsix = Join-Path $here "bin\$Configuration\net472\BubuUI.VisualStudio.vsix"
if (-not (Test-Path -LiteralPath $vsix)) { throw "VSIX build produced no package at $vsix" }
& (Join-Path $here 'verify-vsix.ps1') -Path $vsix
Write-Host "Ready to install: $vsix" -ForegroundColor Green
