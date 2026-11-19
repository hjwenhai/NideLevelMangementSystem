#requires -Version 7.0
param([switch]$Restore,[switch]$SkipTests,[switch]$SkipWeb)
. (Join-Path $PSScriptRoot 'DevelopmentEnvironment.ps1')
New-Item -ItemType Directory -Path (Join-Path $taskProjectRoot 'output') -Force | Out-Null
if($Restore){
 Invoke-TaskDotnet restore (Join-Path $taskProjectRoot 'LevelManager.slnx') --locked-mode --verbosity minimal
 if(-not $SkipWeb){Invoke-TaskPnpm install --frozen-lockfile --store-dir (Join-Path $taskProjectRoot '.build-tools/pnpm-store') --cache-dir (Join-Path $taskProjectRoot '.build-tools/pnpm-cache')}
}
if(-not $SkipWeb){Invoke-TaskPnpm run build}
Invoke-TaskDotnet build (Join-Path $taskProjectRoot 'LevelManager.slnx') --no-restore --configuration Release --verbosity minimal '-m:1'
if(-not $SkipTests){Invoke-TaskDotnet test (Join-Path $taskProjectRoot 'tests/LevelManager.Tests/LevelManager.Tests.csproj') --no-build --no-restore --configuration Release --logger 'trx;LogFileName=foundation.trx' --results-directory (Join-Path $taskProjectRoot 'output/test-results') --verbosity minimal}
Invoke-TaskDotnet publish (Join-Path $taskProjectRoot 'src/LevelManager.Host/LevelManager.Host.csproj') --no-build --no-restore --configuration Release --output (Join-Path $taskProjectRoot 'output/engineering') --verbosity minimal
Write-Output '工程版已生成到output/engineering；当前为内部工程开发版，客户测试版尚在开发。'
