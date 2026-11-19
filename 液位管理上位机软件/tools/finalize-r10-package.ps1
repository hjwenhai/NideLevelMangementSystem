# Refresh the still-unsealed r10 candidate after the final authorization regression.
# This script refuses older protected candidates and a revision already sealed as ZIP.
#requires -Version 7.0
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'DevelopmentEnvironment.ps1')
$taskPackage=[IO.Path]::GetFullPath((Join-Path $taskProjectRoot 'output/中文联调候选包_0.3.0-zh-test.1_r10'))
$taskExpected=[IO.Path]::GetFullPath((Join-Path $taskProjectRoot 'output'))+[IO.Path]::DirectorySeparatorChar
if(!$taskPackage.StartsWith($taskExpected,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($taskPackage) -ne '中文联调候选包_0.3.0-zh-test.1_r10'){throw '只允许刷新本次新建的r10候选'}
if(Test-Path -LiteralPath (Join-Path $taskProjectRoot 'output/releases/液位管理系统_中文联调候选版_0.3.0-zh-test.1_20261028_r10.zip')){throw 'r10已封装，不修改已封装目录'}
$taskManifestFile=Join-Path $taskPackage 'package-manifest.json';$taskManifest=Get-Content -LiteralPath $taskManifestFile -Raw|ConvertFrom-Json
if($taskManifest.productId -ne 'Naide.LevelManager.2026'){throw '候选标识不符'}
Copy-Item -LiteralPath $taskManifestFile -Destination (Join-Path $taskProjectRoot 'output/priority-r10-before-final-manifest.json')
foreach($taskDoc in Get-ChildItem -LiteralPath (Join-Path $taskProjectRoot 'deployment/docs') -File){if($taskDoc.Name -notmatch '_r9\.(html|csv)$'){Copy-Item -LiteralPath $taskDoc.FullName -Destination (Join-Path $taskPackage ('docs/'+$taskDoc.Name)) -Force}}
foreach($taskOldName in @('原型与正式系统逐项对照_r9.html','原型与正式系统逐项对照_r9.csv')){
 $taskOld=[IO.Path]::GetFullPath((Join-Path $taskPackage ('docs/'+$taskOldName)))
 if(!$taskOld.StartsWith($taskPackage+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '清单目录越界'}
 # The r9 source, comparison output, archive and sealed r9 package are retained.
 if(Test-Path -LiteralPath $taskOld){Remove-Item -LiteralPath $taskOld}
}
$taskForbidden=@(Get-ChildItem -LiteralPath $taskPackage -Recurse -File|Where-Object{$_.Extension -in '.pem','.pfx','.key','.db' -or $_.Name -match 'LevelManager.Tools|VendorAuthorization|license\.json|installation\.dat|clock\.dat|password|mock-gateway|node.exe|python.exe'})
if($taskForbidden.Count){throw '发现不应发放的工具、密钥或数据'}
$taskFiles=@(Get-ChildItem -LiteralPath $taskPackage -Recurse -File|Where-Object{$_.FullName -ne $taskManifestFile}|Sort-Object FullName|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($taskPackage,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
[ordered]@{productId=$taskManifest.productId;version=$taskManifest.version;revision='r10';stage='中文联调候选版，待六小时阶段验证及现场验收';builtAtUtc=[DateTime]::UtcNow.ToString('o');target='win-x64';schemaVersion=15;maximumTanks=128;historySeconds=60;retentionMonths=12;language='zh-CN';averageResources=$true;plannedSoakSeconds=21600;customerExpirationPreset=$false;containsSupplierSigningTools=$false;files=$taskFiles}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $taskManifestFile -Encoding utf8
Write-Output ('r10候选文件清单已更新：'+$taskFiles.Count+'项；尚未将六小时测试标为通过。')
