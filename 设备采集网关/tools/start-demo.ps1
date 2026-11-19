#requires -Version 7.0
param([int]$Meters=64)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
if($Meters -lt 1 -or $Meters -gt 64){throw 'Meters must be 1..64'}
$taskDir=Join-Path $taskRoot 'output/demo'
New-Item -ItemType Directory -Path $taskDir -Force | Out-Null
$taskCore=Join-Path $taskRoot 'output/windows-amd64/gateway-core.exe'
$taskWeb=Join-Path $taskRoot 'output/windows-amd64/gateway-web.exe'
if(-not (Test-Path $taskCore)){throw 'Run tools/build.ps1 first'}
foreach($name in @('core','web')){
 $pidPath=Join-Path $taskDir ($name+'.pid')
 if(Test-Path $pidPath){
  $ownedPID=[int](Get-Content -LiteralPath $pidPath)
  $existing=Get-Process -Id $ownedPID -ErrorAction SilentlyContinue
  if($existing -and $existing.Path -eq $(if($name -eq 'core'){$taskCore}else{$taskWeb})){throw 'Demo already running; stop it before starting again.'}
 }
}
$configPath=Join-Path $taskDir 'config.json'
& $taskCore --init-config $configPath --meters $Meters
if($LASTEXITCODE -ne 0){throw 'Config generation failed'}
$cfg=Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
$cfg.tcp.allow_writes=$true
$cfg.allowed_actions=@(1,2,4,8,16,32,64,128,256)
$cfg | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM
$authPath=Join-Path $taskDir 'admin.json'
if(-not(Test-Path $authPath)){
 $bytes=New-Object byte[] 20
 [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
 $password='Demo-'+[Convert]::ToBase64String($bytes)
 $password | & $taskWeb --init-admin --auth-file $authPath
 if($LASTEXITCODE -ne 0){throw 'Admin creation failed'}
 $password | Set-Content -LiteralPath (Join-Path $taskDir 'login-password.txt') -Encoding utf8NoBOM
}
$coreArgs=@('--simulate','--config','output/demo/config.json','--db','output/demo/gateway-v2.db','--token-file','output/demo/ipc.token')
$webArgs=@('--auth-file','output/demo/admin.json','--token-file','output/demo/ipc.token')
$core=Start-Process -FilePath $taskCore -ArgumentList $coreArgs -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskDir 'core.out.log') -RedirectStandardError (Join-Path $taskDir 'core.err.log')
$core.Id | Set-Content -LiteralPath (Join-Path $taskDir 'core.pid')
Start-Sleep -Milliseconds 800
if($core.HasExited){throw ('Core failed: '+(Get-Content -LiteralPath (Join-Path $taskDir 'core.err.log') -Raw))}
$web=Start-Process -FilePath $taskWeb -ArgumentList $webArgs -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskDir 'web.out.log') -RedirectStandardError (Join-Path $taskDir 'web.err.log')
$web.Id | Set-Content -LiteralPath (Join-Path $taskDir 'web.pid')
Start-Sleep -Milliseconds 500
if($web.HasExited){throw ('Web failed: '+(Get-Content -LiteralPath (Join-Path $taskDir 'web.err.log') -Raw))}
Write-Host 'Demo: http://127.0.0.1:8088; Modbus TCP: 127.0.0.1:1502, Unit 1'
Write-Host 'Login password is in output/demo/login-password.txt (local simulation only).'
