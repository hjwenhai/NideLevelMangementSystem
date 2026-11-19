$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Start-R24Backend.ps1')
. (Join-Path $PSScriptRoot 'R24Preview.Common.ps1')
$sessionFile=Join-Path $PSScriptRoot 'r24-preview-session.json'
$session=Get-Content -LiteralPath $sessionFile -Raw -Encoding UTF8|ConvertFrom-Json
$health=Invoke-RestMethod -Uri ($session.url.TrimEnd('/')+'/api/health') -TimeoutSec 5
if($health.build -ne '20261118-r24' -or $session.clientExe -ne $clientExe -or $session.url -ne $endpoint.AbsoluteUri -or $session.profileRoot -ne $identity.profileRoot){throw 'r24客户端连接身份不符，已取消打开'}
$saved=$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
try{
 $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=$null
 $p=Start-Process -FilePath $clientExe -ArgumentList @('--server-url',$session.url,'--profile-root',('"'+$session.profileRoot+'"')) -WorkingDirectory (Split-Path $clientExe -Parent) -WindowStyle Normal -PassThru
 $session|Add-Member -NotePropertyName clientPid -NotePropertyValue $p.Id -Force
 $session|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $sessionFile -Encoding UTF8
 Write-Output ('r24客户端已打开，PID '+$p.Id)
}finally{$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=$saved}
