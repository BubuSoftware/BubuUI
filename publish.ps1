<#
.SYNOPSIS
Build local installers for VS Code, Visual Studio and Rider on Windows.
.EXAMPLE
pwsh -File ./publish.ps1
.EXAMPLE
pwsh -File ./publish.ps1 -CheckOnly
.EXAMPLE
pwsh -File ./publish.ps1 -SkipTests -SkipNpm
#>
[CmdletBinding()]
param([switch]$SkipTests, [switch]$SkipNpm, [switch]$CheckOnly)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & (Join-Path $PSScriptRoot 'ide/package.ps1') @PSBoundParameters
}
finally { Pop-Location }
