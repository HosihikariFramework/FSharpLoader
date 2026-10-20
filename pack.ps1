<#
    Assemble the hosihikari-fsharp provider package.

    The package is a plugin package itself: provider DLL, private dependencies and
    manifest. Shared-layer assemblies are excluded by the core packager from its
    canonical list (ADR-0019 / 0026 / 0036).

    Requires a sibling Hosihikari.PluginManager checkout, or HOSIHIKARI_PLUGINMANAGER.

    Usage: pwsh ./pack.ps1 -Version 0.1.1 -OutputRoot artifacts
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
New-Item -ItemType Directory -Path $root -Force | Out-Null

$core = if ($env:HOSIHIKARI_PLUGINMANAGER) {
    $env:HOSIHIKARI_PLUGINMANAGER
} else {
    Join-Path (Split-Path $here -Parent) 'Hosihikari.PluginManager'
}
$packTool = Join-Path $core 'tools/Pack/Hosihikari.Plugin.Pack.csproj'
if (-not (Test-Path $packTool)) {
    throw "找不到核心打包器：$packTool。请并列检出 Hosihikari.PluginManager，或设置 HOSIHIKARI_PLUGINMANAGER。"
}

$manifest = Join-Path ([System.IO.Path]::GetTempPath()) "hosihikari-$name-$Version-manifest.json"
try {
    [ordered]@{
        name         = $name
        entry        = $entry
        type         = 'hosihikari'
        version      = $Version
        dependencies = @(@{ name = 'hosihikari' })
        extraInfo    = [ordered]@{ api = '2'; format_version = '1'; provider = 'assembly' }
    } | ConvertTo-Json -Depth 6 | Set-Content $manifest -Encoding UTF8

    & dotnet run --project $packTool -c Release -- pack-provider $output $manifest $root
    if ($LASTEXITCODE -ne 0) { throw 'the core packager failed' }
} finally {
    Remove-Item $manifest -Force -ErrorAction SilentlyContinue
}

$zip = Join-Path $root "$name-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $package -DestinationPath $zip
Write-Host "packed $zip"
