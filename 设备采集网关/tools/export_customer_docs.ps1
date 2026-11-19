#requires -Version 7.0
param([ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$Revision='20261117-r1')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskName='gt6757s-customer-v3.2-d2-'+$Revision
$taskStage=Join-Path $taskRoot ('output/customer-docs/'+$taskName)
$taskZip=$taskStage+'.zip'
if((Test-Path -LiteralPath $taskStage) -or (Test-Path -LiteralPath $taskZip)){throw 'Revision exists; choose a new Revision'}
$taskSources=@('docs/12_客户通讯协议V3.md','output/pdf/GT6757S_客户通讯协议_V3.2-D2_20261117-r1.pdf')
foreach($taskSource in $taskSources){if(!(Test-Path -LiteralPath (Join-Path $taskRoot $taskSource))){throw ('Missing '+$taskSource)}}
$taskSpec=Get-Content -LiteralPath (Join-Path $taskRoot 'protocols/north-v3.json') -Raw | ConvertFrom-Json
if($taskSpec.version -ne 3 -or $taskSpec.capacity -ne 64){throw 'Protocol version mismatch'}
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
foreach($taskSource in $taskSources){Copy-Item -LiteralPath (Join-Path $taskRoot $taskSource) -Destination $taskStage}
$taskReadme="# GT6757S客户通讯协议`n`n接口V3.2-D2，软件0.8.3，厂家资料包V1.8。第一页读取，第二页控制。全64台使用同一网关Unit ID。仪表故障字非零时允许下发已开通的操作和独立停止，仪表自行判断是否接受。通讯应答及指令回读确认后返回状态3，机械执行进度和结果继续查看仪表数据。旧错误3为历史保留码，人工核查后可复位。`n`n连接参数、罐号和允许动作以厂家现场交付设置为准。控制结果状态5须人工核查并留档后复位，同周期相同指令重复提交只查询原周期。`n"
[IO.File]::WriteAllText((Join-Path $taskStage 'README.md'),$taskReadme,[Text.UTF8Encoding]::new($false))
$taskLines=Get-ChildItem -LiteralPath $taskStage -File | Sort-Object Name | ForEach-Object {(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower()+'  '+$_.Name}
[IO.File]::WriteAllText((Join-Path $taskStage 'SHA256SUMS'),($taskLines -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
Compress-Archive -LiteralPath $taskStage -DestinationPath $taskZip -CompressionLevel Optimal
Get-FileHash -LiteralPath $taskZip -Algorithm SHA256 | Select-Object Path,Hash
