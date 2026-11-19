#requires -Version 7.0
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
foreach($count in @(16,24,32,48,64)){
 $file='deploy/config.production-'+$count+'.json'
 & ./output/windows-amd64/gateway-core.exe --init-config $file --meters $count
 if($LASTEXITCODE -ne 0){throw 'Generation failed'}
 $cfg=Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
 $cfg.tcp.listen='0.0.0.0:502'
 $cfg | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $file -Encoding utf8NoBOM
 & ./output/windows-amd64/gateway-core.exe --check-config --config $file
 if($LASTEXITCODE -ne 0){throw 'Config check failed'}
}
& ./output/windows-amd64/gateway-core.exe --init-config deploy/config.demo.json --meters 64
