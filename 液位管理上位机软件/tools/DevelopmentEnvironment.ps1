$ErrorActionPreference='Stop'
$taskProjectRoot=Split-Path -Parent $PSScriptRoot
$taskLocalDotnet=Join-Path $taskProjectRoot '.build-tools/dotnet/dotnet.exe'
$taskDotnet=if(Test-Path -LiteralPath $taskLocalDotnet){$taskLocalDotnet}else{(Get-Command dotnet -ErrorAction Stop).Source}
$env:DOTNET_ROOT=Split-Path -Parent $taskDotnet
$env:DOTNET_CLI_HOME=Join-Path $taskProjectRoot '.build-tools/dotnet-home'
$env:NUGET_PACKAGES=Join-Path $taskProjectRoot '.build-tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$taskPnpm=(Get-Command pnpm -ErrorAction Stop).Source
$taskBundledNode=Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin'
if(Test-Path -LiteralPath (Join-Path $taskBundledNode 'node.exe')){$env:PATH=$taskBundledNode+';'+$env:PATH}
function Invoke-TaskDotnet { & $taskDotnet @args;if($LASTEXITCODE -ne 0){throw 'dotnet执行失败'} }
function Invoke-TaskPnpm { & $taskPnpm --dir (Join-Path $taskProjectRoot 'web') @args;if($LASTEXITCODE -ne 0){throw 'pnpm执行失败'} }
