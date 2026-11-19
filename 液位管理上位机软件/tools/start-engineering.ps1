#requires -Version 7.0
param([int]$Port=8780,[switch]$NoBrowser)
$ErrorActionPreference='Stop';$taskRoot=Split-Path -Parent $PSScriptRoot
$taskDotnet=Join-Path $taskRoot '.build-tools/dotnet/dotnet.exe'
if(-not(Test-Path -LiteralPath $taskDotnet)){$taskDotnet=(Get-Command dotnet -ErrorAction Stop).Source}
$taskDll=Join-Path $taskRoot 'output/engineering/LevelManager.Host.dll'
if(-not(Test-Path -LiteralPath $taskDll)){throw '请先运行tools/build.ps1生成工程版'}
if($Port -lt 1024 -or $Port -gt 65535){throw '端口应为1024～65535'}
$taskData=Join-Path $taskRoot 'data/engineering';$taskUrl='http://127.0.0.1:'+$Port
$taskState=Join-Path $taskRoot 'output/engineering-process.json'
if(Test-Path -LiteralPath $taskState){
 $record=Get-Content -LiteralPath $taskState -Raw | ConvertFrom-Json
 $existing=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$record.processId) -ErrorAction SilentlyContinue
 if($existing -and $existing.CommandLine -and $existing.CommandLine.Contains($taskDll) -and $existing.CommandLine.Contains($taskData)){
  if($record.url -ne $taskUrl){throw ('工程版已在'+$record.url+'运行，请先停止旧进程')}
  if(-not $NoBrowser){Start-Process $taskUrl};Write-Output $taskUrl;exit 0
 }
}
try{$test=Invoke-RestMethod -Uri ($taskUrl+'/api/health') -TimeoutSec 1;if($test){throw '端口已有程序使用，请更换端口或停止旧实例'}}catch{if($_.Exception.Message -like '*端口已有*'){throw}}
$env:DOTNET_ROOT=Split-Path -Parent $taskDotnet;$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$argsList=@(('"'+$taskDll+'"'),'--data-root',('"'+$taskData+'"'),'--urls',$taskUrl)
$process=Start-Process -FilePath $taskDotnet -ArgumentList $argsList -WorkingDirectory $taskRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskRoot 'output/engineering.stdout.log') -RedirectStandardError (Join-Path $taskRoot 'output/engineering.stderr.log') -PassThru
@{processId=$process.Id;url=$taskUrl;dll=$taskDll;data=$taskData;executable=$taskDotnet} | ConvertTo-Json | Set-Content -LiteralPath $taskState -Encoding utf8
for($i=0;$i -lt 100;$i++){
 if($process.HasExited){throw '后台启动失败，请查看output/engineering.stderr.log'}
 try{$health=Invoke-RestMethod -Uri ($taskUrl+'/api/health') -TimeoutSec 1;if($health.product -eq '液位管理系统' -and $health.maximumTanks -eq 128){if(-not $NoBrowser){Start-Process $taskUrl};Write-Output $taskUrl;exit 0}}catch{}
 Start-Sleep -Milliseconds 100
}
throw '后台未在预期时间内就绪，请查看output目录日志'
