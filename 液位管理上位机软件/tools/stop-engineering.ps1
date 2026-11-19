#requires -Version 7.0
$ErrorActionPreference='Stop';$taskRoot=Split-Path -Parent $PSScriptRoot;$state=Join-Path $taskRoot 'output/engineering-process.json'
if(-not(Test-Path -LiteralPath $state)){Write-Output '没有本工程启动记录';exit 0}
$record=Get-Content -LiteralPath $state -Raw | ConvertFrom-Json
$process=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$record.processId) -ErrorAction SilentlyContinue
if(-not $process){Write-Output '工程版已停止';exit 0}
$expectedDll=Join-Path $taskRoot 'output/engineering/LevelManager.Host.dll';$expectedData=Join-Path $taskRoot 'data/engineering'
if($record.dll -ne $expectedDll -or $record.data -ne $expectedData -or -not $process.CommandLine -or -not $process.CommandLine.Contains($expectedDll) -or -not $process.CommandLine.Contains($expectedData)){throw '进程身份与本工程不匹配，拒绝停止'}
# P1工程版尚未启动任何设备采集或控制。正式控制阶段须接入受控停止/关机流程。
Stop-Process -Id ([int]$record.processId)
Write-Output 'P1工程版已停止，SQLite数据保留。'
