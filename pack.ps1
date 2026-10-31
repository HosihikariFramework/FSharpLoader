<#
    Assemble the hosihikari-fsharp loader release archive. Extracting it at the
    server root installs the loader into plugins/hosihikari-fsharp/.

    The package is a plugin package itself: loader DLL, private dependencies and
    manifest. Shared-layer assemblies are excluded by the core packager from its
    canonical list (ADR-0019 / 0026 / 0036).

    Requires a sibling PluginManager checkout, or HOSIHIKARI_PLUGINMANAGER.

    Usage: pwsh ./pack.ps1 -Version 1.0.0 -OutputRoot artifacts
#>
param(
    [string]$Version = '0.0.0-dev',
    [string]$OutputRoot = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$name = 'hosihikari-fsharp'
$entry = 'Hosihikari.PluginLoader.FSharp.dll'

& dotnet build (Join-Path $here 'src/Hosihikari.PluginLoader.FSharp.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'the F# loader failed to build' }

$output = Join-Path $here 'src/bin/Release/net10.0'
$root = if ([System.IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $here $OutputRoot }
$packageRoot = Join-Path $root 'plugins'
$package = Join-Path $packageRoot $name
if (Test-Path $package) { Remove-Item $package -Recurse -Force }
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

$core = if ($env:HOSIHIKARI_PLUGINMANAGER) {
    $env:HOSIHIKARI_PLUGINMANAGER
} else {
    Join-Path (Split-Path $here -Parent) 'PluginManager'
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
        extraInfo    = [ordered]@{ api_version = '2'; manifest_version = '1'; loader = 'assembly' }
    } | ConvertTo-Json -Depth 6 | Set-Content $manifest -Encoding UTF8

    & dotnet run --project $packTool -c Release -- pack-loader $output $manifest $packageRoot
    if ($LASTEXITCODE -ne 0) { throw 'the core packager failed' }
} finally {
    Remove-Item $manifest -Force -ErrorAction SilentlyContinue
}

# 包根的 sei.json 是分发元数据，安装时被跳过；加载器依赖核心的 1.x 兼容线。
$sei = Join-Path $root 'sei.json'
[ordered]@{
    schemaVersion = 1
    name          = $name
    version       = $Version
    dependencies  = @(@{ name = 'hosihikari'; version = '[1.2.0,2.0.0)' })
    layout        = 'root'
} | ConvertTo-Json -Depth 4 | Set-Content $sei -Encoding utf8NoBOM

$zip = Join-Path $root "$name-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $packageRoot, $sei -DestinationPath $zip
Write-Host "packed $zip"
