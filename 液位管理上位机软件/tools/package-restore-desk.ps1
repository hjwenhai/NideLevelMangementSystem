param([string]$OutputDirectory,[switch]$SkipPublish)
$ErrorActionPreference='Stop'
$taskRoot=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if(!$OutputDirectory){$OutputDirectory=Join-Path $taskRoot 'output\业务数据恢复工具_20261031'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath (Join-Path $OutputDirectory '工具文件清单.json')){throw '该工具目录已封装，请选择新目录，不覆盖已发布工具。'}
if(!$SkipPublish){
 $env:DOTNET_ROOT=Join-Path $taskRoot '.build-tools\dotnet';$env:DOTNET_CLI_HOME=Join-Path $taskRoot '.build-tools\dotnet-home';$env:NUGET_PACKAGES=Join-Path $taskRoot '.build-tools\nuget'
 $dotnet=Join-Path $env:DOTNET_ROOT 'dotnet.exe';$project=Join-Path $taskRoot 'tools\LevelManager.RestoreDesk\LevelManager.RestoreDesk.csproj'
 & $dotnet restore $project -r win-x64 -p:SelfContained=true -p:NuGetAudit=false --ignore-failed-sources --disable-parallel -m:1 --verbosity minimal;if($LASTEXITCODE-ne0){throw '工具还原失败'}
 & $dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -p:DebugType=None -p:DebugSymbols=false -m:1 -o (Join-Path $OutputDirectory 'app') --verbosity minimal;if($LASTEXITCODE-ne0){throw '工具发布失败'}
}
if(!(Test-Path -LiteralPath (Join-Path $OutputDirectory 'app\LevelManager.RestoreDesk.exe'))){throw '缺少自包含桌面程序'}
Copy-Item -LiteralPath (Join-Path $taskRoot 'deployment\docs\桌面业务恢复工具.html') -Destination (Join-Path $OutputDirectory '使用说明.html')
$entry="@echo off`r`nstart `"`" `"%~dp0app\LevelManager.RestoreDesk.exe`"`r`n"
[IO.File]::WriteAllText((Join-Path $OutputDirectory '打开业务恢复工具.cmd'),$entry,[Text.Encoding]::ASCII)
& (Join-Path $PSScriptRoot 'copy-package-notices.ps1') -ApplicationRoot (Join-Path $OutputDirectory 'app') -Destination (Join-Path $OutputDirectory '第三方许可')
$files=@(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File)
if(@($files|Where-Object{$_.Extension-in@('.db','.pem','.pfx','.key','.license','.lic')-or$_.Name-match'LicenseDesk|installation\.dat|clock\.dat|mock-gateway|fixture|password'}).Count){throw '工具包含不应交付的密钥、客户数据或测试文件'}
$manifest=@($files|Sort-Object FullName|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($OutputDirectory,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
[ordered]@{tool='LevelManager.RestoreDesk';compatibleRelease='r18';builtAtUtc=[DateTimeOffset]::UtcNow.ToString('o');target='win-x64';selfContained=$true;containsCustomerData=$false;containsPrivateKey=$false;files=$manifest}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $OutputDirectory '工具文件清单.json') -Encoding UTF8
Write-Output ('工具已封装：'+$OutputDirectory)
