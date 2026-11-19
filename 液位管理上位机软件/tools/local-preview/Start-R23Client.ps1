$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Start-R23Backend.ps1')
. (Join-Path $PSScriptRoot 'R23Preview.Common.ps1')
$sessionFile=Join-Path $PSScriptRoot 'r23-preview-session.json'
$session=Get-Content -LiteralPath $sessionFile -Raw -Encoding UTF8|ConvertFrom-Json
$health=Invoke-RestMethod -Uri ($session.url.TrimEnd('/')+'/api/health') -TimeoutSec 5
if($health.build -ne '20261113-r23' -or $session.clientExe -ne $clientExe -or $session.url -ne $endpoint.AbsoluteUri -or $session.profileRoot -ne $identity.profileRoot){throw 'r23客户端连接身份不符，已取消打开'}
$saved=$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
try{
 $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=$null
 $p=Start-Process -FilePath $clientExe -ArgumentList @('--server-url',$session.url,'--profile-root',('"'+$session.profileRoot+'"')) -WorkingDirectory (Split-Path $clientExe -Parent) -WindowStyle Normal -PassThru
 $session|Add-Member -NotePropertyName clientPid -NotePropertyValue $p.Id -Force
 $session|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $sessionFile -Encoding UTF8
 Write-Output ('r23客户端已打开，PID '+$p.Id)
}finally{$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=$saved}
