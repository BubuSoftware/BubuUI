param(
    # Release for extension use; Debug keeps pdbs so you can break into the server.
    [string]$Configuration = 'Release',
    # Skip npm ci when dependencies are already present.
    [switch]$SkipNpm,
    [switch]$SkipTests
)

# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads .ps1 without a BOM
# as ANSI, and non-ASCII text here turns into mojibake / parse errors.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$serverProject = Join-Path $here 'BubuUI.LanguageServer'
$extensionRoot = Join-Path $here 'vscode'
$serverOutput = Join-Path $extensionRoot 'server'

Write-Host "[1/4] dotnet publish ($Configuration) -> $serverOutput" -ForegroundColor Cyan
dotnet publish $serverProject -c $Configuration -o $serverOutput --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed ($LASTEXITCODE). If VS Code is running the extension, its language server locks server/*.dll (MSB3026/3027). Use Developer: Reload Window or close the window, then retry."
}

# The server derives its tag catalog from BubuUI.Runtime.dll (metadata only, no assembly
# load) and style properties from BubuUI.Rendering.dll. Neither is a build dependency of
# the server, so publish does NOT copy them. Bundle them so a packaged extension also works
# when the open workspace is not this repository.
dotnet build (Join-Path $here '..\src\BubuUI.Rendering') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Runtime/rendering build failed ($LASTEXITCODE)." }
foreach ($name in @('BubuUI.Runtime', 'BubuUI.Rendering')) {
    $assembly = Join-Path $here "..\src\$name\bin\$Configuration\net11.0\$name.dll"
    if (-not (Test-Path -LiteralPath $assembly)) { throw "Required metadata assembly not found: $assembly" }
    Copy-Item -LiteralPath $assembly -Destination $serverOutput -Force
}

if (-not $SkipNpm) {
    Write-Host '[2/4] npm ci (VS Code shell dependencies)' -ForegroundColor Cyan
    Push-Location $extensionRoot
    try {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed ($LASTEXITCODE)" }
    }
    finally { Pop-Location }
}

if (-not $SkipTests) {
    Write-Host '[3/4] shell tests (grammar + extension glue)' -ForegroundColor Cyan
    Push-Location $extensionRoot
    try {
        node test/tokenize.test.js
        if ($LASTEXITCODE -ne 0) { throw "grammar assertions failed ($LASTEXITCODE)" }
        node test/extension.test.js
        if ($LASTEXITCODE -ne 0) { throw "extension assertions failed ($LASTEXITCODE)" }
        node test/template-colors.test.js
        if ($LASTEXITCODE -ne 0) { throw "template palette assertions failed ($LASTEXITCODE)" }
    }
    finally { Pop-Location }

    # The Rider module is a Java/Gradle IntelliJ plugin. A real compile needs JDK 21-24
    # plus a Rider SDK download (Gradle 9 comes from the repo wrapper), but every
    # wiring mistake is visible in text, so the static contract is checked here with
    # plain node (no JVM required).
    Write-Host '[4/4] Rider shell contract (static)' -ForegroundColor Cyan
    $riderRoot = Join-Path $here 'rider'
    Push-Location $riderRoot
    try {
        node test/plugin.test.js
        if ($LASTEXITCODE -ne 0) { throw "Rider shell assertions failed ($LASTEXITCODE)" }
    }
    finally { Pop-Location }

    Write-Host ''
}
Write-Host 'Done. Two ways to use the VS Code shell:' -ForegroundColor Green
Write-Host '  1) Open ide/vscode in VS Code and press F5 (Run Extension).'
Write-Host '  2) code --extensionDevelopmentPath="<repo>\ide\vscode"'
Write-Host ''
Write-Host 'Rider: ide/rider is the lsp4ij plugin; see ide/README.md section 4.'
Write-Host '  Build with JDK 21-24: cd ide/rider; .\gradlew.bat buildPlugin (macOS/Linux: ./build.sh)'
