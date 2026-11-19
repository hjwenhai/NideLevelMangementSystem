; *** Inno Setup version 6.4.0+ English messages ***
;
; To download user-contributed translations of this file, go to:
;   https://jrsoftware.org/files/istrans/
;
; Note: When translating this text, do not add periods (.) to the end of
; messages that didn't have them already, because on those messages Inno
; Setup adds the periods automatically (appending a period would result in
; two periods being displayed).

[LangOptions]
; The following three entries are very important. Be sure to read and 
; understand the '[LangOptions] section' topic in the help file.
LanguageName=简体中文
LanguageID=$0804
LanguageCodePage=936
; If the language you are translating to requires special font faces or
; sizes, uncomment any of the following entries and change them accordingly.
DialogFontName=Microsoft YaHei
DialogFontSize=10
WelcomeFontName=Microsoft YaHei
;WelcomeFontSize=12
;TitleFontName=Arial
;TitleFontSize=29
;CopyrightFontName=Arial
;CopyrightFontSize=8

[Messages]

; *** Application titles
SetupAppTitle=安装
SetupWindowTitle=%1 安装向导
UninstallAppTitle=卸载
UninstallAppFullTitle=卸载 %1

; *** Misc. common
InformationTitle=提示
ConfirmTitle=请确认
ErrorTitle=错误

; *** SetupLdr messages
SetupLdrStartupMessage=This will install %1. Do you wish to continue?
LdrCannotCreateTemp=Unable to create a temporary file. Setup aborted
LdrCannotExecTemp=Unable to execute file in the temporary directory. Setup aborted
HelpTextNote=

; *** Startup error messages
LastErrorMessage=%1.%n%nError %2: %3
SetupFileMissing=The file %1 is missing from the installation directory. Please correct the problem or obtain a new copy of the program.
SetupFileCorrupt=安装文件校验失败，请重新获取安装程序。
SetupFileCorruptOrWrongVer=安装文件损坏或版本不兼容，请重新获取安装程序。
InvalidParameter=An invalid parameter was passed on the command line:%n%n%1
SetupAlreadyRunning=安装向导已经运行。
WindowsVersionNotSupported=本程序支持 Windows 10 或 Windows 11 64 位系统。
WindowsServicePackRequired=This program requires %1 Service Pack %2 or later.
NotOnThisPlatform=This program will not run on %1.
OnlyOnThisPlatform=This program must be run on %1.
OnlyOnTheseArchitectures=This program can only be installed on versions of Windows designed for the following processor architectures:%n%n%1
WinVersionTooLowError=This program requires %1 version %2 or later.
WinVersionTooHighError=This program cannot be installed on %1 version %2 or later.
AdminPrivilegesRequired=安装需要管理员权限，请允许 Windows 授权后继续。
PowerUserPrivilegesRequired=You must be logged in as an administrator or as a member of the Power Users group when installing this program.
SetupAppRunningError=Setup has detected that %1 is currently running.%n%nPlease close all instances of it now, then click OK to continue, or Cancel to exit.
UninstallAppRunningError=Uninstall has detected that %1 is currently running.%n%nPlease close all instances of it now, then click OK to continue, or Cancel to exit.

; *** Startup questions
PrivilegesRequiredOverrideTitle=Select Setup Install Mode
PrivilegesRequiredOverrideInstruction=Select install mode
PrivilegesRequiredOverrideText1=%1 can be installed for all users (requires administrative privileges), or for you only.
PrivilegesRequiredOverrideText2=%1 can be installed for you only, or for all users (requires administrative privileges).
PrivilegesRequiredOverrideAllUsers=Install for &all users
PrivilegesRequiredOverrideAllUsersRecommended=Install for &all users (recommended)
PrivilegesRequiredOverrideCurrentUser=Install for &me only
PrivilegesRequiredOverrideCurrentUserRecommended=Install for &me only (recommended)

; *** Misc. errors
ErrorCreatingDir=Setup was unable to create the directory "%1"
ErrorTooManyFilesInDir=Unable to create a file in the directory "%1" because it contains too many files

