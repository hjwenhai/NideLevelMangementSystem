$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskDocs=Get-Content -LiteralPath (Join-Path $taskRoot 'output/r22-latest-delivery-qa_20261107/documents.json') -Raw -Encoding UTF8|ConvertFrom-Json
$taskWord=New-Object -ComObject kwps.Application
if($taskWord.Documents.Count -ne 0){throw '独立文档实例已含打开文档，不操作现有会话'}
Write-Output ('独立WPS文档数 '+$taskWord.Documents.Count);$taskWord.Visible=$false
try{
 for($taskIndex=0;$taskIndex -lt $taskDocs.Count;$taskIndex++){
  $taskName=if($taskIndex -eq 0){'upgrade'}else{'manual'}
  $taskOutput=Join-Path $taskRoot ('output/r22-latest-delivery-qa_20261107/documents/'+$taskName)
  New-Item -ItemType Directory -Path $taskOutput -Force|Out-Null
  $taskPdf=Join-Path $taskOutput ([IO.Path]::GetFileNameWithoutExtension($taskDocs[$taskIndex])+'.pdf')
  $taskDocument=$taskWord.Documents.Open($taskDocs[$taskIndex],$false,$true,$false)
  try{$taskDocument.ExportAsFixedFormat($taskPdf,17)}finally{$taskDocument.Close($false)}
  if(!(Test-Path -LiteralPath $taskPdf)){throw '未生成文档PDF'}
  Write-Output $taskPdf
 }
}finally{$taskWord.Quit()}
