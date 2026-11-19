[CmdletBinding()]param([switch]$Execute)
$ErrorActionPreference='Stop'
$taskProject=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$taskOutput=Join-Path $taskProject 'output'
$taskRecord=Join-Path $taskOutput 'cleanup-records/20261107-r22-latest-only'
New-Item -ItemType Directory -Path $taskRecord -Force|Out-Null
$taskPlanFile=Join-Path $taskRecord 'plan.json'
$taskR20=Join-Path $taskOutput '客户交付资料_r20_20261102'
$taskProtected=@($taskR20,(Join-Path $taskOutput '客户交付资料_r22_最新版_20261107'),(Join-Path $taskOutput 'r22-preview-runtime_20261107-u2'),(Join-Path $taskOutput 'r22-preview-1794033145639'),(Join-Path $taskOutput 'client-viewing'),(Join-Path $taskOutput 'r22-latest-delivery-qa_20261107'),(Join-Path $taskOutput 'GT6757X本机模拟器_r22_20261107_V1.0.3'),(Join-Path $taskOutput 'cleanup-records'))
function Within([string]$Path,[string]$Parent){return [IO.Path]::GetFullPath($Path).StartsWith([IO.Path]::GetFullPath($Parent).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)}
function ReferencesPath([string]$Command,[string]$Path){
 # A current sibling such as runtime_20261107-u2 is not inside runtime_20261107.
 # Require a path separator or command argument boundary after the whole target.
 $needle=$Path.TrimEnd('\');$position=0
 while(($position=$Command.IndexOf($needle,$position,[StringComparison]::OrdinalIgnoreCase)) -ge 0){
  $after=$position+$needle.Length
  if($after -eq $Command.Length){return $true}
  $next=$Command[$after]
  if($next -in @([char]92,[char]47,[char]34,[char]39) -or [char]::IsWhiteSpace($next)){return $true}
  $position=$after
 }
 return $false
}
function Plain([string]$Path){
 $full=[IO.Path]::GetFullPath($Path)
 if(!(Within $full $taskOutput)){throw ('不在本项目软件output内：'+$full)}
 foreach($protected in $taskProtected){if($full.Equals($protected,[StringComparison]::OrdinalIgnoreCase) -or (Within $full $protected) -or (Within $protected $full)){throw ('保护目录与删除目标相交：'+$full)}}
 if(([IO.Path]::GetRelativePath($taskOutput,$full).Split([char[]]'\/')[0]) -notmatch '(?i)r22'){throw ('目标不是r22产物：'+$full)}
 $part=$full
 while($part -and !$part.Equals($taskProject,[StringComparison]::OrdinalIgnoreCase)){
  if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('目标路径包含链接：'+$part)}
  $part=[IO.Path]::GetDirectoryName($part)
 }
}
function Inventory([string]$Path){
 $item=Get-Item -LiteralPath $Path -Force;$files=New-Object Collections.Generic.List[object];$links=New-Object Collections.Generic.List[object]
 if(!$item.PSIsContainer){$files.Add($item);return @{files=@($files.ToArray());links=@()}}
 $stack=New-Object Collections.Generic.Stack[string];$stack.Push($item.FullName)
 while($stack.Count){foreach($child in Get-ChildItem -LiteralPath $stack.Pop() -Force){if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){$links.Add($child)}elseif($child.PSIsContainer){$stack.Push($child.FullName)}else{$files.Add($child)}}}
 return @{files=@($files.ToArray());links=@($links.ToArray())}
}
function Evidence([IO.FileInfo]$File){
 if($File.Extension -eq '.zip'){return $File.Name -match '(?i)backup|business|备份|source|源码'}
 return $File.Extension.ToLowerInvariant() -in '.cs','.ts','.vue','.py','.ps1','.cjs','.mjs','.sql','.md','.json','.trx','.log','.txt','.csv','.ndjson','.png','.jpg','.jpeg','.svg','.pdf','.html','.docx','.xlsx','.xls','.sha256'
}
if(!$Execute){
 if(Test-Path -LiteralPath $taskPlanFile){throw '计划已存在，不覆盖'}
 $names=@('客户交付资料_r22_20261107','客户交付资料_r22_导入修复版_20261107','r22-delivery-build_20261107','r22-import-restore-build_20261107','r22-phase2-build_20261107','r22-preview-runtime_20261107','r22-preview-runtime_20261107-u1','r22-review-build_20261107','r22-delivery-qa_20261107','r22-import-restore-qa_20261107','r22-phase2-20261107','r22-review-20261107','r22-template-import-qa_20261107','r22-simulator-qa_20261107','GT6757X本机模拟器_r22_20261107','GT6757X本机模拟器_r22_20261107_V1.0.1','GT6757X本机模拟器_r22_20261107_V1.0.2','GT6757X本机模拟器_r22_20261107_V1.0.1.zip','GT6757X本机模拟器_r22_20261107_V1.0.1.zip.sha256','GT6757X本机模拟器_r22_20261107_V1.0.2.zip','GT6757X本机模拟器_r22_20261107_V1.0.2.zip.sha256')
 # The current u2 driver has two redirected log handles here. Retain those
 # active logs and the parent, but clean the inactive test children separately.
 $names=@($names|Where-Object{$_ -ne 'r22-import-restore-qa_20261107'})
 $activeQa=Join-Path $taskOutput 'r22-import-restore-qa_20261107'
 $activeLogs=@('resume-u2-retry.stdout.log','resume-u2-retry.stderr.log')
 $targets=@();$evidence=@();$skipped=@();$inventories=@()
 if(Test-Path -LiteralPath $activeQa){foreach($child in Get-ChildItem -LiteralPath $activeQa -Force){if($child.Name -in $activeLogs){$skipped+=@{path=$child.FullName;reason='Current u2 driver log handle, retained'}}else{$names+=('r22-import-restore-qa_20261107/'+$child.Name)}}}
 foreach($name in $names){
  $path=Join-Path $taskOutput $name;if(!(Test-Path -LiteralPath $path)){continue};Plain $path;$inventory=Inventory $path
  if(@($inventory.files|Where-Object Extension -in '.pem','.key','.pfx').Count){$skipped+=@{path=$path;reason='Key-like file found, retained'};continue}
  $safeLinks=@();foreach($link in $inventory.links){
   $expected=Join-Path $taskOutput 'r22-template-import-qa_20261107/authoring/node_modules'
   $dependency='C:\Program Files\WindowsApps\OpenAI.CodexPrimaryRuntime.v26-1104-2876-0_26.1104.2876.0_x64__3k8sg7r9htsxt\dependencies\node\node_modules'
   if($link.FullName -ne $expected -or $link.LinkType -ne 'Junction' -or [string]$link.Target -ne $dependency){throw ('未知链接，停止计划：'+$link.FullName)}
   $safeLinks+=@{path=$link.FullName;target=[string]$link.Target;action='Delete junction entry only; never traverse dependency target'}
  }
  $bytes=[long](($inventory.files|Measure-Object Length -Sum).Sum)
  $targets+=@{path=$path;name=$name;bytes=$bytes;files=$inventory.files.Count;links=$safeLinks}
  foreach($file in $inventory.files){
   $relative=[IO.Path]::GetRelativePath($taskOutput,$file.FullName).Replace('\','/')
   $inventories+=@{path=$relative;bytes=$file.Length;lastWriteUtcTicks=$file.LastWriteTimeUtc.Ticks}
   if(Evidence $file){$evidence+=@{path=$file.FullName;entry=$relative;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}}
  }
 }
 $key=Join-Path $taskProject '.build-tools/vendor-keys/level-manager-signing.pem';$keyItem=Get-Item -LiteralPath $key
 $keyMeta=@{path=$key;bytes=$keyItem.Length;lastWriteUtcTicks=$keyItem.LastWriteTimeUtc.Ticks}
 $plan=@{atUtc=[DateTime]::UtcNow.ToString('o');scope=$taskOutput;targets=$targets;inventory=$inventories;evidence=$evidence;skipped=$skipped;deleteBytes=($targets|Measure-Object bytes -Sum).Sum;evidenceBytes=($evidence|Measure-Object bytes -Sum).Sum;protected=$taskProtected;privateKeyMetadata=$keyMeta}
 $plan|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $taskPlanFile -Encoding UTF8
 Write-Output ('计划 '+$targets.Count+' 个旧r22目标，'+[math]::Round($plan.deleteBytes/1GB,2)+' GiB；归档验证记录 '+$evidence.Count+' 文件；跳过 '+$skipped.Count)
 return
}
if(Test-Path -LiteralPath (Join-Path $taskRecord 'result.json')){throw '已清理完成，拒绝重复执行'}
$plan=Get-Content -LiteralPath $taskPlanFile -Raw -Encoding UTF8|ConvertFrom-Json
if($plan.scope -ne $taskOutput){throw '计划作用域不符'}
$seal=Get-Content -LiteralPath (Join-Path $taskOutput 'r22-latest-delivery-qa_20261107/package.json') -Raw -Encoding UTF8|ConvertFrom-Json
if(!$seal.passed -or $seal.build -ne '20261107-r22-u2' -or (Get-FileHash -LiteralPath $seal.zip -Algorithm SHA256).Hash -ne $seal.sha256){throw '最新交付包校验不符'}
$r20=Join-Path $taskR20 '液位管理系统_r20_自动结果复位改进版_20261102.zip'
if((Get-FileHash -LiteralPath $r20 -Algorithm SHA256).Hash -ne '464521C5A6380873E1C06DBD50583F6025187395462465BCED1AE6FC200C3E70'){throw 'r20保护包不符'}
$processes=@(Get-CimInstance Win32_Process)
foreach($target in $plan.targets){
 Plain $target.path
 foreach($process in $processes){if(($process.ExecutablePath -and ((Within $process.ExecutablePath $target.path) -or $process.ExecutablePath -eq $target.path)) -or ($process.CommandLine -and (ReferencesPath $process.CommandLine $target.path))){throw ('旧目录仍被进程引用：'+$target.path+' PID '+$process.ProcessId)}}
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=Join-Path $taskRecord '旧r22验证记录归档.zip'
if(Test-Path -LiteralPath $archive){throw '验证归档已存在，不覆盖'}
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($file in $plan.evidence){if((Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash -ne $file.sha256){throw ('记录已变化：'+$file.path)};[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.path,$file.entry,[IO.Compression.CompressionLevel]::Optimal)|Out-Null}}finally{$zip.Dispose()}
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try{
 if($zip.Entries.Count -ne @($plan.evidence).Count){throw '归档数量不符'}
 foreach($file in $plan.evidence){$entry=$zip.GetEntry($file.entry);$stream=$entry.Open();$hash=[Security.Cryptography.SHA256]::Create();try{if([BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','') -ne $file.sha256){throw ('归档校验失败：'+$file.entry)}}finally{$stream.Dispose();$hash.Dispose()}}
}finally{$zip.Dispose()}
$deleted=@()
foreach($target in $plan.targets){
 Plain $target.path;$current=Inventory $target.path
 if($current.files.Count -ne $target.files -or [long](($current.files|Measure-Object Length -Sum).Sum) -ne $target.bytes){throw ('目标内容已变化，停止：'+$target.path)}
 $expectedFiles=@{};foreach($row in $plan.inventory){if($row.path.StartsWith($target.name+'/') -or $row.path -eq $target.name){$expectedFiles[$row.path]=$row}}
 foreach($file in $current.files){$relative=[IO.Path]::GetRelativePath($taskOutput,$file.FullName).Replace('\','/');$expected=$expectedFiles[$relative];if(!$expected -or $expected.bytes -ne $file.Length -or $expected.lastWriteUtcTicks -ne $file.LastWriteTimeUtc.Ticks){throw ('文件已变化：'+$file.FullName)}}
 foreach($link in $target.links){$item=Get-Item -LiteralPath $link.path -Force;if($item.LinkType -ne 'Junction' -or [string]$item.Target -ne $link.target){throw '依赖链接目标已变化'};[IO.Directory]::Delete($link.path);if(Test-Path -LiteralPath $link.path){throw '链接未移除'}}
 if(@((Inventory $target.path).links).Count){throw '尚有链接，禁止递归删除'}
 Plain $target.path;Remove-Item -LiteralPath $target.path -Recurse -Force
 if(Test-Path -LiteralPath $target.path){throw ('目标未删除：'+$target.path)}
 $deleted+=$target;@{atUtc=[DateTime]::UtcNow.ToString('o');path=$target.path;bytes=$target.bytes}|ConvertTo-Json -Compress|Add-Content -LiteralPath (Join-Path $taskRecord 'deleted.ndjson') -Encoding UTF8
}
$key=Get-Item -LiteralPath $plan.privateKeyMetadata.path
if($key.Length -ne $plan.privateKeyMetadata.bytes -or $key.LastWriteTimeUtc.Ticks -ne $plan.privateKeyMetadata.lastWriteUtcTicks){throw '私钥元数据变化'}
if((Get-FileHash -LiteralPath $r20 -Algorithm SHA256).Hash -ne '464521C5A6380873E1C06DBD50583F6025187395462465BCED1AE6FC200C3E70'){throw 'r20校验变化'}
$result=@{passed=$true;deletedTargets=$deleted.Count;deletedFiles=($deleted|Measure-Object files -Sum).Sum;deletedBytes=($deleted|Measure-Object bytes -Sum).Sum;archive=$archive;archiveBytes=(Get-Item -LiteralPath $archive).Length;archiveSha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash;evidenceFiles=@($plan.evidence).Count;latestZip=$seal.zip;latestSha256=$seal.sha256;r20Retained=$true;privateKeyMetadataUnchanged=$true;currentRuntimeAndDataOutsideDeleteScope=$true;sourceOutsideDeleteScope=$true;atUtc=[DateTime]::UtcNow.ToString('o')}
$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $taskRecord 'result.json') -Encoding UTF8
Write-Output ($result|ConvertTo-Json -Depth 4)
