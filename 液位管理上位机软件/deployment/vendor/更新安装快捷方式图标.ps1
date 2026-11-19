#requires -Version 5.1
[CmdletBinding()]param([Parameter(Mandatory=$true)][string]$ClientExecutable,[string]$ServerUrl='http://127.0.0.1:8780/',[string]$DesktopRoot)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Desktop'){$env:PSModulePath=Join-Path $PSHOME 'Modules'}
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Drawing
$client=[IO.Path]::GetFullPath($ClientExecutable)
if(!(Test-Path -LiteralPath $client -PathType Leaf) -or [IO.Path]::GetFileName($client) -ne 'LevelManager.Desktop.exe'){throw '请选择已安装的LevelManager.Desktop.exe'}
$endpoint=New-Object Uri($ServerUrl)
if(!$endpoint.IsAbsoluteUri -or $endpoint.Scheme -notin @('http','https') -or $endpoint.AbsolutePath -ne '/' -or $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment){throw '后台地址无效'}
if(!$DesktopRoot){$identity=[Security.Principal.WindowsIdentity]::GetCurrent();$principal=New-Object Security.Principal.WindowsPrincipal($identity);if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '更新公共桌面图标时请使用管理员PowerShell'};$DesktopRoot=[Environment]::GetFolderPath('CommonDesktopDirectory')}
$desktop=[IO.Path]::GetFullPath($DesktopRoot);if(!(Test-Path -LiteralPath $desktop -PathType Container)){throw '桌面目录不存在'}
$presentation=Invoke-RestMethod -Uri (New-Object Uri($endpoint,'api/presentation')) -TimeoutSec 5
$value=$presentation.branding.iconDataUrl;$default=[string]::IsNullOrEmpty($value);$iconPath=Join-Path ([IO.Path]::GetDirectoryName($client)) 'presentation-icon.ico'
$memory=New-Object IO.MemoryStream
try{
 if($default){$icon=[Drawing.Icon]::ExtractAssociatedIcon($client);try{$icon.Save($memory)}finally{$icon.Dispose()}}
 else{
  $comma=$value.IndexOf(',');if($comma -lt 0 -or $value.Length -gt 1500000){throw '后台图标无效'};$mime=$value.Substring(0,$comma);$bytes=[Convert]::FromBase64String($value.Substring($comma+1));if($bytes.Length -gt 1048576){throw '后台图标超过1MB'}
  if($mime -eq 'data:image/x-icon;base64'){$memory.Write($bytes,0,$bytes.Length)}
  elseif($mime -in @('data:image/png;base64','data:image/jpeg;base64')){
   Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class LevelShortcutNative{[DllImport("user32.dll")]public static extern bool DestroyIcon(IntPtr h);}'
   $iconInput=New-Object IO.MemoryStream(,$bytes);$image=$null;$resized=$null;$borrowed=$null;$handle=[IntPtr]::Zero
   try{$image=[Drawing.Image]::FromStream($iconInput);if($image.Width -gt 1024 -or $image.Height -gt 1024){throw '程序图标尺寸超过1024'};$resized=New-Object Drawing.Bitmap($image,(New-Object Drawing.Size(64,64)));$handle=$resized.GetHicon();$borrowed=[Drawing.Icon]::FromHandle($handle);$borrowed.Save($memory)}
   finally{if($borrowed){$borrowed.Dispose()};if($handle -ne [IntPtr]::Zero){[void][LevelShortcutNative]::DestroyIcon($handle)};if($resized){$resized.Dispose()};if($image){$image.Dispose()};$iconInput.Dispose()}
  }else{throw '后台图标类型不支持'}
 }
 [IO.File]::WriteAllBytes($iconPath,$memory.ToArray())
}finally{$memory.Dispose()}
$shell=New-Object -ComObject WScript.Shell;$shortcutPath=Join-Path $desktop '液位管理系统.lnk'
if(Test-Path -LiteralPath $shortcutPath){$existing=$shell.CreateShortcut($shortcutPath);if(![string]::Equals([IO.Path]::GetFullPath($existing.TargetPath),$client,[StringComparison]::OrdinalIgnoreCase)){throw '同名快捷方式属于其他程序，未改动，请先核实'}}
$shortcut=$shell.CreateShortcut($shortcutPath);$shortcut.TargetPath=$client;$shortcut.WorkingDirectory=[IO.Path]::GetDirectoryName($client);$shortcut.IconLocation=$iconPath+',0';$shortcut.Save()
[PSCustomObject]@{Shortcut=$shortcutPath;Icon=$iconPath;DefaultIconUsed=$default;SoftwareName=$presentation.branding.softwareName}|ConvertTo-Json
