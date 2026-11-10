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
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

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