; *** Setup common messages
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。是否退出安装向导？
AboutSetupMenuItem=关于安装程序
AboutSetupTitle=关于安装程序
AboutSetupMessage=%1 version %2%n%3%n%n%1 home page:%n%4
AboutSetupNote=
TranslatorNote=

; *** Buttons
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonYesToAll=全部是
ButtonNo=否(&N)
ButtonNoToAll=全部否
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)…
ButtonWizardBrowse=浏览(&B)…
ButtonNewFolder=新建文件夹

; *** "Select Language" dialog messages
SelectLanguageTitle=选择安装语言
SelectLanguageLabel=Select the language to use during the installation.

; *** Common wizard text
ClickNext=点击“下一步”继续，或点击“取消”退出。
BeveledLabel=
BrowseDialogTitle=选择文件夹
BrowseDialogLabel=选择文件夹后点击“确定”。
NewFolderName=新建文件夹

; *** "Welcome" wizard page
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=本向导将安装 [name/ver]。

; *** "Password" wizard page
WizardPassword=Password
PasswordLabel1=This installation is password protected.
PasswordLabel3=Please provide the password, then click Next to continue. Passwords are case-sensitive.
PasswordEditLabel=&Password:
IncorrectPassword=The password you entered is not correct. Please try again.

; *** "License Agreement" wizard page
WizardLicense=License Agreement
LicenseLabel=Please read the following important information before continuing.
LicenseLabel3=Please read the following License Agreement. You must accept the terms of this agreement before continuing with the installation.
LicenseAccepted=I &accept the agreement
LicenseNotAccepted=I &do not accept the agreement

; *** "Information" wizard pages
WizardInfoBefore=Information
InfoBeforeLabel=Please read the following important information before continuing.
InfoBeforeClickLabel=When you are ready to continue with Setup, click Next.
WizardInfoAfter=Information
InfoAfterLabel=Please read the following important information before continuing.
InfoAfterClickLabel=When you are ready to continue with Setup, click Next.

; *** "User Information" wizard page
WizardUserInfo=User Information
UserInfoDesc=Please enter your information.
UserInfoName=&User Name:
UserInfoOrg=&Organization:
UserInfoSerial=&Serial Number:
UserInfoNameRequired=You must enter a name.

; *** "Select Destination Location" wizard page
WizardSelectDir=选择程序安装位置
SelectDirDesc=将 [name] 安装到哪个文件夹
SelectDirLabel3=程序文件将安装到以下目录。业务数据位置将在下一步设置。
SelectDirBrowseLabel=点击“下一步”继续，或点击“浏览”选择其他位置。
DiskSpaceGBLabel=程序安装至少需要 [gb] GB 可用空间。
DiskSpaceMBLabel=程序安装至少需要 [mb] MB 可用空间。
CannotInstallToNetworkDrive=程序请选择本机磁盘目录。
CannotInstallToUNCPath=程序请选择本机磁盘目录。
InvalidPath=请输入带盘符的完整路径，例如 D:\LevelManager。
InvalidDrive=所选磁盘不存在或不可访问。
DiskSpaceWarningTitle=磁盘空间不足
DiskSpaceWarning=所选磁盘可用空间不足。请返回选择其他位置。是否仍要继续？
DirNameTooLong=目录路径过长，请选择更短的路径。
InvalidDirName=目录名称无效。
BadDirName32=Folder names cannot include any of the following characters:%n%n%1
DirExistsTitle=目录已经存在
DirExists=目录 %1 已经存在，是否继续使用？
DirDoesntExistTitle=创建程序目录
DirDoesntExist=目录 %1 不存在。是否创建？

