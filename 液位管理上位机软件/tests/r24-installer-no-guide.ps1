#requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$qa=Join-Path $root 'output/r24-installer-no-guide_20261118'
$exe=Join-Path $root 'output/r24-feedback_20261118/qa/installer/package/液位管理系统_r24_安装程序_20261118.exe'
$case=Join-Path $qa ('client-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$app=Join-Path $case 'app'
$group=Join-Path ([Environment]::GetFolderPath('CommonPrograms')) '液位管理系统安装验证'
$service='Naide.LevelManager.QA.WizardPackage'
$checks=New-Object Collections.Generic.List[object]
function Require([string]$name,[bool]$ok){$checks.Add(@{name=$name;passed=$ok});if(!$ok){throw $name}}
function Install([string]$log){
    $arguments='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /MODE=Client /SERVERURL="http://127.0.0.1:18780/" /MERGETASKS="!desktopicon" /DIR="'+$app+'" /LOG="'+(Join-Path $case $log)+'"'
    $process=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    Require ('installer-'+$log) ($process.ExitCode -eq 0)
}
$passed=$false
New-Item -ItemType Directory -Path $case -Force|Out-Null
try{
    Require 'qa-service-not-present' (!(Get-Service $service -ErrorAction SilentlyContinue))
    Require 'qa-registration-not-present' (!(Test-Path -LiteralPath 'HKLM:/SOFTWARE/Microsoft/Windows/CurrentVersion/Uninstall/{575AD2FD-D52A-4168-B9DA-F8A0BE01A1A6}_is1'))
    Require 'qa-shortcut-group-empty' (@(Get-ChildItem -LiteralPath $group -ErrorAction SilentlyContinue).Count -eq 0)
    Install 'fresh.log'
    $record=Get-Content -LiteralPath (Join-Path $app 'installation.json') -Raw -Encoding UTF8|ConvertFrom-Json
    Require 'isolated-client-installation' ($record.Mode -eq 'Client' -and $record.InstallRoot -eq $app)
    $guide=Join-Path $app '安装向导使用说明.html'
    $versionGuide=Join-Path $record.VersionRoot 'docs/r24修订与升级说明.html'
    $link=Join-Path $group '安装与升级说明.lnk'
    Require 'fresh-root-guide-absent' (!(Test-Path -LiteralPath $guide))
    Require 'fresh-version-guide-absent' (!(Test-Path -LiteralPath $versionGuide))
    Require 'fresh-guide-shortcut-absent' (!(Test-Path -LiteralPath $link))
    Require 'client-shortcut-present' (Test-Path -LiteralPath (Join-Path $group '液位管理系统.lnk'))
    Require 'slot-instruction-retained' (Test-Path -LiteralPath (Join-Path $record.VersionRoot 'docs/网关槽位填写说明.txt'))
    # Seed only obsolete files into this test installation to simulate an upgrade.
    'old guide'|Set-Content -LiteralPath $guide -Encoding UTF8
    'old version guide'|Set-Content -LiteralPath $versionGuide -Encoding UTF8
    $shell=New-Object -ComObject WScript.Shell
    $shortcut=$shell.CreateShortcut($link);$shortcut.TargetPath=$guide;$shortcut.Save()
    $keep=Join-Path $app '客户自存资料.txt';'keep me'|Set-Content -LiteralPath $keep -Encoding UTF8
    $keepHash=(Get-FileHash -LiteralPath $keep).Hash
    Install 'upgrade.log'
    Require 'upgrade-root-guide-removed' (!(Test-Path -LiteralPath $guide))
    Require 'upgrade-version-guide-removed' (!(Test-Path -LiteralPath $versionGuide))
    Require 'upgrade-guide-shortcut-removed' (!(Test-Path -LiteralPath $link))
    Require 'upgrade-customer-file-preserved' ((Get-FileHash -LiteralPath $keep).Hash -eq $keepHash)
    $config=Get-Content -LiteralPath (Join-Path $record.VersionRoot 'app/client/client.json') -Raw -Encoding UTF8|ConvertFrom-Json
    Require 'upgrade-client-address-preserved' ($config.serverUrl -eq 'http://127.0.0.1:18780/')
    Require 'qa-backend-never-created' (!(Get-Service $service -ErrorAction SilentlyContinue))
    $passed=$true
}catch{$_|Out-String|Set-Content -LiteralPath (Join-Path $case 'failure.txt') -Encoding UTF8}
finally{
    $uninstaller=Join-Path $app 'unins000.exe'
    if(Test-Path -LiteralPath $uninstaller){
        $process=Start-Process -FilePath $uninstaller -ArgumentList ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="'+(Join-Path $case 'uninstall.log')+'"') -WindowStyle Hidden -Wait -PassThru
        if($process.ExitCode -ne 0){$passed=$false}
        $checks.Add(@{name='qa-uninstall';passed=($process.ExitCode -eq 0)})
    }
    @{passed=$passed;checks=$checks;fixture=$case;realDeviceActions=0;liveBackendChanged=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'package-result.json') -Encoding UTF8
}
Get-Content -LiteralPath (Join-Path $qa 'package-result.json') -Raw -Encoding UTF8
if(!$passed){exit 1}
