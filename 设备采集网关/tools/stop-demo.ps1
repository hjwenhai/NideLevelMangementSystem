#requires -Version 7.0
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
foreach($name in @('web','core')){
 $pidPath=Join-Path $taskRoot ('output/demo/'+$name+'.pid')
 if(-not(Test-Path -LiteralPath $pidPath)){continue}
 $ownedPID=[int](Get-Content -LiteralPath $pidPath)
 $process=Get-Process -Id $ownedPID -ErrorAction SilentlyContinue
 $expected=Join-Path $taskRoot ('output/windows-amd64/gateway-'+$name+'.exe')
 if($process){
  if($process.Path -ne $expected){throw 'PID belongs to another process; refusing to stop it'}
  Stop-Process -Id $ownedPID
 }
 Remove-Item -LiteralPath $pidPath
}
Write-Host 'Demo stopped. Config, database and logs retained.'
