param([Parameter(Mandatory=$true)][string]$OutputDirectory, [string]$Dotnet='dotnet', [string]$MakeAppx)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$staging = Join-Path $output ('widget-staging-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
& $Dotnet publish (Join-Path $PSScriptRoot 'Quota.WidgetProvider') -c Release -r win-x64 --self-contained true -p:DebugType=None -o $staging
if ($LASTEXITCODE) { throw 'Widget provider publish failed' }
& $Dotnet publish (Join-Path $PSScriptRoot 'Quota.Desktop') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o (Join-Path $staging 'Desktop')
if ($LASTEXITCODE) { throw 'Desktop publish failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Quota.WidgetProvider/Package.appxmanifest') -Destination (Join-Path $staging 'AppxManifest.xml')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Quota.WidgetProvider/Assets') -Destination $staging -Recurse
New-Item -ItemType Directory -Force -Path (Join-Path $staging 'Public') | Out-Null
Copy-Item -LiteralPath (Join-Path $staging 'Assets/Logo44.png') -Destination (Join-Path $staging 'Public/Icon.png')
if (!$MakeAppx) {
    $sdkTools = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.windows.sdk.buildtools'
    $MakeAppx = (Get-ChildItem -Path $sdkTools -Filter makeappx.exe -Recurse | Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1).FullName
}
if (!$MakeAppx) { throw 'Specify the Windows SDK makeappx.exe path with -MakeAppx' }
$package = Join-Path $output 'CodexQuota-Widgets-2.0.0-rc.6-x64.msix'
& $MakeAppx pack /d $staging /p $package /o > (Join-Path $output 'widget-packaging.log')
if ($LASTEXITCODE) { throw 'MSIX packaging failed, see widget-packaging.log' }
Write-Output $package
