#requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$qa=Join-Path $root 'output/r24-lan-host_20261119'
$case=Join-Path $qa ('host-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$server=Join-Path $case 'server'
$data=Join-Path $case 'data'
$checks=[Collections.Generic.List[object]]::new()
$serverProcess=$null
function Require([string]$name,[bool]$ok){$checks.Add(@{name=$name;passed=$ok});if(!$ok){throw $name}}
function Read-Page([string]$path,[string]$hostHeader){
    Invoke-WebRequest -Uri ($script:url+$path) -Headers @{Host=$hostHeader} -SkipHttpErrorCheck -NoProxy -TimeoutSec 4
}
function Start-TestHost([string]$name){
    $script:serverProcess=Start-Process -FilePath (Join-Path $server 'LevelManager.Host.exe') -ArgumentList ('--data-root "'+$data+'" --urls "'+$url+'"') -WorkingDirectory $server -WindowStyle Hidden -RedirectStandardOutput (Join-Path $case ($name+'.out.log')) -RedirectStandardError (Join-Path $case ($name+'.err.log')) -PassThru
    for($i=0;$i -lt 60;$i++){
        if($serverProcess.HasExited){throw '隔离后台启动失败'}
        try{if((Read-Page '/api/health' ('127.0.0.1:'+$port)).StatusCode -eq 200){return}}catch{}
        Start-Sleep -Milliseconds 250
    }
    throw '隔离后台启动超时'
}
function Stop-TestHost{
    if($script:serverProcess -and !$script:serverProcess.HasExited){Stop-Process -Id $script:serverProcess.Id -Force;$script:serverProcess.WaitForExit()}
    $script:serverProcess=$null
}
$passed=$false
try{
    New-Item -ItemType Directory -Path $case -Force|Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'output/r24-feedback_20261118/payload/app/server') -Destination $server -Recurse
    Copy-Item -LiteralPath (Join-Path $qa 'payload-appsettings-before.json') -Destination (Join-Path $server 'appsettings.json') -Force
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
    $script:url='http://127.0.0.1:'+$port
    Start-TestHost 'before'
    $old=Read-Page '/api/health' ('192.168.152.128:'+$port)
    Require 'old-release-reproduces-400' ($old.StatusCode -eq 400 -and $old.Content.Contains('Bad Request - Invalid Hostname'))
    Stop-TestHost
    Copy-Item -LiteralPath (Join-Path $root 'src/LevelManager.Host/appsettings.json') -Destination (Join-Path $server 'appsettings.json') -Force
    Start-TestHost 'after'
    foreach($hostName in @('localhost','127.0.0.1','192.168.152.128','192.168.1.10','[::1]','[fd00::1234]')){
        $reply=Read-Page '/api/health' ($hostName+':'+$port)
        Require ('health-accepts-'+$hostName) ($reply.StatusCode -eq 200 -and ($reply.Content|ConvertFrom-Json).build -eq '20261118-r24')
    }
    $pageReply=Read-Page '/' ('192.168.152.128:'+$port)
    Require 'browser-home-accepts-vm-ip' ($pageReply.StatusCode -eq 200 -and $pageReply.Content.Contains('<div id="app">'))
    $reject=Read-Page '/api/health' ('unconfigured.example.invalid:'+$port)
    Require 'unconfigured-hostname-still-rejected' ($reject.StatusCode -eq 400 -and ($reject.Content|ConvertFrom-Json).error -eq '访问主机名未配置')
    $session=Read-Page '/api/session' ('192.168.152.128:'+$port)
    Require 'session-endpoint-accepts-vm-ip' ($session.StatusCode -eq 200 -and ($session.Content|ConvertFrom-Json).csrfToken)
    $private=Read-Page '/api/authorization/status' ('192.168.152.128:'+$port)
    Require 'protected-api-still-requires-login' ($private.StatusCode -eq 401)
    $passed=$true
}catch{$_|Out-String|Set-Content -LiteralPath (Join-Path $case 'failure.txt') -Encoding UTF8}
finally{
    Stop-TestHost
    @{passed=$passed;fixture=$case;checks=$checks;liveBackendRestarted=$false;realDeviceActions=0}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $qa 'result.json') -Encoding UTF8
}
Get-Content -LiteralPath (Join-Path $qa 'result.json') -Raw -Encoding UTF8
if(!$passed){exit 1}
