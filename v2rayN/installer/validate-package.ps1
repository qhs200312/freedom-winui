[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path

foreach ($application in @('freedom', 'AmazTool')) {
    $configuration = Join-Path $package "$application.runtimeconfig.json"
    if (!(Test-Path -LiteralPath $configuration -PathType Leaf)) {
        throw "Missing runtime configuration: $application"
    }
    $json = Get-Content -LiteralPath $configuration -Raw | ConvertFrom-Json -AsHashtable
    $runtime = $json['runtimeOptions']
    if ($null -eq $runtime -or $runtime.ContainsKey('framework') -or $runtime.ContainsKey('frameworks')) {
        throw "$application is framework-dependent. Publish with --self-contained true before packaging."
    }
    $included = @($runtime['includedFrameworks'] | ForEach-Object { $_['name'] })
    if ($included -notcontains 'Microsoft.NETCore.App') {
        throw "$application does not declare an included .NET runtime."
    }
    if ($application -eq 'freedom' -and $included -notcontains 'Microsoft.WindowsDesktop.App') {
        throw 'freedom does not declare the included Windows Desktop runtime.'
    }
}

$required = @(
    'freedom.exe', 'freedom.dll', 'freedom.deps.json', 'freedom.pri',
    'AmazTool.exe', 'AmazTool.dll',
    'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll',
    'PresentationFramework.dll', 'System.Windows.Forms.dll',
    'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll',
    'App.xbf', 'MainWindow.xbf', 'Views\UpdateManagerView.xbf', 'Assets\freedom.ico',
    'bin\xray\xray.exe', 'bin\xray\wintun.dll', 'bin\sing_box\sing-box.exe',
    'bin\mihomo\mihomo-windows-amd64-v1.exe', 'bin\geoip.dat', 'bin\geosite.dat',
    'bin\proxifyre\ProxiFyre.exe', 'bin\proxifyre\.freedom-managed',
    'prerequisites\Windows.Packet.Filter.3.6.2.1.x64.msi', 'prerequisites\VC_redist.x64.exe'
)
foreach ($relative in $required) {
    $file = Join-Path $package $relative
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Missing or empty installer payload: $relative"
    }
}
$manifest = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $package 'freedom.exe')))
if ($manifest -notmatch '<requestedExecutionLevel\s+level="requireAdministrator"') {
    throw 'The application does not contain its administrator manifest.'
}
Write-Output "Validated self-contained .NET/Desktop runtimes, WinUI resources, cores and UDP prerequisites: $package"
