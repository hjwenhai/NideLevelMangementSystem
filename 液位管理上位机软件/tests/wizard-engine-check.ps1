#requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$qa=Join-Path $root 'output/wizard-installer-qa_20261110'
New-Item -ItemType Directory -Force -Path $qa | Out-Null
$engine=Join-Path $root 'deployment/wizard/SetupEngine.ps1'
$powershell=Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
$serviceName='Naide.LevelManager.QA.Wizard20261110'
$payload=Join-Path $root 'output/客户交付资料_r22_最新版_20261107/液位管理系统_r22_客户交付版_20261107'
$fixture=Join-Path $qa ('case-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $fixture|Out-Null
$appRoot=Join-Path $fixture '程序目录'
$dataRoot=Join-Path $fixture '业务数据'
$port=58640
$listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
$script:checks=New-Object Collections.Generic.List[object]
function Run-Engine([string]$name,[string[]]$values,[bool]$expected=$true){
    $file=Join-Path $fixture ($name+'.txt')
    $previous=$ErrorActionPreference;$ErrorActionPreference='Continue'
    try{& $powershell -NoProfile -ExecutionPolicy Bypass -File $engine @values -ServiceName $serviceName -ResultFile $file 2>&1|Out-Null;$code=$LASTEXITCODE}finally{$ErrorActionPreference=$previous}
    $message=if(Test-Path -LiteralPath $file){[IO.File]::ReadAllText($file)}else{''}
    $pass=(($code -eq 0) -eq $expected)
    $script:checks.Add(@{name=$name;passed=$pass;exitCode=$code;message=$message})
    if(!$pass){throw ($name+' failed: '+$message)}
}
$plan=@('-InstallRoot',$appRoot,'-DataRoot',$dataRoot,'-Port',[string]$port,'-Mode','Full')
$state=Join-Path $fixture 'transaction.json'
$snapshot=@(Get-CimInstance Win32_Process -Filter "Name='LevelManager.Host.exe'" | Select-Object ProcessId,CommandLine)
$success=$false
try{
    if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){throw 'QA服务已存在，请先核对现场'}
    Run-Engine 'inspect' @('-Action','Inspect','-InstallRoot',$appRoot)
    Run-Engine 'fresh-validation' (@('-Action','Validate')+$plan)
    Run-Engine 'overlap-rejected' @('-Action','Validate','-InstallRoot',$appRoot,'-DataRoot',(Join-Path $appRoot 'data'),'-Port',[string]$port) $false
    Run-Engine 'disk-root-rejected' @('-Action','Validate','-InstallRoot',$appRoot,'-DataRoot','E:\','-Port',[string]$port) $false
    Run-Engine 'client-url-invalid' @('-Action','Validate','-InstallRoot',$appRoot,'-Mode','Client','-ServerUrl','http://example.com/path') $false
    Run-Engine 'client-url-valid' @('-Action','Validate','-InstallRoot',$appRoot,'-Mode','Client','-ServerUrl','http://192.168.1.10:8780/')
    Run-Engine 'prepare-confirmation-required' (@('-Action','Prepare','-StateFile',$state)+$plan) $false
    Run-Engine 'prepare' (@('-Action','Prepare','-StateFile',$state,'-Confirmed')+$plan)
    $version=Join-Path $appRoot 'versions/20261107-r22-u2-setup1/app'
    New-Item -ItemType Directory -Path (Join-Path $version 'client') -Force|Out-Null
    Copy-Item -LiteralPath (Join-Path $payload 'app/client/LevelManager.Desktop.exe') -Destination (Join-Path $version 'client')
    Copy-Item -LiteralPath (Join-Path $payload 'app/server') -Destination $version -Recurse
    Run-Engine 'commit-start-health' (@('-Action','Commit','-StateFile',$state)+$plan)
    if(!(Test-Path -LiteralPath (Join-Path $dataRoot 'level-manager.db'))){throw '没有创建业务库'}
    $health=Invoke-RestMethod ('http://127.0.0.1:'+$port+'/api/health')
    if($health.build -ne '20261107-r22-u2'){throw '后台构建不匹配'}
    Run-Engine 'inspect-upgrade' @('-Action','Inspect','-InstallRoot',$appRoot)
    Run-Engine 'upgrade-change-data-rejected' @('-Action','Validate','-InstallRoot',$appRoot,'-DataRoot',(Join-Path $fixture 'other-data'),'-Port',[string]$port) $false
    Run-Engine 'upgrade-to-client-rejected' @('-Action','Validate','-InstallRoot',$appRoot,'-Mode','Client') $false
    Run-Engine 'upgrade-validation' (@('-Action','Validate')+$plan)
    $upgrade=Join-Path $fixture 'upgrade-transaction.json'
    Run-Engine 'upgrade-stop' (@('-Action','Prepare','-StateFile',$upgrade,'-Confirmed')+$plan)
    if((Get-Service -Name $serviceName).Status -ne 'Stopped'){throw '旧测试服务未停止'}
    Run-Engine 'prepare-failure-rollback' @('-Action','Rollback','-InstallRoot',$appRoot,'-StateFile',$upgrade)
    if((Get-Service -Name $serviceName).Status -ne 'Running'){throw '写入新库前取消未恢复原服务'}
    Run-Engine 'upgrade-prepare-again' (@('-Action','Prepare','-StateFile',$upgrade,'-Confirmed')+$plan)
    Run-Engine 'upgrade-commit' (@('-Action','Commit','-StateFile',$upgrade)+$plan)
    $record=Get-Content (Join-Path $appRoot 'installation.json') -Raw -Encoding UTF8|ConvertFrom-Json
    if($record.DataRoot -ne $dataRoot){throw '升级数据路径变化'}
    Run-Engine 'uninstall-confirmation-required' @('-Action','Uninstall','-InstallRoot',$appRoot) $false
    Run-Engine 'uninstall' @('-Action','Uninstall','-InstallRoot',$appRoot,'-Confirmed')
    if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){throw 'QA服务未移除'}
    if(!(Test-Path (Join-Path $dataRoot 'level-manager.db')) -or !(Test-Path ($dataRoot+'.authorization'))){throw '卸载丢失业务库或授权目录'}
    $after=@(Get-CimInstance Win32_Process -Filter "Name='LevelManager.Host.exe'"|Select-Object ProcessId,CommandLine)
    foreach($original in $snapshot){if(!($after|Where-Object ProcessId -eq $original.ProcessId)){throw '原运行后台发生变化'}}
    $success=$true
}catch{
    $_|Out-String|Set-Content (Join-Path $qa 'failure.txt') -Encoding UTF8
}finally{
    $svc=Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if($svc){if($svc.Status -ne 'Stopped'){Stop-Service -Name $serviceName;$svc.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))};sc.exe delete $serviceName|Out-Null}
    @{passed=$success;checks=$checks;fixture=$fixture;testService=$serviceName;port=$port;existingHosts=$snapshot;realInstrumentWrites=0;atUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 10|Set-Content (Join-Path $qa 'engine-result.json') -Encoding UTF8
}
if(!$success){exit 1}