; *** "Select Components" wizard page
WizardSelectComponents=Select Components
SelectComponentsDesc=Which components should be installed?
SelectComponentsLabel2=Select the components you want to install; clear the components you do not want to install. Click Next when you are ready to continue.
FullInstallation=Full installation
; if possible don't translate 'Compact' as 'Minimal' (I mean 'Minimal' in your language)
CompactInstallation=Compact installation
CustomInstallation=Custom installation
NoUninstallWarningTitle=Components Exist
NoUninstallWarning=Setup has detected that the following components are already installed on your computer:%n%n%1%n%nDeselecting these components will not uninstall them.%n%nWould you like to continue anyway?
ComponentSize1=%1 KB
ComponentSize2=%1 MB
ComponentsDiskSpaceGBLabel=Current selection requires at least [gb] GB of disk space.
ComponentsDiskSpaceMBLabel=Current selection requires at least [mb] MB of disk space.

; *** "Select Additional Tasks" wizard page
WizardSelectTasks=设置快捷方式
SelectTasksDesc=选择要创建的快捷方式
SelectTasksLabel2=选择所需项目后点击“下一步”。

; *** "Select Start Menu Folder" wizard page
WizardSelectProgramGroup=Select Start Menu Folder
SelectStartMenuFolderDesc=Where should Setup place the program's shortcuts?
SelectStartMenuFolderLabel3=Setup will create the program's shortcuts in the following Start Menu folder.
SelectStartMenuFolderBrowseLabel=To continue, click Next. If you would like to select a different folder, click Browse.
MustEnterGroupName=You must enter a folder name.
GroupNameTooLong=The folder name or path is too long.
InvalidGroupName=The folder name is not valid.
BadGroupName=The folder name cannot include any of the following characters:%n%n%1
NoProgramGroupCheck2=&Don't create a Start Menu folder

; *** "Ready to Install" wizard page
WizardReady=准备安装
ReadyLabel1=已准备好安装 [name]。
ReadyLabel2a=核对下列设置，点击“安装”开始。需要调整时点击“上一步”。
ReadyLabel2b=点击“安装”开始。
ReadyMemoUserInfo=User information:
ReadyMemoDir=Destination location:
ReadyMemoType=Setup type:
ReadyMemoComponents=Selected components:
ReadyMemoGroup=Start Menu folder:
ReadyMemoTasks=快捷方式

; *** TDownloadWizardPage wizard page and DownloadTemporaryFile
DownloadingLabel=Downloading additional files...
ButtonStopDownload=&Stop download
StopDownload=Are you sure you want to stop the download?
ErrorDownloadAborted=Download aborted
ErrorDownloadFailed=Download failed: %1 %2
ErrorDownloadSizeFailed=Getting size failed: %1 %2
ErrorFileHash1=File hash failed: %1
ErrorFileHash2=Invalid file hash: expected %1, found %2
ErrorProgress=Invalid progress: %1 of %2
ErrorFileSize=Invalid file size: expected %1, found %2

; *** TExtractionWizardPage wizard page and Extract7ZipArchive
ExtractionLabel=Extracting additional files...
ButtonStopExtraction=&Stop extraction
StopExtraction=Are you sure you want to stop the extraction?
ErrorExtractionAborted=Extraction aborted
ErrorExtractionFailed=Extraction failed: %1

; *** "Preparing to Install" wizard page
WizardPreparing=安装前检查
PreparingDesc=正在检查安装条件。
PreviousInstallNotCompleted=The installation/removal of a previous program was not completed. You will need to restart your computer to complete that installation.%n%nAfter restarting your computer, run Setup again to complete the installation of [name].
CannotContinue=暂时无法继续，请根据提示处理后重试。
ApplicationsFound=The following applications are using files that need to be updated by Setup. It is recommended that you allow Setup to automatically close these applications.
ApplicationsFound2=The following applications are using files that need to be updated by Setup. It is recommended that you allow Setup to automatically close these applications. After the installation has completed, Setup will attempt to restart the applications.
CloseApplications=&Automatically close the applications
DontCloseApplications=&Do not close the applications
ErrorCloseApplications=Setup was unable to automatically close all applications. It is recommended that you close all applications using files that need to be updated by Setup before continuing.
PrepareToInstallNeedsRestart=Setup must restart your computer. After restarting your computer, run Setup again to complete the installation of [name].%n%nWould you like to restart now?

