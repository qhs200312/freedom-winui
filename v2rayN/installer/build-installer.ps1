[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$CoreArchive,
    [string]$UdpCacheDirectory = (Join-Path $PSScriptRoot '..\artifacts\udp-cache'),
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sourceRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $sourceRoot -Parent
$artifactsRoot = Join-Path $sourceRoot 'artifacts'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$buildProps = Get-Content (Join-Path $sourceRoot 'Directory.Build.props')
    $Version = [string]$buildProps.Project.PropertyGroup.Version | Select-Object -First 1
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid installer version: $Version"
}

$workRoot = Join-Path $artifactsRoot "installer\v$Version"
$publishDir = Join-Path $workRoot 'publish'
$updaterDir = Join-Path $workRoot 'updater'
$coreExtractDir = Join-Path $workRoot 'core'
$packageDir = Join-Path $workRoot 'package'
$outputDir = Join-Path $workRoot 'output'

function Reset-GeneratedDirectory([string]$Path) {
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd('\') + '\'
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside artifacts: $resolvedPath"
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedPath -Force | Out-Null
}

Reset-GeneratedDirectory $workRoot
New-Item -ItemType Directory -Path $publishDir, $updaterDir, $coreExtractDir, $packageDir, $outputDir -Force | Out-Null

& dotnet publish (Join-Path $sourceRoot 'v2rayN.WinUI\v2rayN.WinUI.csproj') `
    -c Release -r $RuntimeIdentifier --self-contained true `
    -p:Platform=x64 -p:Version=$Version -p:PublishSingleFile=false -warnaserror -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw 'WinUI publish failed.'
}

& dotnet publish (Join-Path $sourceRoot 'AmazTool\AmazTool.csproj') `
    -c Release -r $RuntimeIdentifier --self-contained true `
    -p:Version=$Version -p:PublishSingleFile=true -warnaserror -o $updaterDir
if ($LASTEXITCODE -ne 0) {
    throw 'Updater publish failed.'
}

Copy-Item (Join-Path $publishDir '*') $packageDir -Recurse -Force
Copy-Item (Join-Path $updaterDir 'AmazTool.exe') $packageDir -Force

if ([string]::IsNullOrWhiteSpace($CoreArchive)) {
    $coreCache = Join-Path $artifactsRoot 'core'
    New-Item -ItemType Directory -Path $coreCache -Force | Out-Null
    $CoreArchive = Join-Path $coreCache 'v2rayN-windows-64.zip'
    if (-not (Test-Path -LiteralPath $CoreArchive)) {
        $coreUrl = 'https://github.com/2dust/v2rayN-core-bin/raw/refs/heads/master/v2rayN-windows-64.zip'
        Write-Host "Downloading core bundle: $coreUrl"
        Invoke-WebRequest -Uri $coreUrl -OutFile $CoreArchive -UseBasicParsing
    }
}

$CoreArchive = [IO.Path]::GetFullPath($CoreArchive)
if (-not (Test-Path -LiteralPath $CoreArchive -PathType Leaf)) {
    throw "Core archive not found: $CoreArchive"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$archive = [IO.Compression.ZipFile]::OpenRead($CoreArchive)
try {
    foreach ($entry in $archive.Entries) {
        $entryName = $entry.FullName.Replace('\', '/')
        if ($entryName.StartsWith('/') -or $entryName -match '(^|/)\.\.(/|$)') {
            throw "Unsafe core archive entry: $($entry.FullName)"
        }
    }
}
finally {
    $archive.Dispose()
}

[IO.Compression.ZipFile]::ExtractToDirectory($CoreArchive, $coreExtractDir)
$coreContentDir = $coreExtractDir
$coreTopDirectories = @(Get-ChildItem $coreExtractDir -Directory)
$coreTopFiles = @(Get-ChildItem $coreExtractDir -File)
$hasSingleWrappedRoot = $coreTopDirectories.Count -eq 1 `
    -and $coreTopFiles.Count -eq 0 `
    -and (Test-Path -LiteralPath (Join-Path $coreTopDirectories[0].FullName 'bin') -PathType Container)
if ($hasSingleWrappedRoot) {
    $coreContentDir = $coreTopDirectories[0].FullName
}
Copy-Item (Join-Path $coreContentDir '*') $packageDir -Recurse -Force

$mihomoDirectory = Join-Path $packageDir 'bin\mihomo'
$mihomoSource = Join-Path $mihomoDirectory 'mihomo.exe'
$mihomoCompatibleName = Join-Path $mihomoDirectory 'mihomo-windows-amd64-v1.exe'
$needsMihomoCompatibleName = (Test-Path -LiteralPath $mihomoSource -PathType Leaf) `
    -and -not (Test-Path -LiteralPath $mihomoCompatibleName -PathType Leaf)
if ($needsMihomoCompatibleName) {
    Copy-Item -LiteralPath $mihomoSource -Destination $mihomoCompatibleName
}

$requiredFiles = @(
    'freedom.exe',
    'freedom.pri',
    'App.xbf',
    'MainWindow.xbf',
    'Views\UpdateManagerView.xbf',
    'Assets\freedom.ico',
    'AmazTool.exe',
    'bin\xray\xray.exe',
    'bin\xray\wintun.dll',
    'bin\sing_box\sing-box.exe',
    'bin\mihomo\mihomo-windows-amd64-v1.exe',
    'bin\geoip.dat',
    'bin\geosite.dat'
)
& (Join-Path $PSScriptRoot 'prepare-udp-components.ps1') -PackageDirectory $packageDir -CacheDirectory $UdpCacheDirectory
$requiredFiles += @(
    'bin\proxifyre\ProxiFyre.exe',
    'bin\proxifyre\.freedom-managed',
    'prerequisites\Windows.Packet.Filter.3.6.2.1.x64.msi',
    'prerequisites\VC_redist.x64.exe'
)
foreach ($relativePath in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageDir $relativePath) -PathType Leaf)) {
        throw "Installer dependency is missing: $relativePath"
    }
}

& (Join-Path $PSScriptRoot 'validate-package.ps1') -PackageDirectory $packageDir

Get-ChildItem $packageDir -Recurse -File | Where-Object {
    $_.Name -match '^(cache\.db|.*\.db-(shm|wal))$'
} | Remove-Item -Force

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $isccCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup compiler was not found. Install Inno Setup 6 or 7, or pass -IsccPath.'
}

$installerScript = Join-Path $PSScriptRoot 'v2rayN.iss'
& $IsccPath "/DMyAppVersion=$Version" "/DSourceDir=$packageDir" "/DOutputDir=$outputDir" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw 'Installer compilation failed.'
}

$setupPath = Join-Path $outputDir 'freedom-windows-64-setup.exe'
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Installer output not found: $setupPath"
}

$hash = Get-FileHash -LiteralPath $setupPath -Algorithm SHA256
[PSCustomObject]@{
    Installer = $setupPath
    Bytes = (Get-Item -LiteralPath $setupPath).Length
    SHA256 = $hash.Hash
}
