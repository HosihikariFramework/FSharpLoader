<#
    Assemble the hosihikari-fsharp provider package.

    The package is a plugin package itself: provider DLL, private dependencies and
    manifest. Shared-layer assemblies come from the Shim package and must not be
    copied here (ADR-0019 / 0026 / 0036).

    Usage: pwsh ./pack.ps1 -Version 0.1.0 -OutputRoot artifacts
#>
param(
    [string]$Version = '0.0.0-dev',
    [string]$OutputRoot = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$name = 'hosihikari-fsharp'
$entry = 'Hosihikari.PluginProvider.FSharp.dll'

& dotnet build (Join-Path $here 'src/Hosihikari.PluginProvider.FSharp.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'the F# provider failed to build' }

$output = Join-Path $here 'src/bin/Release/net10.0'
$root = if ([System.IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $here $OutputRoot }
$package = Join-Path $root $name
if (Test-Path $package) { Remove-Item $package -Recurse -Force }
New-Item -ItemType Directory -Path $package -Force | Out-Null

$shared = @(
    'Hosihikari.Abstractions.dll',
    'Hosihikari.Abstractions.pdb',
    'Hosihikari.Abstractions.xml',
    'Microsoft.Extensions.Configuration.Abstractions.dll',
    'Microsoft.Extensions.DependencyInjection.Abstractions.dll',
    'Microsoft.Extensions.Logging.Abstractions.dll',
    'Microsoft.Extensions.Primitives.dll'
)

foreach ($file in Get-ChildItem $output -Recurse -File) {
    if ($shared -contains $file.Name) { continue }
    $relative = $file.FullName.Substring($output.Length).TrimStart([char[]]@('\', '/'))
    $target = Join-Path $package $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item $file.FullName $target -Force
}

$manifest = [ordered]@{
    name         = $name
    entry        = $entry
    type         = 'hosihikari'
    version      = $Version
    dependencies = @(@{ name = 'hosihikari' })
    extraInfo    = [ordered]@{ api = '2'; format_version = '1'; provider = 'assembly' }
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $package 'manifest.json') -Encoding UTF8

New-Item -ItemType Directory -Path $root -Force | Out-Null
$zip = Join-Path $root "$name-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $package -DestinationPath $zip
Write-Host "packed $zip"