; *** "Installing" wizard page
WizardInstalling=正在安装
InstallingLabel=正在安装 [name]，请稍候。

; *** "Setup Completed" wizard page
FinishedHeadingLabel=[name] 安装完成
FinishedLabelNoIcons=[name] 已安装完成。
FinishedLabel=[name] 已安装完成，可从桌面或开始菜单打开。
ClickFinish=点击“完成”退出安装向导。
FinishedRestartLabel=Windows 运行环境需要重启后生效，请保存工作后选择重启时间。
FinishedRestartMessage=运行环境需要重启。是否现在重启电脑？
ShowReadmeCheck=Yes, I would like to view the README file
YesRadio=现在重启
NoRadio=稍后自行重启
; used for example as 'Run MyProg.exe'
RunEntryExec=Run %1
; used for example as 'View Readme.txt'
RunEntryShellExec=View %1

; *** "Setup Needs the Next Disk" stuff
ChangeDiskTitle=Setup Needs the Next Disk
SelectDiskLabel2=Please insert Disk %1 and click OK.%n%nIf the files on this disk can be found in a folder other than the one displayed below, enter the correct path or click Browse.
PathLabel=&Path:
FileNotInDir2=The file "%1" could not be located in "%2". Please insert the correct disk or select another folder.
SelectDirectoryLabel=Please specify the location of the next disk.

; *** Installation phase messages
SetupAborted=安装未完成，请保留日志并按提示处理后重试。
AbortRetryIgnoreSelectAction=Select action
AbortRetryIgnoreRetry=重试
AbortRetryIgnoreIgnore=忽略
AbortRetryIgnoreCancel=取消安装

; *** Installation status messages
StatusClosingApplications=Closing applications...
StatusCreateDirs=正在创建程序目录…
StatusExtractFiles=正在安装程序文件…
StatusCreateIcons=正在创建快捷方式…
StatusCreateIniEntries=Creating INI entries...
StatusCreateRegistryEntries=正在注册程序信息…
StatusRegisterFiles=Registering files...
StatusSavingUninstall=正在保存卸载信息…
StatusRunProgram=正在完成安装…
StatusRestartingApplications=Restarting applications...
StatusRollback=正在回退安装文件…

; *** Misc. errors
ErrorInternal2=Internal error: %1
ErrorFunctionFailedNoCode=%1 failed
ErrorFunctionFailed=%1 failed; code %2
ErrorFunctionFailedWithMessage=%1 failed; code %2.%n%3
ErrorExecutingProgram=Unable to execute file:%n%1

; *** Registry errors
ErrorRegOpenKey=Error opening registry key:%n%1\%2
ErrorRegCreateKey=Error creating registry key:%n%1\%2
ErrorRegWriteKey=Error writing to registry key:%n%1\%2

; *** INI errors
ErrorIniEntry=Error creating INI entry in file "%1".

; *** File copying errors
FileAbortRetryIgnoreSkipNotRecommended=&Skip this file (not recommended)
FileAbortRetryIgnoreIgnoreNotRecommended=&Ignore the error and continue (not recommended)
SourceIsCorrupted=The source file is corrupted
SourceDoesntExist=The source file "%1" does not exist
ExistingFileReadOnly2=The existing file could not be replaced because it is marked read-only.
ExistingFileReadOnlyRetry=&Remove the read-only attribute and try again
ExistingFileReadOnlyKeepExisting=&Keep the existing file
ErrorReadingExistingDest=An error occurred while trying to read the existing file:
FileExistsSelectAction=Select action
FileExists2=The file already exists.
FileExistsOverwriteExisting=&Overwrite the existing file
FileExistsKeepExisting=&Keep the existing file
FileExistsOverwriteOrKeepAll=&Do this for the next conflicts
ExistingFileNewerSelectAction=Select action
ExistingFileNewer2=The existing file is newer than the one Setup is trying to install.
ExistingFileNewerOverwriteExisting=&Overwrite the existing file
ExistingFileNewerKeepExisting=&Keep the existing file (recommended)
ExistingFileNewerOverwriteOrKeepAll=&Do this for the next conflicts
ErrorChangingAttr=An error occurred while trying to change the attributes of the existing file:
ErrorCreatingTemp=An error occurred while trying to create a file in the destination directory:
ErrorReadingSource=An error occurred while trying to read the source file:
ErrorCopying=An error occurred while trying to copy a file:
ErrorReplacingExistingFile=An error occurred while trying to replace the existing file:
ErrorRestartReplace=RestartReplace failed:
ErrorRenamingTemp=An error occurred while trying to rename a file in the destination directory:
ErrorRegisterServer=Unable to register the DLL/OCX: %1
ErrorRegSvr32Failed=RegSvr32 failed with exit code %1
ErrorRegisterTypeLib=Unable to register the type library: %1

