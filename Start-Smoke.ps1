param([Parameter(Mandatory=$true)][string]$ApplicationPath,[ValidateRange(1,8760)][int]$CacheAgeHours=1,[switch]$HoldRequests,[switch]$Offscreen)
$ErrorActionPreference='Stop'
$application=(Resolve-Path -LiteralPath $ApplicationPath).Path
$fake=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'Quota.Tests/bin/Release/net10.0/Quota.Tests.exe')).Path
$data=Join-Path ([IO.Path]::GetTempPath()) ('CodexQuotaSmoke-'+[guid]::NewGuid().ToString('N'))
$current=Join-Path $data 'current-profile'
New-Item -ItemType Directory -Force -Path $data,$current | Out-Null
$accounts=@()
foreach($index in 1..2){
 $id=[guid]::NewGuid().ToString('N')
 $profile=Join-Path $data ('profiles/'+$id)
 New-Item -ItemType Directory -Force -Path $profile | Out-Null
 Set-Content -LiteralPath (Join-Path $profile 'config.toml') -Value 'cli_auth_credentials_store = "file"' -Encoding utf8
 Set-Content -LiteralPath (Join-Path $profile '.quota-test-scenario') -Value $(if($index -eq 1){'per-profile'}elseif($HoldRequests){'slow'}else{'quota-error'}) -Encoding utf8
 $accounts+=@{Id=$id;Name=$(if($index -eq 1){'集成测试 A'}else{'集成测试 B（过期缓存）'})}
 if($index -eq 2){
  New-Item -ItemType Directory -Force -Path (Join-Path $data 'cache') | Out-Null
  @{FetchedAt=[DateTimeOffset]::UtcNow.AddHours(-$CacheAgeHours).ToString('O');Buckets=@(@{Name='Codex';Plan='plus';Windows=@(@{UsedPercent=64;DurationMinutes=300;ResetsAt=[DateTimeOffset]::UtcNow.AddHours(1).ToUnixTimeSeconds()})})} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $data ('cache/'+$id+'.json')) -Encoding utf8
 }
}
ConvertTo-Json -InputObject $accounts -Depth 8 | Set-Content -LiteralPath (Join-Path $data 'accounts-v2.json') -Encoding utf8
$settings=@{Width=380;Height=520;Topmost=$true;Privacy=$true;Compact=$true;CloseToTray=$false;CodexPath=$fake;RefreshMinutes=5}
if($Offscreen){$settings.Left=100000;$settings.Top=100000}
$settings | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding utf8
$start=[Diagnostics.ProcessStartInfo]::new($application)
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.Environment['CODEX_HOME']=$current
$start.ArgumentList.Add('--data-dir');$start.ArgumentList.Add($data)
$process=[Diagnostics.Process]::Start($start)
[pscustomobject]@{ProcessId=$process.Id;DataDirectory=$data;CurrentProfile=$current;Application=$application} | ConvertTo-Json
