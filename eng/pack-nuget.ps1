[CmdletBinding()]
param(
    [ValidateSet('Desktop', 'Android', 'iOS')]
    [string] $Group = 'Desktop',
    [string] $OutputPath = 'out/nuget',
    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projects = switch ($Group) {
    'Desktop' {
        'BubuUI.Compiler.Generator'
        'BubuUI.Runtime'
        'BubuUI.Rendering'
        'BubuUI.Text.Shaping'
        'BubuUI.Rendering.Vello'
        'BubuUI.Platform.Windows'
        'BubuUI.Platform.Linux'
        'BubuUI.Platform.MacOS'
        'BubuUI.Themes.Fluent'
        'BubuUI.Templates'
    }
    'Android' { 'BubuUI.Platform.Android' }
    'iOS' { 'BubuUI.Platform.iOS' }
}

Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $output = [IO.Path]::GetFullPath($OutputPath)
    foreach ($project in $projects) {
        dotnet pack "src/$project/$project.csproj" --configuration Release --output $output -p:ContinuousIntegrationBuild=true
        if ($LASTEXITCODE -ne 0) { throw "Packing $project failed." }

        # Read the version calculated by MinVer, rather than overriding it from the tag.
        $version = dotnet msbuild "src/$project/$project.csproj" -target:MinVer -getProperty:PackageVersion -verbosity:quiet
        if ($LASTEXITCODE -ne 0) { throw "Reading the MinVer version for $project failed." }
        $nuspec = Join-Path "src/$project/obj/Release" "$project.$version.nuspec"
        [xml] $manifest = Get-Content -LiteralPath $nuspec -Raw
        $version = $manifest.package.metadata.version
        if ($ExpectedVersion -and $version -cne $ExpectedVersion) {
            throw "$project version '$version' does not match the release tag '$ExpectedVersion'."
        }
        if (-not (Test-Path -LiteralPath (Join-Path $output "$project.$version.nupkg"))) {
            throw "Missing package for $project $version."
        }
    }
}
finally {
    Pop-Location
}
