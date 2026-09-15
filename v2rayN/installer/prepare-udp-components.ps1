[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '..\artifacts\udp-cache')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = [IO.Path]::GetFullPath($PackageDirectory)
$cache = [IO.Path]::GetFullPath($CacheDirectory)
New-Item -ItemType Directory -Path $package, $cache -Force | Out-Null

function Get-VerifiedPayload([string]$Name, [string]$Url, [string]$Sha256) {
    $path = Join-Path $cache $Name
    if (!(Test-Path -LiteralPath $path)) {
        Invoke-WebRequest -Uri $Url -OutFile "$path.partial" -TimeoutSec 240
        if ((Get-FileHash -LiteralPath "$path.partial" -Algorithm SHA256).Hash -ne $Sha256) {
            throw "SHA-256 mismatch: $Name"
        }
        Move-Item -LiteralPath "$path.partial" -Destination $path
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Cached payload SHA-256 mismatch: $Name"
    }
    return $path
}

$zip = Get-VerifiedPayload 'ProxiFyre-v2.6.1-x64.zip' `
    'https://github.com/wiresock/proxifyre/releases/download/v2.6.1/ProxiFyre-v2.6.1-x64.zip' `
    '86b81504d49194e002acae5c4de7e2e84a79b087cbe1669255bb510e4072abef'
$driver = Get-VerifiedPayload 'Windows.Packet.Filter.3.6.2.1.x64.msi' `
    'https://github.com/wiresock/ndisapi/releases/download/v3.6.2/Windows.Packet.Filter.3.6.2.1.x64.msi' `
    '9c388c0b7f189f7fa98720bae2caecf7d64f30910838b80b438ecf8956b8502c'
$runtime = Get-VerifiedPayload 'VC_redist.x64.exe' `
    'https://download.visualstudio.microsoft.com/download/pr/0b44c2d1-8944-4834-a01a-c9a225f8088a/CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B/VC_redist.x64.exe' `
    'cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b'

$staging = Join-Path $cache ("extract-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($entry in $archive.Entries) {
        $target = [IO.Path]::GetFullPath((Join-Path $staging $entry.FullName))
        if (!$target.StartsWith($staging + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe archive entry: $($entry.FullName)"
        }
    }
} finally { $archive.Dispose() }
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $staging)
$engines = @(Get-ChildItem -LiteralPath $staging -Recurse -Filter ProxiFyre.exe)
if ($engines.Count -ne 1) { throw 'Expected exactly one ProxiFyre.exe.' }
$destination = Join-Path $package 'bin\proxifyre'
if ((Test-Path -LiteralPath (Join-Path $destination 'app-config.json')) -and
    !(Test-Path -LiteralPath (Join-Path $destination '.freedom-managed'))) {
    throw 'Refusing to overwrite an unmanaged ProxiFyre configuration.'
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -Path (Join-Path $engines[0].DirectoryName '*') -Destination $destination -Recurse -Force
[IO.File]::WriteAllText((Join-Path $destination '.freedom-managed'), '2.6.1')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UDP-COMPONENTS.md') -Destination $destination
$prerequisites = Join-Path $package 'prerequisites'
New-Item -ItemType Directory -Path $prerequisites -Force | Out-Null
Copy-Item -LiteralPath $driver, $runtime -Destination $prerequisites -Force
Write-Output "Verified UDP components staged in $package. No driver was installed."
