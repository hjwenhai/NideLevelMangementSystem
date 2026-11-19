#requires -Version 7.0
param([string]$Version='0.8.3-20261117-r1')
$ErrorActionPreference='Stop'
if($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'){throw 'Invalid version'}
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
$taskSigner=Join-Path $taskRoot 'output/windows-amd64/gateway-release.exe'
$taskPrivate=Join-Path $taskRoot 'private/update-signing.key'
if(-not(Test-Path -LiteralPath $taskPrivate)){
 & $taskSigner keygen --private-key $taskPrivate --public-key (Join-Path $taskRoot 'deploy/update-trust.json')
 if($LASTEXITCODE -ne 0){throw 'Signing key setup failed'}
}
$runtimeDeploy=@('install.sh','service.sh','supervise.sh','rollback.sh','S99levelgateway','settings.example.env','update-trust.json')
# A runtime package contains only binaries, essential service scripts/configuration and release metadata.
foreach($arch in @('linux-arm64','linux-armv7')){
 $candidate=Join-Path $taskRoot ('output/packages/gt6757s-'+$Version+'-'+$arch)
 if((Test-Path -LiteralPath $candidate) -or (Test-Path -LiteralPath ($candidate+'.tar.gz'))){throw 'Release exists; choose a new version'}
 foreach($name in @('gateway-core','gateway-web','gateway-updater')){
  if(-not (Test-Path -LiteralPath (Join-Path $taskRoot ('output/'+$arch+'/'+$name)))){throw ('Missing binary: '+$arch+'/'+$name)}
 }
}
foreach($arch in @('linux-arm64','linux-armv7')){
 $taskStage=Join-Path $taskRoot ('output/packages/gt6757s-'+$Version+'-'+$arch)
 if(Test-Path -LiteralPath $taskStage){throw 'Package directory exists; choose a new version rather than overwrite it'}
 New-Item -ItemType Directory -Path (Join-Path $taskStage 'bin') -Force | Out-Null
 foreach($name in @('gateway-core','gateway-web','gateway-updater')){
  Copy-Item -LiteralPath (Join-Path $taskRoot ('output/'+$arch+'/'+$name)) -Destination (Join-Path $taskStage ('bin/'+$name))
 }
 New-Item -ItemType Directory -Path (Join-Path $taskStage 'deploy') -Force | Out-Null
 foreach($name in $runtimeDeploy){
  Copy-Item -LiteralPath (Join-Path $taskRoot ('deploy/'+$name)) -Destination (Join-Path $taskStage ('deploy/'+$name))
 }
 # Protocol data and Web assets are embedded in the binaries; this tiny marker is for V3 rollback checks.
 [IO.File]::WriteAllText((Join-Path $taskStage 'NORTH_VERSION'),('3.2'+[char]10),[Text.UTF8Encoding]::new($false))
 [IO.File]::WriteAllText((Join-Path $taskStage 'UPDATE_VERSION'),('1'+[char]10),[Text.UTF8Encoding]::new($false))
 Copy-Item -LiteralPath (Join-Path $taskRoot 'deploy/config.production-64.json') -Destination (Join-Path $taskStage 'config.default.json')
 [IO.File]::WriteAllText((Join-Path $taskStage 'RELEASE'),$Version+"`n",[Text.UTF8Encoding]::new($false))
 [IO.File]::WriteAllText((Join-Path $taskStage 'ARCH'),$arch+"`n",[Text.UTF8Encoding]::new($false))
 $entries=Get-ChildItem -LiteralPath $taskStage -File -Recurse | Sort-Object FullName | ForEach-Object {
  $h=Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
  $h.Hash.ToLower()+'  '+$_.FullName.Substring($taskStage.Length+1).Replace('\','/')
 }
 [IO.File]::WriteAllText((Join-Path $taskStage 'SHA256SUMS'),($entries -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
 $archive=$taskStage+'.tar.gz'
 & tar.exe -czf $archive -C (Split-Path $taskStage -Parent) (Split-Path $taskStage -Leaf)
 if($LASTEXITCODE -ne 0){throw 'Archive failed'}
 # 厂家私钥只在电脑签名，设备仅携带公钥；客户只需上传一个.gwup文件。
 $taskBundle=$taskStage+'.gwup'
 & $taskSigner sign --payload $archive --stage $taskStage --output $taskBundle --private-key $taskPrivate --notes '允许仪表带故障码下发操作，按指令应答及回读确认控制结果，保留故障告警与原控制记录；旧错误3可人工复位。'
 if($LASTEXITCODE -ne 0){throw 'Signed bundle creation failed'}
 Get-FileHash -LiteralPath $archive,$taskBundle -Algorithm SHA256 | Format-List
}
