param(
    # Release for extension use; Debug keeps pdbs so you can break into the server.
    [string]$Configuration = 'Release',
    # Skip npm install when dependencies are already present.
    [switch]$SkipNpm
)

# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads .ps1 without a BOM
# as ANSI, and non-ASCII text here turns into mojibake / parse errors.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$serverProject = Join-Path $here 'EclipseUI.LanguageServer'
$extensionRoot = Join-Path $here 'vscode'
$serverOutput = Join-Path $extensionRoot 'server'

Write-Host "[1/3] dotnet publish ($Configuration) -> $serverOutput" -ForegroundColor Cyan
dotnet publish $serverProject -c $Configuration -o $serverOutput --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed ($LASTEXITCODE). If VS Code is running the extension, its language server locks server/*.dll (MSB3026/3027). Use Developer: Reload Window or close the window, then retry."
}

# The server derives its tag catalog from EclipseUI.Runtime.dll (metadata only, no assembly
# load) and style properties from EclipseUI.Rendering.dll. Neither is a build dependency of
# the server, so publish does NOT copy them. Bundle them so a packaged extension also works
# when the open workspace is not this repository.
function Copy-LatestAssembly($projectName, $fileName) {
    $bin = Join-Path $here "..\src\$projectName\bin"
    if (-not (Test-Path $bin)) { return $false }
    $found = Get-ChildItem -Path $bin -Filter $fileName -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $found) { return $false }
    Copy-Item -Path $found.FullName -Destination (Join-Path $serverOutput $fileName) -Force
    Write-Host "  bundled $fileName" -ForegroundColor DarkGray
    return $true
}

$runtimeOk = Copy-LatestAssembly 'EclipseUI.Runtime' 'EclipseUI.Runtime.dll'
$renderingOk = Copy-LatestAssembly 'EclipseUI.Rendering' 'EclipseUI.Rendering.dll'
if (-not $runtimeOk) {
    Write-Host 'WARNING: EclipseUI.Runtime.dll not found. Build the solution first; otherwise tag completion and semantic diagnostics are disabled.' -ForegroundColor Yellow
}
if (-not $renderingOk) {
    Write-Host 'WARNING: EclipseUI.Rendering.dll not found. Style= property validation will be incomplete.' -ForegroundColor Yellow
}

if (-not $SkipNpm) {
    Write-Host '[2/3] npm install (VS Code shell dependencies)' -ForegroundColor Cyan
    Push-Location $extensionRoot
    try {
        npm install --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm install failed ($LASTEXITCODE)" }
    }
    finally { Pop-Location }
}

Write-Host '[3/3] shell tests (grammar + extension glue)' -ForegroundColor Cyan
Push-Location $extensionRoot
try {
    node test/tokenize.test.js
    if ($LASTEXITCODE -ne 0) { throw "grammar assertions failed ($LASTEXITCODE)" }
    node test/extension.test.js
    if ($LASTEXITCODE -ne 0) { throw "extension assertions failed ($LASTEXITCODE)" }
}
finally { Pop-Location }

Write-Host ''
Write-Host 'Done. Two ways to use it:' -ForegroundColor Green
Write-Host '  1) Open ide/vscode in VS Code and press F5 (Run Extension).'
Write-Host '  2) code --extensionDevelopmentPath="<repo>\ide\vscode"'
Write-Host ''
Write-Host 'Rider: see ide/README.md (lsp4ij config, same server, no code changes).'
