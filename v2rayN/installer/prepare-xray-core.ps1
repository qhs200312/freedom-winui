[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$ArchivePath,
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '..\artifacts\core')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'core-versions.json') -Raw | ConvertFrom-Json
$core = @($manifest.cores | Where-Object name -eq 'Xray')
if ($core.Count -ne 1) { throw 'Expected exactly one pinned Xray release.' }
$core = $core[0]
if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
    $ArchivePath = Join-Path $CacheDirectory "Xray-$($core.version)-windows-64.zip"
    if (!(Test-Path -LiteralPath $ArchivePath)) {
        Invoke-WebRequest -Uri $core.url -OutFile "$ArchivePath.partial" -TimeoutSec 240
        if ((Get-FileHash -LiteralPath "$ArchivePath.partial" -Algorithm SHA256).Hash -ne $core.sha256) {
            throw 'Downloaded Xray archive SHA-256 does not match the pinned release.'
        }
        Move-Item -LiteralPath "$ArchivePath.partial" -Destination $ArchivePath
    }
}
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $core.sha256) {
    throw 'Xray archive SHA-256 does not match the pinned release.'
}

$destination = Join-Path ([IO.Path]::GetFullPath($PackageDirectory)) 'bin\xray'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
try {
    # Only the official root executable, driver and license files replace the bundled core.
    $files = @('xray.exe', 'wintun.dll', 'LICENSE', 'LICENSE-Wintun', 'README.md')
    foreach ($name in $files) {
        $entries = @($archive.Entries | Where-Object FullName -eq $name)
        if ($entries.Count -ne 1 -or $entries[0].Length -eq 0) {
            throw "Missing or duplicate Xray payload: $name"
        }
    }
    foreach ($name in $files) {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry($name), (Join-Path $destination $name), $true)
    }
} finally {
    $archive.Dispose()
}
Write-Output "Staged official Xray $($core.version), verified archive SHA-256 $($core.sha256)."
