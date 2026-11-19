[CmdletBinding()]param([switch]$Execute)
$ErrorActionPreference='Stop'
$taskProject=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$taskOutput=Join-Path $taskProject 'output'
$taskRecord=Join-Path $taskOutput 'cleanup-records/20261107-keep-r20-r22'
New-Item -ItemType Directory -Path $taskRecord -Force|Out-Null
$taskPlanFile=Join-Path $taskRecord 'plan.json'
function Within([string]$Path,[string]$Parent){$p=[IO.Path]::GetFullPath($Path);$base=[IO.Path]::GetFullPath($Parent).TrimEnd('\')+'\';return $p.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)}
function RetainedName([string]$Name){return $Name -match '(?i)r(?:20|22)(?!\d)' -or $Name -match '^(供方|cleanup-records$)'}
function Plain([string]$Path){
 if(!(Within $Path $taskOutput)){throw ('不在软件output内 '+$Path)}
 $part=[IO.Path]::GetFullPath($Path)
 while($part -and !$part.Equals($taskProject,[StringComparison]::OrdinalIgnoreCase)){
  if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('链接目录禁止清理 '+$part)}
  $part=[IO.Path]::GetDirectoryName($part)
 }
}
function FilesOf([string]$Path){$item=Get-Item -LiteralPath $Path -Force;if($item.PSIsContainer){return @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force)};return @($item)}
function Evidence([IO.FileInfo]$File,[bool]$InPackage){
 $n=$File.Name;$ext=$File.Extension.ToLowerInvariant()
 if($ext -in '.cs','.ts','.vue','.py','.ps1','.cjs','.mjs','.sql','.md'){return $true}
 if($ext -eq '.zip'){return $n -match '(?i)(source|源码|基线|backup|business|备份|\.csv\.zip$)'}
 if($InPackage){return $File.FullName -match '\\docs\\' -or $n -in 'package-manifest.json' -or $ext -eq '.sha256'}
 return $ext -in '.json','.trx','.log','.txt','.csv','.ndjson','.png','.jpg','.jpeg','.svg','.pdf','.html','.docx','.xlsx','.xls','.sha256'
}
if(!$Execute){
 if(Test-Path -LiteralPath $taskPlanFile){throw '清理计划已存在，不覆盖'}
 $taskTargets=New-Object Collections.Generic.List[object]
 foreach($item in Get-ChildItem -LiteralPath $taskOutput -Force){
  if(RetainedName $item.Name){continue}
  if($item.Name -eq 'client-viewing'){
   foreach($child in Get-ChildItem -LiteralPath $item.FullName -Force){if(!(RetainedName $child.Name)){$taskTargets.Add($child)}}
  }else{$taskTargets.Add($item)}
 }
 $targets=@();$evidence=@();$allFiles=@();$total=0L;$skipped=@()
 foreach($item in $taskTargets){
  Plain $item.FullName
  $relative=[IO.Path]::GetRelativePath($taskOutput,$item.FullName)
  if($relative -match '(?i)(^|\\)r(?:20|22)(?!\d)' -or $relative -match '(?i)客户交付资料_r(?:20|22)_'){throw ('保护版本被列入清理 '+$relative)}
  if($item.PSIsContainer -and @(Get-ChildItem -LiteralPath $item.FullName -Recurse -Force|Where-Object{$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){$skipped+=@{path=$item.FullName;reason='Contains reparse links; retained for safety'};continue}
  $files=FilesOf $item.FullName
  if(@($files|Where-Object{$_.Extension -in '.pem','.key','.pfx'}).Count){$skipped+=@{path=$item.FullName;reason='Contains signing-key file; retained'};continue}
  $sum=($files|Measure-Object Length -Sum).Sum;$total+=[long]$sum
  $targets+=@{path=$item.FullName;relative=$relative;bytes=[long]$sum;files=$files.Count}
  $package=$item.Name -match '客户交付|业务数据恢复工具'
  foreach($file in $files){
   $r=[IO.Path]::GetRelativePath($taskOutput,$file.FullName).Replace('\','/')
   $allFiles+=@{path=$r;bytes=$file.Length}
   if(Evidence $file $package){$evidence+=@{path=$file.FullName;entry=$r;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}}
  }
 }
 $plan=@{createdAtUtc=[DateTime]::UtcNow.ToString('o');scope=$taskOutput;targets=$targets;files=$allFiles;evidence=$evidence;deleteBytes=$total;evidenceBytes=($evidence|Measure-Object bytes -Sum).Sum;skipped=$skipped;protected='All r20 and r22 output roots; source, docs, customer inputs, private keys and supplier tools outside this delete scope'}
 $plan|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $taskPlanFile -Encoding UTF8
 Write-Output ('已保存清理计划：'+$targets.Count+'个目标，'+[math]::Round($total/1GB,2)+' GiB；留存验证/源码快照 '+$evidence.Count+'文件')
 return
}
$taskSeal=Get-Content -LiteralPath (Join-Path $taskOutput 'r22-delivery-qa_20261107/package.json') -Raw -Encoding UTF8|ConvertFrom-Json
if(!$taskSeal.passed){throw 'r22完整交付包未封装通过，禁止清理'}
if((Get-FileHash -LiteralPath $taskSeal.zip -Algorithm SHA256).Hash -ne $taskSeal.sha256){throw 'r22交付包哈希不符'}
$taskR20Zip=Join-Path $taskOutput '客户交付资料_r20_20261102/液位管理系统_r20_自动结果复位改进版_20261102.zip'
if((Get-FileHash -LiteralPath $taskR20Zip -Algorithm SHA256).Hash -ne '464521C5A6380873E1C06DBD50583F6025187395462465BCED1AE6FC200C3E70'){throw 'r20完整包不符'}
$taskPlan=Get-Content -LiteralPath $taskPlanFile -Raw -Encoding UTF8|ConvertFrom-Json
if($taskPlan.scope -ne $taskOutput){throw '清理根目录不符'}
$taskProcesses=@(Get-CimInstance Win32_Process)
foreach($target in $taskPlan.targets){
 Plain $target.path
 $leaf=Split-Path $target.path -Leaf;if(RetainedName $leaf){throw '保护目标命中'}
 if(@($taskProcesses|Where-Object {($_.ExecutablePath -and (Within $_.ExecutablePath $target.path)) -or ($_.CommandLine -and $_.CommandLine.Contains($target.path))}).Count){throw ('目标仍有进程引用 '+$target.path)}
}
# Build and validate the compact evidence archive before deleting any target.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskArchive=Join-Path $taskRecord '旧版验证记录与源码快照.zip'
if(Test-Path -LiteralPath $taskArchive){throw '验证归档已存在，不重复执行'}
$taskZip=[IO.Compression.ZipFile]::Open($taskArchive,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($file in $taskPlan.evidence){
 if((Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash -ne $file.sha256){throw ('验证文件已变化 '+$file.path)}
 [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip,$file.path,$file.entry,[IO.Compression.CompressionLevel]::Optimal)|Out-Null
}}finally{$taskZip.Dispose()}
$taskZip=[IO.Compression.ZipFile]::OpenRead($taskArchive)
try{
 if($taskZip.Entries.Count -ne $taskPlan.evidence.Count){throw '归档文件数不符'}
 foreach($file in $taskPlan.evidence){$entry=$taskZip.GetEntry($file.entry);$stream=$entry.Open();$hash=[Security.Cryptography.SHA256]::Create();try{$sha=[BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','');if($sha -ne $file.sha256){throw '归档内容校验失败'}}finally{$stream.Dispose();$hash.Dispose()}}
}finally{$taskZip.Dispose()}
$taskDeleted=@()
foreach($target in $taskPlan.targets){
 Plain $target.path
 $actual=@(FilesOf $target.path);if($actual.Count -ne $target.files -or [long](($actual|Measure-Object Length -Sum).Sum) -ne $target.bytes){throw ('目标清单已变化，停止 '+$target.path)}
 Remove-Item -LiteralPath $target.path -Recurse -Force
 if(Test-Path -LiteralPath $target.path){throw ('目标未清除 '+$target.path)}
 $taskDeleted+=$target
 @{atUtc=[DateTime]::UtcNow.ToString('o');path=$target.path;bytes=$target.bytes}|ConvertTo-Json -Compress|Add-Content -LiteralPath (Join-Path $taskRecord 'deleted.ndjson') -Encoding UTF8
}
$taskResult=@{passed=$true;deletedTargets=$taskDeleted.Count;deletedFiles=($taskDeleted|Measure-Object files -Sum).Sum;deletedBytes=($taskDeleted|Measure-Object bytes -Sum).Sum;evidenceFiles=$taskPlan.evidence.Count;evidenceArchive=$taskArchive;evidenceSha256=(Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash;r20Sha256=(Get-FileHash -LiteralPath $taskR20Zip -Algorithm SHA256).Hash;r22Sha256=(Get-FileHash -LiteralPath $taskSeal.zip -Algorithm SHA256).Hash;sourceAndKeysOutsideDeleteScope=$true;atUtc=[DateTime]::UtcNow.ToString('o')}
$taskResult|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $taskRecord 'result.json') -Encoding UTF8
Write-Output ($taskResult|ConvertTo-Json -Depth 3)
