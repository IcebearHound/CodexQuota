param([string]$PackageDirectory = (Join-Path $PSScriptRoot 'App'))
$ErrorActionPreference = 'Stop'
$manifest = Join-Path ([IO.Path]::GetFullPath($PackageDirectory)) 'AppxManifest.xml'
if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) {
    throw 'Extract the complete Widgets ZIP first, or pass -PackageDirectory pointing to the expanded package.'
}
$developer = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue
if ($developer -ne 1) {
    throw 'This unsigned developer package requires Windows Developer Mode. Enable it in Windows Settings, then rerun this script. The portable ZIP does not require it.'
}
Add-AppxPackage -Register $manifest -ErrorAction Stop
Write-Output 'Registered Codex Quota: one Start menu application with an integrated Windows Widgets provider.'
Write-Output 'Keep this extracted folder in place. Open Codex Quota from Start, then add Codex Quota from the Windows Widgets board.'
