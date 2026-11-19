#requires -Version 7.0
[CmdletBinding()]param([Parameter(Mandatory)][string]$OutputDirectory,[switch]$SkipPublish)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'DevelopmentEnvironment.ps1')
$taskPackage=[IO.Path]::GetFullPath($OutputDirectory)
if(!(Test-Path -LiteralPath $taskPackage)){New-Item -ItemType Directory -Path $taskPackage|Out-Null}
$taskApp=Join-Path $taskPackage 'app'
if(!$SkipPublish){
    Invoke-TaskDotnet @('restore',(Join-Path $taskProjectRoot 'tools/LevelManager.LicenseDesk/LevelManager.LicenseDesk.csproj'),'-r','win-x64','-p:SelfContained=true','--source',(Join-Path $taskProjectRoot '.build-tools/nuget'),'-p:NuGetAudit=false')
    Invoke-TaskDotnet @('publish',(Join-Path $taskProjectRoot 'tools/LevelManager.LicenseDesk/LevelManager.LicenseDesk.csproj'),'-c','Release','-r','win-x64','--self-contained','true','--no-restore','-p:DebugType=None','-p:DebugSymbols=false','-m:1','-nodeReuse:false','-p:UseSharedCompilation=false','-o',$taskApp)
}
if(!(Test-Path -LiteralPath (Join-Path $taskApp 'LevelManager.LicenseDesk.exe'))){throw '缺少已发布桌面工具'}
if((Get-ChildItem -LiteralPath $taskPackage -File -Recurse|Where-Object{$_.Extension -in '.pem','.pfx','.key' -or $_.Name -like '*.license.json'}).Count){throw '工具分发目录不得包含密钥或已签发客户授权'}
[IO.File]::WriteAllText((Join-Path $taskPackage '打开供方授权工具.cmd'),"@echo off`r`nstart `"`" `"%~dp0app\LevelManager.LicenseDesk.exe`"`r`n",[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'deployment/vendor/桌面授权工具说明.html') -Destination (Join-Path $taskPackage '供方桌面授权工具使用说明.html')
Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'deployment/vendor/更新安装快捷方式图标.ps1') -Destination $taskPackage
$taskNotices=Join-Path $taskPackage '第三方许可';New-Item -ItemType Directory -Path $taskNotices -Force|Out-Null
foreach($taskNotice in @('dotnet-LICENSE.txt','dotnet-ThirdPartyNotices.txt')){Copy-Item -LiteralPath (Join-Path $taskProjectRoot ('deployment/third-party/'+$taskNotice)) -Destination $taskNotices}
$taskFiles=@(Get-ChildItem -LiteralPath $taskPackage -File -Recurse|Where-Object{$_.Name -ne '工具文件清单.json'}|Sort-Object FullName|ForEach-Object{@{path=[IO.Path]::GetRelativePath($taskPackage,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
@{name='液位管理系统供方桌面授权工具';builtAtUtc=[DateTimeOffset]::UtcNow.ToString('o');target='win-x64';selfContained=$true;containsPrivateKey=$false;customerPackage=$false;files=$taskFiles}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $taskPackage '工具文件清单.json') -Encoding utf8
$taskZip=$taskPackage+'.zip';if(Test-Path -LiteralPath $taskZip){throw 'ZIP已存在，请使用新输出文件名，不覆盖已留存版本'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($taskPackage,$taskZip,[IO.Compression.CompressionLevel]::Optimal,$false)
$taskHash=(Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash
[IO.File]::WriteAllText($taskZip+'.sha256',$taskHash+'  '+[IO.Path]::GetFileName($taskZip)+"`r`n",[Text.UTF8Encoding]::new($false))
Write-Output ('供方桌面工具：'+$taskPackage);Write-Output ('ZIP SHA256：'+$taskHash)
