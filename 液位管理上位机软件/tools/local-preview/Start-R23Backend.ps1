param([switch]$CheckOnly)
. (Join-Path $PSScriptRoot 'R23Preview.Common.ps1')
if(!(Test-Path -LiteralPath $hostExe)){throw '找不到r23后台程序。'}
if($CheckOnly){& (Join-Path $PSScriptRoot 'Start-R23Simulator.ps1') -CheckOnly;Write-Output "r23后台入口检查通过：$url，未启动。";return}
$launchLock=[IO.File]::Open((Join-Path $preview 'backend-launch.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
 $sessionFile=Join-Path $PSScriptRoot 'r23-preview-session.json'
 $existing=$null;try{$existing=Invoke-RestMethod -Uri ($url+'/api/health') -TimeoutSec 2}catch{}
 if($existing){
  if($existing.build -ne '20261113-r23'){throw "$url 正在运行其他版本，不会替换或结束。"}
  $savedSession=if(Test-Path -LiteralPath $sessionFile){Get-Content -LiteralPath $sessionFile -Raw -Encoding UTF8|ConvertFrom-Json}else{$null}
  $savedProcess=if($savedSession){Get-Process -Id $savedSession.hostPid -ErrorAction SilentlyContinue}else{$null}
  if(!$savedSession -or $savedSession.url -ne ($url+'/') -or $savedSession.dataRoot -ne $data -or !$savedProcess -or $savedProcess.Path -ne $hostExe){throw '端口上已有r23，但不属于本入口记录的实例，未接管。'}
  Start-R23OptionalSimulator
  Write-Output ('r23后台已经运行：'+$url);return
 }
 Assert-R23PortAvailable $endpoint.Port 'r23后台'
 Start-R23OptionalSimulator
 New-Item -ItemType Directory -Path $data -Force|Out-Null
 $logs=Join-Path $preview 'logs';New-Item -ItemType Directory -Path $logs -Force|Out-Null
 $stamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ');$stdout=Join-Path $logs ($stamp+'.stdout.log');$stderr=Join-Path $logs ($stamp+'.stderr.log')
 $p=Start-Process -FilePath $hostExe -ArgumentList @('--data-root',('"'+$data+'"'),'--urls',$url) -WorkingDirectory $server -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
 $health=$null
 for($i=0;$i -lt 80;$i++){
  try{$health=Invoke-RestMethod -Uri ($url+'/api/health') -TimeoutSec 2;if($health.build -eq '20261113-r23'){break}}catch{}
  $p.Refresh();if($p.HasExited){throw ('r23后台启动失败，查看：'+$stderr)};Start-Sleep -Milliseconds 250
 }
 if(!$health -or $health.build -ne '20261113-r23'){throw ('后台尚未就绪，查看：'+$stdout)}
 @{revision='r23';build=$health.build;hostPid=$p.Id;url=($url+'/');dataRoot=$data;clientExe=$clientExe;profileRoot=$identity.profileRoot;stdout=$stdout;stderr=$stderr;startedAtUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content -LiteralPath $sessionFile -Encoding UTF8
 Write-Output ('r23后台已启动：'+$url)
 Write-Output '沿用已有账号、配置及历史数据。关闭此提示窗口不会停止后台。'
 Write-Output '现在可双击“打开r23客户端.cmd”。'
}finally{$launchLock.Dispose()}
