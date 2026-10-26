[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$ArchivePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$expectedHash = '4c45907468d5d86ab880730402312f1906fbca1e6bb6e7b6f359ad33476768ab'
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Nowhere 3.0.0 Windows x64 archive SHA-256 mismatch.'
}
$destination = Join-Path ([IO.Path]::GetFullPath($PackageDirectory)) 'bin\nowhere'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
try {
    $entries = @($archive.Entries | Where-Object FullName -eq 'nowhere.exe')
    if ($entries.Count -ne 1 -or $entries[0].Length -eq 0) {
        throw 'Expected exactly one root Nowhere executable.'
    }
    [IO.Compression.ZipFileExtensions]::ExtractToFile(
        $entries[0], (Join-Path $destination 'nowhere.exe'), $true)
} finally {
    $archive.Dispose()
}
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination (Join-Path $destination 'LICENSE')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NOWHERE-COMPONENT.md') -Destination (Join-Path $destination 'README-freedom.md')
Write-Output "Staged verified Nowhere 3.0.0 Windows x64 in $destination."
