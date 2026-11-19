#requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$qa=Join-Path $root 'output/r24-feedback_20261118/qa/installer'
$exe=Join-Path $qa 'package/液位管理系统_r24_安装程序_20261118.exe'
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
function Test-ReinstallPrepare([string]$target,[bool]$consent){
    $arguments=($engineArgs | ForEach-Object {'"'+$_+'"'}) -join ' '
    $arguments+=' -DataRoot "'+$target+'"'
    if($consent){$arguments+=' -ReuseExistingData'}
    $process=Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    return $process.ExitCode
}
$passed=$false
try{
    if(Get-Service -Name $service -ErrorAction SilentlyContinue){throw 'QA服务已存在'}
    $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
    Install $app ('/DATADIR="'+$data+'" /PORT='+$port) 'fresh.log'
    $record=Get-Content -LiteralPath (Join-Path $app 'installation.json') -Raw -Encoding UTF8|ConvertFrom-Json
    Require 'full-mode-and-data-path' ($record.Mode -eq 'Full' -and $record.DataRoot -eq $data)
    Require 'service-running' ((Get-Service $service).Status -eq 'Running')
    Require 'health-build' ((Invoke-RestMethod ('http://127.0.0.1:'+$port+'/api/health')).build -eq '20261118-r24')
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
    $python='C:/Program Files/WindowsApps/OpenAI.CodexPrimaryRuntime.v26-1117-835-0_26.1117.835.0_x64__3k8sg7r9htsxt/dependencies/python/python.exe'
    $seed=Join-Path $PSScriptRoot 'r24-legacy-data.py'
    & $python -X utf8 $seed seed $db (Join-Path $case 'legacy-before.json')
    Require 'schema16-sample-created' ($LASTEXITCODE -eq 0)
    Install $app ('/DATADIR="'+$data+'" /PORT='+$port) 'upgrade.log'
    Require 'upgrade-still-running' ((Get-Service $service).Status -eq 'Running')
    & $python -X utf8 $seed read $db (Join-Path $case 'legacy-after.json')
    Require 'legacy-readback' ($LASTEXITCODE -eq 0)
    $before=Get-Content (Join-Path $case 'legacy-before.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $after=Get-Content (Join-Path $case 'legacy-after.json') -Raw -Encoding UTF8|ConvertFrom-Json
    Require 'schema16-upgraded-to18' ($before.schema -eq 16 -and $after.schema -eq 18)
    foreach($name in @('users','configuration','history','events')){
        Require ('preserve-'+$name) (($before.$name|ConvertTo-Json -Depth 20 -Compress) -eq ($after.$name|ConvertTo-Json -Depth 20 -Compress))
    }
    $session=Invoke-RestMethod ($endpoint+'/api/session') -SessionVariable loginWeb
    $login=@{login='qa_installer';password='QaSetupTest2026'}|ConvertTo-Json
    Invoke-RestMethod ($endpoint+'/api/auth/login') -Method Post -WebSession $loginWeb -Headers @{'X-CSRF-TOKEN'=$session.csrfToken} -ContentType 'application/json' -Body $login|Out-Null
    Require 'original-login-works-after-upgrade' ($null -ne (Invoke-RestMethod ($endpoint+'/api/session') -WebSession $loginWeb).user)
    $authorizationId=(Invoke-RestMethod ($endpoint+'/api/authorization/request') -WebSession $loginWeb).installationId
    Require 'authorization-identity-before-reinstall' (![string]::IsNullOrWhiteSpace($authorizationId))
    Uninstall $app 'uninstall-full.log'
    Require 'full-service-removed' (!(Get-Service $service -ErrorAction SilentlyContinue))
    Require 'uninstall-keeps-business-data' (Test-Path -LiteralPath $db)
    Require 'uninstall-keeps-authorization' (Test-Path -LiteralPath ($data+'.authorization'))
    Require 'uninstall-removes-server-binary' (!(Test-Path (Join-Path $record.VersionRoot 'app/server/LevelManager.Host.exe')))
    $engine=Join-Path $root 'deployment/wizard/SetupEngine.ps1'
    $engineResult=Join-Path $case 'engine-result.ini'
    $engineState=Join-Path $case 'engine-state.json'
    $engineArgs=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$engine,'-Action','Prepare','-InstallRoot',$app,'-Port',$port,'-ServiceName',$service,'-StateFile',$engineState,'-ResultFile',$engineResult,'-Confirmed')
    $retainedHash=(Get-FileHash -LiteralPath $db).Hash
    $result=Test-ReinstallPrepare $data $false
    Require 'reinstall-requires-reuse-consent' ($result -ne 0 -and (Get-Content -LiteralPath $engineResult -Raw).Contains('请在安装向导中确认重装'))
    $lock=[IO.File]::Open((Join-Path $data 'service.lock'),'OpenOrCreate','ReadWrite','Read')
    try{
        $result=Test-ReinstallPrepare $data $true
        Require 'reinstall-rejects-in-use-database' ($result -ne 0 -and (Get-Content -LiteralPath $engineResult -Raw).Contains('仍被后台占用'))
    }finally{$lock.Dispose()}
    foreach($fixtureType in @('foreign','newer')){
        $invalidData=Join-Path $case $fixtureType
        New-Item -ItemType Directory -Path $invalidData|Out-Null
        $invalidDb=Join-Path $invalidData 'level-manager.db'
        if($fixtureType -eq 'newer'){
            Copy-Item -LiteralPath $db -Destination $invalidDb
            $fixtureSql='PRAGMA user_version=19'
            $expectedFailure='高于此安装程序支持的版本'
        }else{
            $fixtureSql='CREATE TABLE unrelated (test TEXT); PRAGMA user_version=1'
            $expectedFailure='不属于液位管理系统'
        }
        & $python -c 'import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); c.executescript(sys.argv[2]); c.close()' $invalidDb $fixtureSql
        if($LASTEXITCODE -ne 0){throw '无法建立数据库拒绝样本'}
        $invalidHash=(Get-FileHash -LiteralPath $invalidDb).Hash
        $result=Test-ReinstallPrepare $invalidData $true
        Require ('reinstall-rejects-'+$fixtureType+'-database') ($result -ne 0 -and (Get-Content -LiteralPath $engineResult -Raw).Contains($expectedFailure))
        Require ('reinstall-preserves-rejected-'+$fixtureType+'-database') ((Get-FileHash -LiteralPath $invalidDb).Hash -eq $invalidHash)
    }
    Require 'no-reinstall-service-after-refusals' (!(Get-Service -Name $service -ErrorAction SilentlyContinue))
    Install $app ('/DATADIR="'+$data+'" /PORT='+$port) 'reinstall.log'
    Require 'reinstall-service-running' ((Get-Service $service).Status -eq 'Running')
    $savedDb=@(Get-ChildItem -LiteralPath (Join-Path $data '重装备份') -Filter 'level-manager.db' -File -Recurse)
    Require 'reinstall-preserves-exact-database-copy' ($savedDb.Count -eq 1 -and (Get-FileHash -LiteralPath $savedDb[0].FullName).Hash -eq $retainedHash)
    & $python -X utf8 $seed read $db (Join-Path $case 'reinstall-after.json')
    Require 'reinstall-database-readable' ($LASTEXITCODE -eq 0)
    $reinstalled=Get-Content -LiteralPath (Join-Path $case 'reinstall-after.json') -Raw -Encoding UTF8|ConvertFrom-Json
    foreach($name in @('schema','users','configuration','history','events')){
        Require ('reinstall-preserves-'+$name) (($after.$name|ConvertTo-Json -Depth 20 -Compress) -eq ($reinstalled.$name|ConvertTo-Json -Depth 20 -Compress))
    }
    $session=Invoke-RestMethod ($endpoint+'/api/session') -SessionVariable reinstallWeb
    Invoke-RestMethod ($endpoint+'/api/auth/login') -Method Post -WebSession $reinstallWeb -Headers @{'X-CSRF-TOKEN'=$session.csrfToken} -ContentType 'application/json' -Body $login|Out-Null
    Require 'reinstall-original-login-works' ($null -ne (Invoke-RestMethod ($endpoint+'/api/session') -WebSession $reinstallWeb).user)
    Require 'reinstall-authorization-identity-preserved' ((Invoke-RestMethod ($endpoint+'/api/authorization/request') -WebSession $reinstallWeb).installationId -eq $authorizationId)
    Uninstall $app 'uninstall-after-reinstall.log'
    Require 'reinstalled-service-removed' (!(Get-Service $service -ErrorAction SilentlyContinue))
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
