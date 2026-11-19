param([switch]$CheckOnly)
. (Join-Path $PSScriptRoot 'R24Preview.Common.ps1')
if(!(Test-Path -LiteralPath $hostExe)){throw '找不到r24后台程序。'}
if($CheckOnly){& (Join-Path $PSScriptRoot 'Start-R24Simulator.ps1') -CheckOnly;Write-Output "r24后台入口检查通过：$url，未启动。";return}
$launchLock=[IO.File]::Open((Join-Path $preview 'backend-launch.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
 $sessionFile=Join-Path $PSScriptRoot 'r24-preview-session.json'
 $existing=$null;try{$existing=Invoke-RestMethod -Uri ($url+'/api/health') -TimeoutSec 2}catch{}
 if($existing){
  if($existing.build -ne '20261118-r24'){throw "$url 正在运行其他版本，不会替换或结束。"}
  $savedSession=if(Test-Path -LiteralPath $sessionFile){Get-Content -LiteralPath $sessionFile -Raw -Encoding UTF8|ConvertFrom-Json}else{$null}
  $savedProcess=if($savedSession){Get-Process -Id $savedSession.hostPid -ErrorAction SilentlyContinue}else{$null}
  if(!$savedSession -or $savedSession.url -ne ($url+'/') -or $savedSession.dataRoot -ne $data -or !$savedProcess -or $savedProcess.Path -notin @($hostExe,$legacyHostExe)){throw '端口上已有r24，但不属于本入口记录的实例，未接管。'}
  if($savedProcess.Path -ne $hostExe){
   Write-Output '本机r24仍在使用上次启动的后台。结束现场作业并停止原后台后，再运行此入口可切换到最新后台。'
   return
  }
  Start-R24OptionalSimulator
  Write-Output ('r24后台已经运行：'+$url);return
 }
 Assert-R24PortAvailable $endpoint.Port 'r24后台'
 Start-R24OptionalSimulator
 New-Item -ItemType Directory -Path $data -Force|Out-Null
 $logs=Join-Path $preview 'logs';New-Item -ItemType Directory -Path $logs -Force|Out-Null
 $stamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ');$stdout=Join-Path $logs ($stamp+'.stdout.log');$stderr=Join-Path $logs ($stamp+'.stderr.log')
 $p=Start-Process -FilePath $hostExe -ArgumentList @('--data-root',('"'+$data+'"'),'--urls',$url) -WorkingDirectory $server -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
 $health=$null
 for($i=0;$i -lt 80;$i++){
  try{$health=Invoke-RestMethod -Uri ($url+'/api/health') -TimeoutSec 2;if($health.build -eq '20261118-r24'){break}}catch{}
  $p.Refresh();if($p.HasExited){throw ('r24后台启动失败，查看：'+$stderr)};Start-Sleep -Milliseconds 250
 }
 if(!$health -or $health.build -ne '20261118-r24'){throw ('后台尚未就绪，查看：'+$stdout)}
 @{revision='r24';build=$health.build;hostPid=$p.Id;url=($url+'/');dataRoot=$data;clientExe=$clientExe;profileRoot=$identity.profileRoot;stdout=$stdout;stderr=$stderr;startedAtUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content -LiteralPath $sessionFile -Encoding UTF8
 Write-Output ('r24后台已启动：'+$url)
 Write-Output '沿用已有账号、配置及历史数据。关闭此提示窗口不会停止后台。'
 Write-Output '现在可双击“打开r24客户端.cmd”。'
}finally{$launchLock.Dispose()}
