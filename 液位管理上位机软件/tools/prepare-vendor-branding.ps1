#requires -Version 5.1
[CmdletBinding()]param([Parameter(Mandatory=$true)][string]$SoftwareName,[string]$ManufacturerName='', [string]$ManufacturerInformation='',[string]$LogoPath,[string]$IconPath,[Parameter(Mandatory=$true)][string]$OutputFile)
$ErrorActionPreference='Stop'
function Image-Data([string]$Path,[bool]$Icon){if([string]::IsNullOrWhiteSpace($Path)){return $null};$file=[IO.Path]::GetFullPath($Path);$bytes=[IO.File]::ReadAllBytes($file);if($bytes.Length -gt 1048576){throw '图像不能超过1MB'};$extension=[IO.Path]::GetExtension($file).ToLowerInvariant();$mime=switch($extension){'.png'{'image/png'} '.jpg'{'image/jpeg'} '.jpeg'{'image/jpeg'} '.ico'{if(!$Icon){throw '厂家Logo请使用PNG/JPEG'};'image/x-icon'} default{throw '请选择PNG/JPEG图像，程序图标还支持ICO'}};return 'data:'+$mime+';base64,'+[Convert]::ToBase64String($bytes)}
if([string]::IsNullOrWhiteSpace($SoftwareName)){throw '软件名称不能为空'}
$output=[IO.Path]::GetFullPath($OutputFile);if(Test-Path -LiteralPath $output){throw '输出已存在，请使用新的文件名'}
$brand=@{softwareName=$SoftwareName;manufacturerName=$ManufacturerName;manufacturerInformation=$ManufacturerInformation;logoDataUrl=(Image-Data $LogoPath $false);iconDataUrl=(Image-Data $IconPath $true)}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))|Out-Null
[IO.File]::WriteAllText($output,($brand|ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
Write-Output ('已生成品牌资料，需供方签名后才生效：'+$output)
