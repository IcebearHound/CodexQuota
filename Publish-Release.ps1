param([Parameter(Mandatory=$true)][string]$OutputDirectory, [string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Quota.Desktop/Quota.Desktop.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$output = [IO.Path]::GetFullPath($OutputDirectory)
$name = "CodexQuota-$version-win-x64"
$payload = Join-Path $output $name
if (Test-Path -LiteralPath $payload) { throw 'Use a fresh output directory; existing releases are never overwritten.' }
New-Item -ItemType Directory -Force -Path $payload | Out-Null
& $Dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -p:IncludeSourceRevisionInInformationalVersion=false -o $payload
if ($LASTEXITCODE) { throw 'Desktop publish failed' }
$deps = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Quota.Desktop/bin/Release/net10.0-windows/win-x64/CodexQuota.deps.json') -Raw | ConvertFrom-Json
$runtimeVersion = (($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/*' }) -split '/')[-1]
$desktopVersion = (($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/*' }) -split '/')[-1]
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $payload 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $cache "microsoft.netcore.app.runtime.win-x64/$runtimeVersion/LICENSE.TXT") -Destination (Join-Path $payload 'DOTNET-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $cache "microsoft.netcore.app.runtime.win-x64/$runtimeVersion/THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $payload 'DOTNET-THIRD-PARTY-NOTICES.txt')
Copy-Item -LiteralPath (Join-Path $cache "microsoft.windowsdesktop.app.runtime.win-x64/$desktopVersion/LICENSE") -Destination (Join-Path $payload 'WINDOWSDESKTOP-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/portable.md') -Destination (Join-Path $payload 'README.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/portable.en.md') -Destination (Join-Path $payload 'README.en.md')
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\','/') }) + @('install-files.json')
$manifest = @{ Format=1; Version=[string]$version; Files=@($files | Sort-Object) } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $payload 'install-files.json'), $manifest)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $output ($name + '.zip')
if (Test-Path -LiteralPath $zip) { throw 'Release ZIP already exists' }
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
Write-Output $zip
