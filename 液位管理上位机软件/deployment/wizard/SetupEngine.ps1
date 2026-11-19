#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Inspect','Validate','Prepare','Commit','Rollback','Uninstall')][string]$Action='Inspect',
    [string]$InstallRoot,
    [string]$DataRoot,
    [ValidateSet('Full','Client')][string]$Mode='Full',
    [string]$ServerUrl='http://127.0.0.1:8780/',
    [ValidateRange(1024,65535)][int]$Port=8780,
    [string]$ServiceName='Naide.LevelManager',
    [string]$ResultFile,
    [string]$StateFile,
    [string]$VersionDirectory='20261118-r24',
    [switch]$AllowLan,
    [switch]$Confirmed,
    [switch]$ReuseExistingData,
    [switch]$IgnoreInstalledService
)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Desktop'){$env:PSModulePath=Join-Path $PSHOME 'Modules'}
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
if($ServiceName -notmatch '^Naide\.LevelManager(?:\.QA\.[A-Za-z0-9]+)?$'){throw '服务名称无效'}
if($IgnoreInstalledService -and $Action -ne 'Inspect'){throw '仅预览允许忽略本机服务'}
function Write-Result([string]$Text){
    if($ResultFile){
        $content=if($Action -eq 'Inspect'){$Text}else{"[Result]`r`nMessage="+($Text -replace '[\r\n]+',' ') + "`r`n"}
        [IO.File]::WriteAllText($ResultFile,$content,[Text.Encoding]::Unicode)
    }else{Write-Output $Text}
}
function Full-Path([string]$Value){
    if(!$Value -or $Value -notmatch '^[A-Za-z]:[\\/]' -or $Value.IndexOf('"') -ge 0){throw '请选择本机磁盘上的绝对目录，例如 D:\LevelManagerData'}
    $full=[IO.Path]::GetFullPath($Value).TrimEnd('\')
    if($full.Length -le 3){throw '请选择磁盘下的文件夹，不能使用磁盘根目录'}
    $part=$full
    while($part){
        if(Test-Path -LiteralPath $part){$item=Get-Item -LiteralPath $part -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw '所选路径包含链接，请选择实际本机文件夹'};if(!$item.PSIsContainer){throw '所选路径中有同名文件，请选择文件夹'}}
        $parent=[IO.Directory]::GetParent($part);if(!$parent){break};$part=$parent.FullName
    }
    return $full
}
function Overlaps([string]$a,[string]$b){return $a.Equals($b,[StringComparison]::OrdinalIgnoreCase) -or $a.StartsWith($b+'\',[StringComparison]::OrdinalIgnoreCase) -or $b.StartsWith($a+'\',[StringComparison]::OrdinalIgnoreCase)}
function Assert-RegularFile([string]$Path){
    $file=Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){
        throw '原数据库或关联文件包含链接或同名目录，请核对业务数据位置。'
    }
    return $file
}
function Assert-BusinessDatabase([string]$Path){
    if(!('Naide.Setup.RetainedDatabase' -as [type])){
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Naide.Setup {
    public static class RetainedDatabase {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int mode, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);

        private static byte[] Utf8(string value) {
            return Encoding.UTF8.GetBytes(value + "\0");
        }

        private static string Scalar(IntPtr db, string sql) {
            IntPtr statement;
            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out statement, IntPtr.Zero) != 0)
                throw new InvalidOperationException("Invalid database schema.");
            try {
                int result = sqlite3_step(statement);
                if (result == 101) return null;
                if (result != 100) throw new InvalidOperationException("Database read failed.");
                return Marshal.PtrToStringAnsi(sqlite3_column_text(statement, 0));
            } finally {
                sqlite3_finalize(statement);
            }
        }

        public static int Check(string path, int maximumSchema) {
            IntPtr db;
            int result = sqlite3_open_v2(Utf8(path), out db, 1, IntPtr.Zero); // SQLITE_OPEN_READONLY
            try {
                if (result != 0) throw new InvalidOperationException("Cannot read the database copy.");
                Scalar(db, "PRAGMA trusted_schema=OFF");
                int version;
                if (!Int32.TryParse(Scalar(db, "PRAGMA user_version"), out version) || version < 1)
                    throw new InvalidOperationException("Unrecognized database version.");
                if (version > maximumSchema) return version;
                if (Scalar(db, "PRAGMA quick_check(1)") != "ok" ||
                    Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('configuration','configuration_versions','users','operation_events')") != "4" ||
                    Scalar(db, "SELECT COUNT(*) FROM configuration WHERE Id=1 AND length(Payload)>0") != "1" ||
                    (version >= 2 && Scalar(db, "SELECT Value FROM database_properties WHERE Key='profile'") != "operational"))
                    throw new InvalidOperationException("Invalid LevelManager business database.");
                return version;
            } finally {
                if (db != IntPtr.Zero) sqlite3_close(db);
            }
        }
    }
}
'@
    }
    try{$schema=[Naide.Setup.RetainedDatabase]::Check($Path,18)}
    catch{throw '原数据库无法通过读取和完整性检查，或不属于液位管理系统。请保留原数据并核对目录或备份。'}
    if($schema -gt 18){throw "原业务数据库结构版本为 $schema，高于此安装程序支持的版本 18。请使用对应版本重新安装。"}
}
function Backup-RetainedDatabase([string]$Root){
    $files=@(Assert-RegularFile (Join-Path $Root 'level-manager.db'))
    # A stopped database can still have committed changes in its WAL.
    foreach($suffix in @('-wal','-journal')){
        $path=Join-Path $Root ('level-manager.db'+$suffix)
        if(Test-Path -LiteralPath $path){$files+=Assert-RegularFile $path}
    }
    $size=($files | Measure-Object -Property Length -Sum).Sum
    $drive=New-Object IO.DriveInfo([IO.Path]::GetPathRoot($Root))
    if($drive.AvailableFreeSpace -lt ($size+1GB)){throw '业务数据盘空间不足，无法保留重装前副本。请释放空间后重试。'}
    $backupRoot=Full-Path (Join-Path $Root '重装备份')
    $destination=Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
    [IO.Directory]::CreateDirectory($destination)|Out-Null
    foreach($file in $files){[IO.File]::Copy($file.FullName,(Join-Path $destination $file.Name),$false)}
    Assert-BusinessDatabase (Join-Path $destination 'level-manager.db')
    return $destination
}
function Service-Info{
    if($IgnoreInstalledService){return $null}
    return Get-CimInstance Win32_Service -Filter ("Name='"+$ServiceName+"'")
}
function Service-Paths($service){
    if(!$service){return $null}
    $exe=[regex]::Match($service.PathName,'^"([^"]+)"')
    $data=[regex]::Match($service.PathName,'--data-root\s+"([^"]+)"')
    if(!$exe.Success -or !$data.Success){throw '发现原后台，但无法核实原安装及数据目录。请联系供方处理，避免使用空数据目录'}
    $exePath=$exe.Groups[1].Value;$index=$exePath.IndexOf('\versions\',[StringComparison]::OrdinalIgnoreCase)
    if($index -lt 0){throw '原后台不属于标准安装目录，请联系供方核实安装方式'}
    $portMatch=[regex]::Match($service.PathName,'--urls\s+"https?://[^:]+:(\d+)')
    return @{InstallRoot=$exePath.Substring(0,$index);DataRoot=$data.Groups[1].Value;Port=$(if($portMatch.Success){[int]$portMatch.Groups[1].Value}else{8780})}
}
function Require-Administrator{
    $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '请允许 Windows 管理员授权后继续安装'}
}
function Validate-Plan{
    $script:InstallRoot=Full-Path $InstallRoot
    if($VersionDirectory -notmatch '^[A-Za-z0-9.+-]+$'){throw '程序版本目录无效'}
    $service=Service-Info;$existing=Service-Paths $service
    $recordPath=Join-Path $InstallRoot 'installation.json'
    if($existing){
        if(!$InstallRoot.Equals((Full-Path $existing.InstallRoot),[StringComparison]::OrdinalIgnoreCase)){throw '升级请沿用原程序目录；更换目录需另外迁移'}
        if($Mode -ne 'Full'){throw '本机已有后台，请选择完整安装升级，不能切换成仅客户端'}
        if(!(Full-Path $DataRoot).Equals((Full-Path $existing.DataRoot),[StringComparison]::OrdinalIgnoreCase)){throw '升级请沿用原业务数据目录'}
        if($Port -ne $existing.Port){throw '升级请沿用原后台端口'}
    }
    if($Mode -eq 'Full'){
        $script:DataRoot=Full-Path $DataRoot
        $auth=Full-Path ($DataRoot+'.authorization')
        if((Overlaps $InstallRoot $DataRoot) -or (Overlaps $InstallRoot $auth)){throw '程序目录和业务数据目录必须相互独立，不能互相包含'}
        $drive=New-Object IO.DriveInfo([IO.Path]::GetPathRoot($DataRoot))
        if($drive.DriveType -ne [IO.DriveType]::Fixed){throw '业务数据请选择本机固定磁盘'}
        if($drive.AvailableFreeSpace -lt 1GB){throw '数据磁盘可用空间不足 1 GB，请清理或选择其他磁盘'}
        if(!$existing -and (Test-Path -LiteralPath (Join-Path $DataRoot 'level-manager.db'))){
            $null=Assert-RegularFile (Join-Path $DataRoot 'level-manager.db')
            if($Action -eq 'Prepare' -and !$ReuseExistingData){throw '沿用原业务数据前，请在安装向导中确认重装。'}
        }
        if(!$existing -and (Test-Path -LiteralPath $recordPath)){throw '发现已有安装记录，请核实原服务后再安装'}
        if(!$service -or $service.State -eq 'Stopped'){
            $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,$Port)
            try{$listener.Start()}catch{throw '后台端口已被占用，请退出占用程序或选择其他端口'}finally{$listener.Stop()}
        }
    }else{
        $uri=$null
        if(![Uri]::TryCreate($ServerUrl,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -notin @('http','https') -or $uri.AbsolutePath -ne '/' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment){throw '服务器地址请输入 http://服务器IP:端口/，例如 http://192.168.1.10:8780/'}
        if(Test-Path -LiteralPath $recordPath){$record=Get-Content -LiteralPath $recordPath -Raw -Encoding UTF8|ConvertFrom-Json;if($record.Mode -ne 'Client'){throw '原安装包含后台，请选择完整安装升级'}}
    }
    return $service
}
function Save-State($state){$state|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $StateFile -Encoding UTF8}
function Stop-Normally($service){
    if($service -and $service.State -ne 'Stopped'){
        Stop-Service -Name $ServiceName -ErrorAction Stop
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))
    }
}
function Protect-Data([string]$path){
    New-Item -ItemType Directory -Path $path -Force|Out-Null
    $acl=New-Object Security.AccessControl.DirectorySecurity;$acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){
        $rule=New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow');$acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $path -AclObject $acl
}
try{
    if($Action -eq 'Inspect'){
        $service=Service-Info;$old=Service-Paths $service
        $root=if($old){$old.InstallRoot}elseif($InstallRoot){Full-Path $InstallRoot}else{Join-Path $env:ProgramFiles 'Naide\LevelManager'}
        $recordFile=Join-Path $root 'installation.json';$record=$null
        if(Test-Path -LiteralPath $recordFile){$record=Get-Content -LiteralPath $recordFile -Raw -Encoding UTF8|ConvertFrom-Json}
        $data=if($old){$old.DataRoot}else{Join-Path $env:ProgramData 'Naide\LevelManager'}
        $oldMode=if($service){'Full'}elseif($record -and $record.Mode -eq 'Client'){'Client'}else{''}
        $portValue=if($old){$old.Port}else{8780}
        $version=if($record){[string]$record.Version}else{'尚未安装'}
        if($service -and $service.State -eq 'Running'){
            try{$health=Invoke-RestMethod ('http://127.0.0.1:'+$portValue+'/api/health') -TimeoutSec 2;if($health.product -eq '液位管理系统'){$version=[string]$health.revision+' / '+[string]$health.build}}catch{}
        }
        $url=if($oldMode -eq 'Client' -and $record.ServerUrl){[string]$record.ServerUrl}else{'http://127.0.0.1:'+$portValue+'/'}
        $lanEnabled=($service -and $service.PathName -match '--urls\s+"http://0\.0\.0\.0:')
        $text="[Existing]`r`nFound=$([int][bool]$oldMode)`r`nMode=$oldMode`r`nInstallRoot=$root`r`nDataRoot=$data`r`nPort=$portValue`r`nServerUrl=$url`r`nVersion=$version`r`nAllowLan=$([int][bool]$lanEnabled)`r`n"
        Write-Result $text;exit 0
    }
    if($Action -eq 'Validate'){$null=Validate-Plan;Write-Result '检查通过';exit 0}
    Require-Administrator
    if($Action -eq 'Prepare'){
        $service=Validate-Plan
        if(!$Confirmed){throw '请确认已备份业务数据并结束现场仪表作业'}
        if(!$StateFile){throw '缺少安装事务状态路径'}
        $previousConfig=Join-Path $InstallRoot 'installation.json'
        $state=@{ServiceName=$ServiceName;InstallRoot=$InstallRoot;Existing=[bool]$service;OldBinary=$(if($service){$service.PathName}else{''});OldStartMode=$(if($service){if($service.StartMode -eq 'Auto'){'Automatic'}else{$service.StartMode}}else{''});WasRunning=($service -and $service.State -eq 'Running');Configuration=$(if(Test-Path -LiteralPath $previousConfig){[IO.File]::ReadAllText($previousConfig)}else{$null});Committed=$false;Started=$false}
        if($service){
            Save-State $state
            Stop-Normally $service
        }
        $backupPath=$null
        if($Mode -eq 'Full'){
            $lockPath=Join-Path $DataRoot 'service.lock'
            $database=Join-Path $DataRoot 'level-manager.db'
            if(!$service -and $ReuseExistingData -and !(Test-Path -LiteralPath $database)){throw '原业务数据库已不在所选目录，请重新核对业务数据位置。'}
            if(!$service -and (Test-Path -LiteralPath $database)){
                if(!$ReuseExistingData){throw '沿用原业务数据前，请在安装向导中确认重装。'}
                if(Test-Path -LiteralPath $lockPath){$null=Assert-RegularFile $lockPath}
                try{$handle=[IO.File]::Open($lockPath,'OpenOrCreate','ReadWrite','None')}
                catch{throw '原业务目录仍被后台占用，或没有访问权限。请先停止使用该目录的后台，再重试。'}
                try{ $backupPath=Backup-RetainedDatabase $DataRoot }finally{ $handle.Dispose() }
            }elseif(Test-Path -LiteralPath $lockPath){
                $handle=[IO.File]::Open($lockPath,'Open','ReadWrite','None')
                $handle.Dispose()
            }
        }
        if(!$service){Save-State $state}
        if($backupPath){Write-Result ('原业务数据库检查通过，重装前副本已保存到 '+$backupPath)}
        else{Write-Result '预检通过'}
        exit 0
    }
    if($Action -eq 'Commit'){
        if(!$StateFile -or !(Test-Path -LiteralPath $StateFile)){throw '安装事务记录不存在'}
        $state=Get-Content -LiteralPath $StateFile -Raw -Encoding UTF8|ConvertFrom-Json
        $versionRoot=Join-Path (Join-Path $InstallRoot 'versions') $VersionDirectory
        $client=Join-Path $versionRoot 'app\client'
        if(!(Test-Path -LiteralPath (Join-Path $client 'LevelManager.Desktop.exe'))){throw '客户端文件不完整'}
        $url=if($Mode -eq 'Full'){'http://127.0.0.1:'+$Port+'/'}else{$ServerUrl}
        @{serverUrl=$url}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $client 'client.json') -Encoding UTF8
        if($Mode -eq 'Full'){
            Protect-Data $DataRoot;Protect-Data ($DataRoot+'.authorization')
            $exe=Join-Path $versionRoot 'app\server\LevelManager.Host.exe'
            if(!(Test-Path -LiteralPath $exe)){throw '后台程序文件不完整'}
            $listen=if($AllowLan){'http://0.0.0.0:'+$Port}else{$url.TrimEnd('/')}
            $binary='"'+$exe+'" --data-root "'+$DataRoot+'" --urls "'+$listen+'" --ServiceName "'+$ServiceName+'"'
            $service=Service-Info
            if($service){$changed=Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{PathName=$binary;StartMode='Automatic'};if($changed.ReturnValue -ne 0){throw '更新 Windows 后台服务失败'}}else{New-Service -Name $ServiceName -BinaryPathName $binary -DisplayName ('液位管理系统后台'+$(if($ServiceName -ne 'Naide.LevelManager'){' 测试'}else{''})) -StartupType Automatic|Out-Null}
            $state.Committed=$true;Save-State $state
            & sc.exe failure $ServiceName 'reset=' '86400' 'actions=' 'restart/5000/restart/30000/restart/60000'|Out-Null
            if($LASTEXITCODE -ne 0){throw '设置后台恢复策略失败'}
            if($AllowLan){
                $firewallName=$ServiceName+'.PrivateTcp'
                if(!(Get-NetFirewallRule -Name $firewallName -ErrorAction SilentlyContinue)){New-NetFirewallRule -Name $firewallName -DisplayName '液位管理系统后台（专用网络）' -Profile Private -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port|Out-Null}
            }
            $state.Started=$true;Save-State $state;Start-Service -Name $ServiceName
            $healthy=$false
            for($i=0;$i -lt 45;$i++){
                try{$health=Invoke-RestMethod ($url+'api/health') -TimeoutSec 2;if($health.product -eq '液位管理系统' -and $health.build -eq '20261118-r24'){$healthy=$true;break}}catch{}
                Start-Sleep -Seconds 1
            }
            if(!$healthy){throw '新后台未在预期时间内就绪，安装将恢复原服务设置。详情见安装日志'}
        }
        $plan=@{Product='Naide.LevelManager.2026';Version='0.3.0';Build='20261118-r24';InstallerRevision='20261118-wizard2';InstallRoot=$InstallRoot;VersionRoot=$versionRoot;Mode=$Mode;DataRoot=$(if($Mode -eq 'Full'){$DataRoot}else{$null});ServiceName=$(if($Mode -eq 'Full'){$ServiceName}else{$null});ServerUrl=$url;Port=$Port;AllowLan=[bool]$AllowLan;SchemaVersion=18}
        $plan|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $InstallRoot 'installation.json') -Encoding UTF8
        if($Mode -eq 'Full'){Write-Result '安装完成，后台连接检查通过'}else{Write-Result '客户端安装和地址配置完成，打开客户端后连接所选采集主机'}
        exit 0
    }
    if($Action -eq 'Rollback'){
        if(!$StateFile -or !(Test-Path -LiteralPath $StateFile)){Write-Result '没有需要恢复的服务设置';exit 0}
        $state=Get-Content -LiteralPath $StateFile -Raw -Encoding UTF8|ConvertFrom-Json
        if($state.ServiceName -ne $ServiceName -or !$state.InstallRoot.Equals($InstallRoot,[StringComparison]::OrdinalIgnoreCase)){throw '安装事务对象不匹配'}
        if($state.Committed){
            Stop-Normally (Service-Info)
            if($state.Existing){$service=Service-Info;$changed=Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{PathName=$state.OldBinary;StartMode=$state.OldStartMode};if($changed.ReturnValue -ne 0){throw '恢复原服务路径失败，请联系供方'}}else{& sc.exe delete $ServiceName|Out-Null;if($LASTEXITCODE -ne 0){throw '移除未完成安装的服务失败'}}
        }
        $record=Join-Path $InstallRoot 'installation.json'
        if($null -ne $state.Configuration){[IO.File]::WriteAllText($record,$state.Configuration,(New-Object Text.UTF8Encoding($true)))}elseif(Test-Path -LiteralPath $record){Remove-Item -LiteralPath $record}
        # A started new backend may have upgraded the DB. Keep both service and data stopped for review.
        if($state.WasRunning -and !$state.Started){Start-Service -Name $ServiceName}
        Write-Result '已恢复原服务设置。业务数据保留，请核对安装日志和数据库版本后启动';exit 0
    }
    if($Action -eq 'Uninstall'){
        $InstallRoot=Full-Path $InstallRoot
        if(!$Confirmed){throw '请确认已结束现场作业后卸载'}
        $record=Get-Content -LiteralPath (Join-Path $InstallRoot 'installation.json') -Raw -Encoding UTF8|ConvertFrom-Json
        if($record.Product -ne 'Naide.LevelManager.2026' -or !$record.InstallRoot.Equals($InstallRoot,[StringComparison]::OrdinalIgnoreCase)){throw '安装记录不匹配，已取消卸载'}
        $service=Service-Info
        if($record.Mode -eq 'Full' -and $service){
            $paths=Service-Paths $service
            if(!$paths.InstallRoot.Equals($InstallRoot,[StringComparison]::OrdinalIgnoreCase)){throw '后台已由其他安装管理，取消卸载'}
            Stop-Normally $service
            & sc.exe delete $ServiceName|Out-Null;if($LASTEXITCODE -ne 0){throw '删除服务注册未成功'}
            Get-NetFirewallRule -Name ($ServiceName+'.PrivateTcp') -ErrorAction SilentlyContinue|Remove-NetFirewallRule
        }
        Write-Result '后台服务已退出。业务数据、历史和授权均已保留';exit 0
    }
}catch{Write-Result $_.Exception.Message;Write-Error $_;exit 1}
