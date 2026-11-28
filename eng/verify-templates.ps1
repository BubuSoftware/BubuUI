[CmdletBinding()]
param([string] $PackageDirectory = 'out/nuget')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
    $templates = @(Get-ChildItem -LiteralPath $packageRoot -Filter 'BubuUI.Templates.*.nupkg')
    if ($templates.Count -ne 1) { throw "Expected one BubuUI.Templates package; found $($templates.Count)." }

    $archive = [IO.Compression.ZipFile]::OpenRead($templates[0].FullName)
    try {
        $entry = $archive.GetEntry('BubuUI.Templates.nuspec')
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml] $manifest = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $version = $manifest.package.metadata.version
        if ($manifest.package.metadata.packageTypes.packageType.name -ne 'Template') {
            throw 'The package is not marked as a dotnet new template.'
        }
    }
    finally { $archive.Dispose() }

    # Isolate both template registration and generated project from this repository.
    $smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('bubuui-template-' + [guid]::NewGuid().ToString('N'))
    $hive = Join-Path $smokeRoot 'hive'
    $app = Join-Path $smokeRoot 'app'
    New-Item -ItemType Directory -Path $smokeRoot | Out-Null
    dotnet new install $templates[0].FullName --debug:custom-hive $hive
    if ($LASTEXITCODE -ne 0) { throw 'Template installation failed.' }
    dotnet new bubuui --name Bubu.TemplateSmoke --output $app --debug:custom-hive $hive
    if ($LASTEXITCODE -ne 0) { throw 'Template creation failed.' }

    $project = Join-Path $app 'Bubu.TemplateSmoke.csproj'
    [xml] $generated = Get-Content -LiteralPath $project -Raw
    if ($generated.Project.PropertyGroup.BubuUIVersion -cne $version) {
        throw "The generated project's framework version must match the template package ($version)."
    }
    foreach ($file in @('Program.cs', 'App.cs', 'MainWindow.bui', '.gitignore', 'README.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $app $file))) { throw "Missing generated file: $file" }
    }
    if (Test-Path -LiteralPath (Join-Path $app '.template.config')) { throw 'Template metadata leaked into the generated app.' }

    $nugetConfig = Join-Path $smokeRoot 'NuGet.Config'
    $escapedPackageRoot = [Security.SecurityElement]::Escape($packageRoot)
    @"
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$escapedPackageRoot" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="local"><package pattern="BubuUI.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $nugetConfig
    dotnet restore $project --configfile $nugetConfig --packages (Join-Path $smokeRoot 'packages')
    if ($LASTEXITCODE -ne 0) { throw 'Restoring the generated app from the new packages failed.' }
    dotnet build $project --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Building the generated app failed.' }
    Write-Output "Template $version installed, generated and built successfully: $app"
}
finally {
    Pop-Location
}
