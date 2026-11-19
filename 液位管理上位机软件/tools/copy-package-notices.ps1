param([Parameter(Mandatory=$true)][string]$ApplicationRoot,[Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
$noticeProjectRoot=Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Path $Destination -Force|Out-Null
Copy-Item -Path (Join-Path $noticeProjectRoot 'deployment/third-party/*') -Destination $Destination -Force
$components=@{}
foreach($deps in Get-ChildItem -LiteralPath $ApplicationRoot -Recurse -File -Filter '*.deps.json'){
 $document=Get-Content -LiteralPath $deps.FullName -Raw -Encoding UTF8|ConvertFrom-Json
 foreach($library in $document.libraries.PSObject.Properties){
  if($library.Value.type -ne 'package' -or $components.ContainsKey($library.Name)){continue}
  $parts=$library.Name.Split('/');$packageDirectory=Join-Path $noticeProjectRoot ('.build-tools/nuget/'+$parts[0].ToLowerInvariant()+'/'+$parts[1]);$spec=Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File -ErrorAction SilentlyContinue|Select-Object -First 1
  if(!$spec){throw ('缺少组件版权元数据：'+$library.Name)}
  [xml]$metadata=Get-Content -LiteralPath $spec.FullName -Raw -Encoding UTF8
  $components[$library.Name]=[ordered]@{package=$library.Name;authors=[string]$metadata.package.metadata.authors;copyright=[string]$metadata.package.metadata.copyright;license=[string]$metadata.package.metadata.license.InnerText;licenseUrl=[string]$metadata.package.metadata.licenseUrl;projectUrl=[string]$metadata.package.metadata.projectUrl}
 }
}
@($components.Values|Sort-Object package)|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Destination 'NuGet组件许可索引.json') -Encoding UTF8
