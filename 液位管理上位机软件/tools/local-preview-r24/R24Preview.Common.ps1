$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$preview=Join-Path $project 'output\r24-preview_20261118'
$identity=Get-Content -LiteralPath (Join-Path $preview 'preview-identity.json') -Raw -Encoding UTF8|ConvertFrom-Json
$data=Join-Path $preview 'data'
$server=Join-Path $project 'output\r24-feedback_20261118\payload\app\server'
$legacyHostExe=Join-Path $server 'LevelManager.Host.exe'
$releasePath=Join-Path $project 'output\客户安装向导_r24_20261118\release.json'
if(Test-Path -LiteralPath $releasePath){
 $release=Get-Content -LiteralPath $releasePath -Raw -Encoding UTF8 | ConvertFrom-Json
 if($release.build -eq '20261118-r24' -and $release.payloadDirectory){
  $publishedPayload=[IO.Path]::GetFullPath((Join-Path $project $release.payloadDirectory))
  $outputPrefix=(Join-Path $project 'output')+[IO.Path]::DirectorySeparatorChar
  if(!$publishedPayload.StartsWith($outputPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'r24发布目录必须位于本工程的output目录内。'}
  $server=Join-Path $publishedPayload 'app\server'
 }
}
$hostExe=Join-Path $server 'LevelManager.Host.exe'
$clientExe=Join-Path $project 'output\r24-feedback_20261118\payload\app\client\LevelManager.Desktop.exe'
$simulatorExe=Join-Path $project 'output\GT6757X本机模拟器_r22_20261107_V1.0.3\app\LevelManager.GatewaySimulator.exe'
$endpoint=[Uri]$identity.url
$gatewayPort=[int]$identity.gatewayPort
if($identity.revision -ne 'r24' -or $identity.projectRoot -ne $project -or $identity.dataRoot -ne $data -or $identity.profileRoot -ne (Join-Path $preview 'client-profile') -or $endpoint.Scheme -ne 'http' -or $endpoint.Host -ne '127.0.0.1' -or $endpoint.AbsolutePath -ne '/' -or $endpoint.Query -or $endpoint.Fragment -or $endpoint.UserInfo -or $gatewayPort -lt 1024 -or $gatewayPort -gt 65535 -or $gatewayPort -eq $endpoint.Port){throw 'r24本机入口设置不符，未启动。请保留现有数据目录。'}
$url=$endpoint.AbsoluteUri.TrimEnd('/')
function Assert-R24PortAvailable([int]$Port,[string]$Purpose){
 $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$Port)
 try{$listener.Start()}catch [Net.Sockets.SocketException]{
  if($_.Exception.SocketErrorCode -eq [Net.Sockets.SocketError]::AccessDenied){throw "$Purpose 无法使用本机端口 ${Port}：Windows 拒绝绑定，可能属于系统保留端口。无需删除数据或修改防火墙；请检查本机入口设置。"}
  if($_.Exception.SocketErrorCode -eq [Net.Sockets.SocketError]::AddressAlreadyInUse){throw "$Purpose 的本机端口 $Port 已被占用，不会关闭其他程序。"}
  throw
 }finally{$listener.Stop()}
}
function Start-R24OptionalSimulator {
 try{& (Join-Path $PSScriptRoot 'Start-R24Simulator.ps1')}
 catch{Write-Warning ('本机模拟网关未启动：'+$_.Exception.Message+' 后台仍可启动；使用该模拟通道的储罐暂时没有数据，其他通道沿用原配置。')}
}
