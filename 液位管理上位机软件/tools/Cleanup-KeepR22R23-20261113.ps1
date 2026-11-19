#requires -Version 5.1
#requires -RunAsAdministrator
param([switch]$Execute)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$taskRecord=Join-Path $taskRoot 'output\cleanup-records\20261113-r22-r23-only'
$taskPlanFile=Join-Path $taskRecord 'plan.json'
$taskPlanHash='F08FED234235DB6F779AD56909FA316524F0F8FD49313BDF33710714F2F5718D'
$taskResultFile=Join-Path $taskRecord 'result.json'
$taskJournal=Join-Path $taskRecord 'deletion-journal.jsonl'
$taskFilesDeleted=0L;$taskBytesDeleted=0L;$taskLinksDeleted=0
$taskDeleted=New-Object Collections.Generic.List[string]
$taskErrors=New-Object Collections.Generic.List[object]
function Write-TaskJson($path,$value){[IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))}
function Assert-TaskPath([string]$path){
 $p=[IO.Path]::GetFullPath($path)
 if(!$p.StartsWith($taskRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw ('目标超出工作目录：'+$path)}
 if($p -match '(?i)\\(\.git|\.agents|\.codex)(\\|$)'){throw '禁止清理版本控制或代理配置'}
 return $p
}
function Remove-TaskTree([string]$path){
 $p=Assert-TaskPath $path
 $item=Get-Item -LiteralPath $p -Force -ErrorAction Stop
 if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){
  # Delete only the link itself, never recursively traverse its target.
  if($item.PSIsContainer){[IO.Directory]::Delete($p,$false)}else{[IO.File]::Delete($p)}
  $script:taskLinksDeleted++;return
 }
 if($item.PSIsContainer){
  foreach($child in @(Get-ChildItem -LiteralPath $p -Force -ErrorAction Stop)){Remove-TaskTree $child.FullName}
  Remove-Item -LiteralPath $p -Force -ErrorAction Stop
 }else{
  $n=$item.Length
  Remove-Item -LiteralPath $p -Force -ErrorAction Stop
  $script:taskFilesDeleted++;$script:taskBytesDeleted+=$n
 }
}
try{
 if(Test-Path -LiteralPath $taskResultFile){throw '本次清理已有完成记录，禁止重复执行。'}
 if((Get-FileHash -LiteralPath $taskPlanFile -Algorithm SHA256).Hash -ne $taskPlanHash){throw '清理清单校验不符'}
 $plan=Get-Content -LiteralPath $taskPlanFile -Raw -Encoding UTF8|ConvertFrom-Json
 if($plan.root -ne $taskRoot){throw '清理清单工作目录不符'}
 if((Get-FileHash -LiteralPath $plan.archive.path -Algorithm SHA256).Hash -ne $plan.archive.sha256){throw '验证记录归档校验失败'}
 foreach($target in $plan.targets){
  $p=Assert-TaskPath $target.path
  if($p -ne [IO.Path]::GetFullPath((Join-Path $taskRoot $target.relative))){throw '绝对与相对目标不符'}
  foreach($k in $plan.keep){
   $q=[IO.Path]::GetFullPath((Join-Path $taskRoot $k))
   if($p -eq $q -or $q.StartsWith($p+'\',[StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($q+'\',[StringComparison]::OrdinalIgnoreCase)){throw ('目标涉及保留目录：'+$p)}
  }
  $ancestor=[IO.Directory]::GetParent($p)
  while($ancestor -and $ancestor.FullName.StartsWith($taskRoot+'\',[StringComparison]::OrdinalIgnoreCase)){
   if(($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw ('目标父目录包含链接：'+$ancestor.FullName)}
   $ancestor=$ancestor.Parent
  }
 }
 foreach($p in @(Get-CimInstance Win32_Process)){
  if($p.ProcessId -eq $PID){continue}
  foreach($t in $plan.targets){
   if($p.ExecutablePath -and ($p.ExecutablePath -eq $t.path -or $p.ExecutablePath.StartsWith($t.path+'\',[StringComparison]::OrdinalIgnoreCase))){throw ('目标仍有运行程序：'+$p.ProcessId+' '+$t.path)}
  }
 }
 foreach($svc in @(Get-CimInstance Win32_Service|Where-Object {$_.State -eq 'Running'})){
  foreach($t in $plan.targets){if($svc.PathName -and $svc.PathName.IndexOf($t.path,[StringComparison]::OrdinalIgnoreCase) -ge 0){throw ('目标仍有运行服务：'+$svc.Name)}}
 }
 foreach($f in $plan.protected){
  $p=Assert-TaskPath (Join-Path $taskRoot $f.path)
  if((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne $f.sha256){throw ('保留文件清理前变化：'+$f.path)}
 }
 if(!$Execute){Write-Output '目标边界、保留清单与归档校验通过。未删除。';exit 0}
 Write-TaskJson (Join-Path $taskRecord 'running.json') @{pid=$PID;stage='Deleting';startedAtUtc=[DateTime]::UtcNow.ToString('o');targets=$plan.targets.Count}
 foreach($target in $plan.targets){
  try{
   if(Test-Path -LiteralPath $target.path){Remove-TaskTree $target.path}
   $taskDeleted.Add($target.relative)
   [IO.File]::AppendAllText($taskJournal,(@{path=$target.relative;removed=$true;atUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Compress)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
  }catch{
   $taskErrors.Add(@{path=$target.relative;error=$_.Exception.Message})
   [IO.File]::AppendAllText($taskJournal,(@{path=$target.relative;removed=$false;error=$_.Exception.Message}|ConvertTo-Json -Compress)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
  }
 }
 # Restore compact current r23 verification records, without any disposable databases or QA installer.
 $from=Assert-TaskPath (Join-Path $taskRoot 'output\r23-feedback_20261113\qa-kept-records')
 $to=Assert-TaskPath (Join-Path $taskRoot 'output\r23-feedback_20261113\qa')
 if(!(Test-Path -LiteralPath $to)){Move-Item -LiteralPath $from -Destination $to -ErrorAction Stop}
 $mismatches=New-Object Collections.Generic.List[string]
 foreach($f in $plan.protected){
  $p=Assert-TaskPath (Join-Path $taskRoot $f.path)
  if(!(Test-Path -LiteralPath $p) -or (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne $f.sha256){$mismatches.Add($f.path)}
 }
 $result=@{passed=($taskErrors.Count -eq 0 -and $mismatches.Count -eq 0);deletedTargets=$taskDeleted;deletedFiles=$taskFilesDeleted;deletedBytes=$taskBytesDeleted;deletedLinks=$taskLinksDeleted;errors=$taskErrors;protectedFileMismatches=$mismatches;protectedFilesChecked=$plan.protected.Count;evidenceArchive=$plan.archive.path;evidenceSha256=$plan.archive.sha256;finishedAtUtc=[DateTime]::UtcNow.ToString('o');r22DataPreserved=$true;vendorKeysPreserved=$true;sourcePreserved=$true}
 Write-TaskJson $taskResultFile $result
 Write-Output ($result|ConvertTo-Json -Depth 5)
 if(!$result.passed){exit 1}
}catch{
 Write-TaskJson (Join-Path $taskRecord 'fatal-error.json') @{error=$_.Exception.Message;atUtc=[DateTime]::UtcNow.ToString('o');deletedFiles=$taskFilesDeleted;deletedBytes=$taskBytesDeleted}
 throw
}