; *** Uninstall display name markings
; used for example as 'My Program (32-bit)'
UninstallDisplayNameMark=%1 (%2)
; used for example as 'My Program (32-bit, All users)'
UninstallDisplayNameMarks=%1 (%2, %3)
UninstallDisplayNameMark32Bit=32-bit
UninstallDisplayNameMark64Bit=64-bit
UninstallDisplayNameMarkAllUsers=All users
UninstallDisplayNameMarkCurrentUser=Current user

; *** Post-installation errors
ErrorOpeningReadme=An error occurred while trying to open the README file.
ErrorRestartingComputer=Setup was unable to restart the computer. Please do this manually.

; *** Uninstaller messages
UninstallNotFound=找不到卸载记录。
UninstallOpenError=File "%1" could not be opened. Cannot uninstall
UninstallUnsupportedVer=The uninstall log file "%1" is in a format not recognized by this version of the uninstaller. Cannot uninstall
UninstallUnknownEntry=An unknown entry (%1) was encountered in the uninstall log
ConfirmUninstall=是否卸载 %1？业务数据及授权将保留。
UninstallOnlyOnWin64=This installation can only be uninstalled on 64-bit Windows.
OnlyAdminCanUninstall=This installation can only be uninstalled by a user with administrative privileges.
UninstallStatusLabel=正在卸载 %1，请稍候。
UninstalledAll=%1 已卸载。业务数据与授权已保留。
UninstalledMost=%1 的程序文件已移除，部分在用文件或业务数据已保留。
UninstalledAndNeedsRestart=To complete the uninstallation of %1, your computer must be restarted.%n%nWould you like to restart now?
UninstallDataCorrupted=卸载记录损坏，请联系供方处理。

; *** Uninstallation phase messages
ConfirmDeleteSharedFileTitle=Remove Shared File?
ConfirmDeleteSharedFile2=The system indicates that the following shared file is no longer in use by any programs. Would you like for Uninstall to remove this shared file?%n%nIf any programs are still using this file and it is removed, those programs may not function properly. If you are unsure, choose No. Leaving the file on your system will not cause any harm.
SharedFileNameLabel=File name:
SharedFileLocationLabel=Location:
WizardUninstalling=Uninstall Status
StatusUninstalling=Uninstalling %1...

; *** Shutdown block reasons
ShutdownBlockReasonInstallingApp=Installing %1.
ShutdownBlockReasonUninstallingApp=Uninstalling %1.

; The custom messages below aren't used by Setup itself, but if you make
; use of them in your scripts, you'll want to translate them.

[CustomMessages]

NameAndVersion=%1 version %2
AdditionalIcons=Additional shortcuts:
CreateDesktopIcon=Create a &desktop shortcut
CreateQuickLaunchIcon=Create a &Quick Launch shortcut
ProgramOnTheWeb=%1 on the Web
UninstallProgram=Uninstall %1
LaunchProgram=Launch %1
AssocFileExtension=&Associate %1 with the %2 file extension
AssocingFileExtension=Associating %1 with the %2 file extension...
AutoStartProgramGroupDescription=Startup:
AutoStartProgram=Automatically start %1
AddonHostProgramNotFound=%1 could not be located in the folder you selected.%n%nDo you want to continue anyway?