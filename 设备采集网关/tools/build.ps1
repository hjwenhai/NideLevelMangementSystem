#requires -Version 7.0
param([switch]$SkipTests, [switch]$SkipWeb)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
$taskGo = Join-Path $taskRoot '.build-tools/go/bin/go.exe'
if (-not (Test-Path -LiteralPath $taskGo)) { $taskGo = (Get-Command go -ErrorAction Stop).Source }
$env:GOTOOLCHAIN='local'
$env:GOCACHE=Join-Path $taskRoot '.build-cache/go'
$env:GOMODCACHE=Join-Path $taskRoot '.module-cache'
$env:TEMP=Join-Path $taskRoot '.build-cache/tmp'
$env:TMP=$env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
function Invoke-Go { & $taskGo @args; if ($LASTEXITCODE -ne 0) { throw "Go failed: $args" } }
if (-not $SkipWeb) { & npm.cmd --prefix src/web run build; if($LASTEXITCODE -ne 0){throw 'TypeScript build failed; run npm ci --prefix src/web first'} }
# Limit discovery to all production packages; archives and temporary installer links are not Go source trees.
$taskPackages=@('./protocols/...','./src/...')
Invoke-Go fmt @taskPackages
if (-not $SkipTests) { Invoke-Go vet @taskPackages; Invoke-Go test @taskPackages -count=1 }
$env:CGO_ENABLED='0'
$taskTargets=@(@{OS='windows';Arch='amd64';Name='windows-amd64';Suffix='.exe'},@{OS='linux';Arch='arm64';Name='linux-arm64';Suffix=''},@{OS='linux';Arch='arm';Name='linux-armv7';Suffix=''})
try {
 foreach($target in $taskTargets) {
  $env:GOOS=$target.OS; $env:GOARCH=$target.Arch
  if($target.Arch -eq 'arm'){$env:GOARM='7'}
  $targetDir=Join-Path $taskRoot ('output/'+$target.Name)
  New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
  foreach($command in @('gateway-core','gateway-web','gateway-updater')) {
   Invoke-Go build -buildvcs=false -trimpath -ldflags '-s -w' -o (Join-Path $targetDir ($command+$target.Suffix)) ('./src/cmd/'+$command)
  }
 }
} finally { Remove-Item Env:GOOS,Env:GOARCH,Env:GOARM -ErrorAction SilentlyContinue }
Invoke-Go build -buildvcs=false -trimpath -ldflags '-s -w' -o (Join-Path $taskRoot 'output/windows-amd64/gateway-release.exe') ./src/cmd/gateway-release
Invoke-Go build -buildvcs=false -trimpath -ldflags '-s -w' -o (Join-Path $taskRoot 'output/windows-amd64/gateway-license.exe') ./src/cmd/gateway-license
Invoke-Go version | Set-Content -LiteralPath output/toolchain.txt -Encoding utf8
Invoke-Go list -m all | Set-Content -LiteralPath output/dependencies.txt -Encoding utf8
Get-ChildItem output -Recurse -File | Where-Object { $_.Name -in @('gateway-core','gateway-web','gateway-core.exe','gateway-web.exe') } | ForEach-Object {
 $hash=Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
 ($hash.Hash.ToLower()+'  '+$_.FullName.Substring($taskRoot.Length+1).Replace('\','/'))
} | Set-Content -LiteralPath output/binary-sha256.txt -Encoding utf8
Write-Host 'Built Windows amd64, Linux ARM64 and ARMv7 (board validation pending).'
