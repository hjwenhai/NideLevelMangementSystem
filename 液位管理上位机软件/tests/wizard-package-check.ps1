#requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$qa=Join-Path $root 'output/wizard-installer-qa_20261110'
$exe=Join-Path $qa 'package/液位管理系统_r22_安装程序_20261110.exe'
$case=Join-Path $qa ('native-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$app=Join-Path $case '完整安装'
$data=Join-Path $case '业务数据'
$client=Join-Path $case '仅客户端'
New-Item -ItemType Directory -Force -Path $case|Out-Null
$service='Naide.LevelManager.QA.WizardPackage'
$checks=New-Object Collections.Generic.List[object]
function Require([string]$name,[bool]$ok){$checks.Add(@{name=$name;passed=$ok});if(!$ok){throw $name}}
function Install([string]$destination,[string]$extra,[string]$log){
    $arguments='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="'+$destination+'" /LOG="'+(Join-Path $case $log)+'" '+$extra
    $process=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    Require ('installer-'+$log) ($process.ExitCode -eq 0)
}
function Uninstall([string]$destination,[string]$log){
    $file=Join-Path $destination 'unins000.exe'
    Require 'uninstaller-created' (Test-Path -LiteralPath $file)
    $process=Start-Process -FilePath $file -ArgumentList ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="'+(Join-Path $case $log)+'"') -WindowStyle Hidden -Wait -PassThru
    Require ('uninstall-'+$log) ($process.ExitCode -eq 0)
}
$passed=$false
try{
    if(Get-Service -Name $service -ErrorAction SilentlyContinue){throw 'QA服务已存在'}
    $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
    Install $app ('/DATADIR="'+$data+'" /PORT='+$port) 'fresh.log'
    $record=Get-Content -LiteralPath (Join-Path $app 'installation.json') -Raw -Encoding UTF8|ConvertFrom-Json
    Require 'full-mode-and-data-path' ($record.Mode -eq 'Full' -and $record.DataRoot -eq $data)
    Require 'service-running' ((Get-Service $service).Status -eq 'Running')
    Require 'health-build' ((Invoke-RestMethod ('http://127.0.0.1:'+$port+'/api/health')).build -eq '20261107-r22-u2')
    $shell=New-Object -ComObject WScript.Shell
    $shortcut=$shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) '液位管理系统安装验证.lnk'))
    Require 'desktop-target-current-version' ($shortcut.TargetPath -eq (Join-Path $record.VersionRoot 'app/client/LevelManager.Desktop.exe'))
    $db=Join-Path $data 'level-manager.db'
    Require 'database-created' (Test-Path -LiteralPath $db)
    $endpoint='http://127.0.0.1:'+$port
    $session=Invoke-RestMethod ($endpoint+'/api/session') -SessionVariable web
    $body=@{login='qa_installer';displayName='安装验证';password='QaSetupTest2026'}|ConvertTo-Json
    Invoke-RestMethod ($endpoint+'/api/auth/setup') -Method Post -WebSession $web -Headers @{'X-CSRF-TOKEN'=$session.csrfToken} -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))|Out-Null
    Stop-Service $service;(Get-Service $service).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))
    $python=Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
    $seed=Join-Path $PSScriptRoot 'wizard-legacy-data.py'
    & $python -X utf8 $seed seed $db (Join-Path $case 'legacy-before.json')
    Require 'schema16-sample-created' ($LASTEXITCODE -eq 0)
    Install $app ('/DATADIR="'+$data+'" /PORT='+$port) 'upgrade.log'
    Require 'upgrade-still-running' ((Get-Service $service).Status -eq 'Running')
    & $python -X utf8 $seed read $db (Join-Path $case 'legacy-after.json')
    Require 'legacy-readback' ($LASTEXITCODE -eq 0)
    $before=Get-Content (Join-Path $case 'legacy-before.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $after=Get-Content (Join-Path $case 'legacy-after.json') -Raw -Encoding UTF8|ConvertFrom-Json
    Require 'schema16-upgraded-to17' ($before.schema -eq 16 -and $after.schema -eq 17)
    foreach($name in @('users','configuration','history','events')){
        Require ('preserve-'+$name) (($before.$name|ConvertTo-Json -Depth 20 -Compress) -eq ($after.$name|ConvertTo-Json -Depth 20 -Compress))
    }
    $session=Invoke-RestMethod ($endpoint+'/api/session') -SessionVariable loginWeb
    $login=@{login='qa_installer';password='QaSetupTest2026'}|ConvertTo-Json
    Invoke-RestMethod ($endpoint+'/api/auth/login') -Method Post -WebSession $loginWeb -Headers @{'X-CSRF-TOKEN'=$session.csrfToken} -ContentType 'application/json' -Body $login|Out-Null
    Require 'original-login-works-after-upgrade' ($null -ne (Invoke-RestMethod ($endpoint+'/api/session') -WebSession $loginWeb).user)
    Uninstall $app 'uninstall-full.log'
    Require 'full-service-removed' (!(Get-Service $service -ErrorAction SilentlyContinue))
    Require 'uninstall-keeps-business-data' (Test-Path -LiteralPath $db)
    Require 'uninstall-keeps-authorization' (Test-Path -LiteralPath ($data+'.authorization'))
    Require 'uninstall-removes-server-binary' (!(Test-Path (Join-Path $record.VersionRoot 'app/server/LevelManager.Host.exe')))
    Install $client '/MODE=Client /SERVERURL="http://192.168.1.10:8780/" /MERGETASKS="!desktopicon"' 'client.log'
    $record=Get-Content -LiteralPath (Join-Path $client 'installation.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $clientJson=Get-Content -LiteralPath (Join-Path $record.VersionRoot 'app/client/client.json') -Raw -Encoding UTF8|ConvertFrom-Json
    Require 'client-server-address' ($record.Mode -eq 'Client' -and $clientJson.serverUrl -eq 'http://192.168.1.10:8780/')
    Require 'client-has-no-service' (!(Get-Service $service -ErrorAction SilentlyContinue))
    Require 'client-omits-server-binary' (!(Test-Path (Join-Path $record.VersionRoot 'app/server/LevelManager.Host.exe')))
    Require 'desktop-checkbox-respected' (!(Test-Path (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) '液位管理系统安装验证.lnk')))
    Uninstall $client 'uninstall-client.log'
    $passed=$true
}catch{$_|Out-String|Set-Content (Join-Path $case 'failure.txt') -Encoding UTF8}
finally{
    $svc=Get-Service -Name $service -ErrorAction SilentlyContinue
    if($svc){if($svc.Status -ne 'Stopped'){Stop-Service $service;$svc.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))};sc.exe delete $service|Out-Null}
    @{passed=$passed;checks=$checks;fixture=$case;service=$service;realInstrumentWrites=0;atUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $qa 'package-result.json') -Encoding UTF8
}
if(!$passed){exit 1}
