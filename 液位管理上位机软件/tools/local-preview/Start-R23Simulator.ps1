param([switch]$CheckOnly)
. (Join-Path $PSScriptRoot 'R23Preview.Common.ps1')
if(!(Test-Path -LiteralPath $simulatorExe)){throw '找不到本机模拟器程序，请检查保留目录。'}
if($CheckOnly){Write-Output "r23模拟网关入口检查通过：127.0.0.1:$gatewayPort，未启动。";return}
$launchLock=[IO.File]::Open((Join-Path $preview 'simulator-launch.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
 $sessionFile=Join-Path $preview 'simulator-session.json'
 $ready=Join-Path $preview 'simulator-ready.json';$stop=Join-Path $preview 'stop-simulator.flag'
 if(Test-Path -LiteralPath $sessionFile){
  $saved=Get-Content -LiteralPath $sessionFile -Raw -Encoding UTF8|ConvertFrom-Json
  $p=Get-Process -Id $saved.pid -ErrorAction SilentlyContinue
  if($p -and $p.Path -eq $simulatorExe){
   if($saved.port -ne $gatewayPort){throw '旧模拟器仍在运行，端口与当前设置不同；请先正常停止旧模拟器。'}
   $info=if(Test-Path -LiteralPath $ready){Get-Content -LiteralPath $ready -Raw -Encoding UTF8|ConvertFrom-Json}else{$null}
   if(!$info -or !$info.passed -or $info.Port -ne $gatewayPort -or $info.count -ne 16 -or !$info.loopbackOnly){throw '模拟器进程尚在运行，但就绪信息不符，请勿反复启动。'}
   Write-Output "r23独立模拟网关已运行：127.0.0.1:$gatewayPort，16槽位。";return
  }
 }
 Assert-R23PortAvailable $gatewayPort '模拟网关'
 foreach($f in @($ready,$stop,($ready+'.failure.json'))){if(Test-Path -LiteralPath $f){Remove-Item -LiteralPath $f -Force}}
 $p=Start-Process -FilePath $simulatorExe -ArgumentList @('--headless','--port',"$gatewayPort",'--count','16','--ready',('"'+$ready+'"'),'--stop-file',('"'+$stop+'"')) -WorkingDirectory (Split-Path $simulatorExe -Parent) -WindowStyle Hidden -PassThru
 for($i=0;$i -lt 60;$i++){
  if(Test-Path -LiteralPath ($ready+'.failure.json')){throw ('模拟网关未能监听端口 '+$gatewayPort+'；详情保存在 '+$ready+'.failure.json')}
  if(Test-Path -LiteralPath $ready){break}
  $p.Refresh();if($p.HasExited){throw '模拟网关启动后提前退出'};Start-Sleep -Milliseconds 250
 }
 if(!(Test-Path -LiteralPath $ready)){throw '模拟网关尚未就绪，请勿反复点击'}
 $info=Get-Content -LiteralPath $ready -Raw -Encoding UTF8|ConvertFrom-Json
 if(!$info.passed -or $info.Port -ne $gatewayPort -or $info.count -ne 16 -or !$info.loopbackOnly){throw '模拟网关就绪信息不符'}
 @{pid=$p.Id;exe=$simulatorExe;ready=$ready;stop=$stop;port=$gatewayPort;count=16;startedAtUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content -LiteralPath $sessionFile -Encoding UTF8
 Write-Output "r23独立模拟网关已启动：127.0.0.1:$gatewayPort；16槽位，不连接真实设备。"
}finally{$launchLock.Dispose()}
