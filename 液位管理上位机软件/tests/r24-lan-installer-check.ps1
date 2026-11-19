#requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$qa=Join-Path $root 'output/r24-lan-host_20261119'
$exe=Join-Path $root 'output/r24-feedback_20261118/qa/installer/package/液位管理系统_r24_安装程序_20261118.exe'
$case=Join-Path $qa ('installed-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$app=Join-Path $case 'app'
$data=Join-Path $case 'data'
$service='Naide.LevelManager.QA.WizardPackage'
$checks=[Collections.Generic.List[object]]::new()
function Require([string]$name,[bool]$ok){$checks.Add(@{name=$name;passed=$ok});if(!$ok){throw $name}}
$passed=$false
New-Item -ItemType Directory -Path $case -Force|Out-Null
try{
    Require 'qa-service-not-present' (!(Get-Service $service -ErrorAction SilentlyContinue))
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
    $arguments='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /MODE=Full /DATADIR="'+$data+'" /PORT='+$port+' /MERGETASKS="!desktopicon" /DIR="'+$app+'" /LOG="'+(Join-Path $case 'install.log')+'"'
    $process=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    Require 'full-installer-completed' ($process.ExitCode -eq 0)
    $record=Get-Content -LiteralPath (Join-Path $app 'installation.json') -Raw -Encoding UTF8|ConvertFrom-Json
    Require 'isolated-full-install' ($record.Mode -eq 'Full' -and $record.DataRoot -eq $data -and $record.ServiceName -eq $service)
    $config=Get-Content -LiteralPath (Join-Path $record.VersionRoot 'app/server/appsettings.json') -Raw -Encoding UTF8|ConvertFrom-Json
    Require 'installed-host-filter-updated' ($config.AllowedHosts -eq '*')
    $url='http://127.0.0.1:'+$port
    $headers=@{Host=('192.168.152.128:'+$port)}
    $health=Invoke-WebRequest -Uri ($url+'/api/health') -Headers $headers -SkipHttpErrorCheck -NoProxy -TimeoutSec 5
    Require 'installed-backend-accepts-vm-ip-host' ($health.StatusCode -eq 200 -and ($health.Content|ConvertFrom-Json).build -eq '20261118-r24')
    $pageReply=Invoke-WebRequest -Uri ($url+'/') -Headers $headers -SkipHttpErrorCheck -NoProxy -TimeoutSec 5
    Require 'installed-home-accepts-vm-ip' ($pageReply.StatusCode -eq 200 -and $pageReply.Content.Contains('<div id="app">'))
    $private=Invoke-WebRequest -Uri ($url+'/api/authorization/status') -Headers $headers -SkipHttpErrorCheck -NoProxy -TimeoutSec 5
    Require 'installed-api-still-requires-login' ($private.StatusCode -eq 401)
    $invalid=Invoke-WebRequest -Uri ($url+'/api/health') -Headers @{Host='unconfigured.example.invalid'} -SkipHttpErrorCheck -NoProxy -TimeoutSec 5
    Require 'installed-unconfigured-hostname-rejected' ($invalid.StatusCode -eq 400)
    Require 'installation-guide-still-excluded' (!(Test-Path -LiteralPath (Join-Path $app '安装向导使用说明.html')) -and !(Test-Path -LiteralPath (Join-Path $record.VersionRoot 'docs/r24修订与升级说明.html')))
    $passed=$true
}catch{$_|Out-String|Set-Content -LiteralPath (Join-Path $case 'failure.txt') -Encoding UTF8}
finally{
    $uninstaller=Join-Path $app 'unins000.exe'
    if(Test-Path -LiteralPath $uninstaller){
        $process=Start-Process -FilePath $uninstaller -ArgumentList ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="'+(Join-Path $case 'uninstall.log')+'"') -WindowStyle Hidden -Wait -PassThru
        $clean=$process.ExitCode -eq 0 -and !(Get-Service $service -ErrorAction SilentlyContinue)
        $checks.Add(@{name='qa-uninstall-removes-test-service';passed=$clean});if(!$clean){$passed=$false}
    }
    @{passed=$passed;fixture=$case;checks=$checks;liveBackendChanged=$false;realDeviceActions=0}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'installer-result.json') -Encoding UTF8
}
Get-Content -LiteralPath (Join-Path $qa 'installer-result.json') -Raw -Encoding UTF8
if(!$passed){exit 1}
