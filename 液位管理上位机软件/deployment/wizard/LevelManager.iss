#ifndef PayloadRoot
  #error PayloadRoot must point to a verified r24 customer package
#endif
#ifndef OutputRoot
  #define OutputRoot "..\..\output\客户安装向导_r24_20261118"
#endif
#ifndef VersionDirectory
  #define VersionDirectory "20261118-r24"
#endif
#define RuntimeDir "versions\" + VersionDirectory
#ifndef ReleaseStamp
  #define ReleaseStamp "20261118"
#endif
#ifdef SetupQa
  #define InstallerAppId "{{575AD2FD-D52A-4168-B9DA-F8A0BE01A1A6}"
  #define InstallerService "Naide.LevelManager.QA.WizardPackage"
  #define ShortcutGroup "液位管理系统安装验证"
#else
  #define InstallerAppId "{{4C1D9AE9-6451-4AFE-9D64-67CE89D1A205}"
  #define InstallerService "Naide.LevelManager"
  #define ShortcutGroup "液位管理系统"
#endif

[Setup]
AppId={#InstallerAppId}
AppName=液位管理系统
AppVersion=0.3.0.24
AppVerName=液位管理系统 r24
AppPublisher=耐德东京计装
VersionInfoDescription=液位管理系统安装程序
VersionInfoVersion=0.3.0.24
DefaultDirName={autopf}\Naide\LevelManager
DefaultGroupName=液位管理系统
DisableProgramGroupPage=yes
DisableDirPage=no
DisableWelcomePage=no
#ifdef PreviewOnly
PrivilegesRequired=lowest
#else
PrivilegesRequired=admin
#endif
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.10240
WizardStyle=modern
WizardSizePercent=115,115
WizardResizable=yes
SetupIconFile=..\..\assets\level.ico
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
OutputDir={#OutputRoot}
#ifdef PreviewOnly
OutputBaseFilename=安装向导画面核对_不执行安装
#else
OutputBaseFilename=液位管理系统_r24_安装程序_{#ReleaseStamp}
#endif
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
UninstallDisplayIcon={app}\{#RuntimeDir}\app\client\LevelManager.Desktop.exe
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "zhcn"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式"

[Files]
Source: "SetupEngine.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "SetupEngine.ps1"; Flags: dontcopy
Source: "{#PayloadRoot}\app\client\*"; DestDir: "{app}\{#RuntimeDir}\app\client"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "client.json"
Source: "{#PayloadRoot}\app\server\*"; DestDir: "{app}\{#RuntimeDir}\app\server"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsFull
Source: "{#PayloadRoot}\app\maintenance\*"; DestDir: "{app}\{#RuntimeDir}\app\maintenance"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsFull
Source: "{#PayloadRoot}\app\restore\*"; DestDir: "{app}\{#RuntimeDir}\app\restore"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsFull
Source: "{#PayloadRoot}\docs\*"; DestDir: "{app}\{#RuntimeDir}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "客户安装升级指南_*.docx,客户安装升级指南_*.html,r*更新说明.txt,r*修订与升级说明.html"
Source: "{#PayloadRoot}\prerequisites\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Flags: dontcopy
Source: "{#PayloadRoot}\third-party\*"; DestDir: "{app}\third-party"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist

[Icons]
Name: "{commondesktop}\{#ShortcutGroup}"; Filename: "{app}\{#RuntimeDir}\app\client\LevelManager.Desktop.exe"; WorkingDir: "{app}\{#RuntimeDir}\app\client"; Tasks: desktopicon
Name: "{commonprograms}\{#ShortcutGroup}\液位管理系统"; Filename: "{app}\{#RuntimeDir}\app\client\LevelManager.Desktop.exe"; WorkingDir: "{app}\{#RuntimeDir}\app\client"
Name: "{commonprograms}\{#ShortcutGroup}\业务数据恢复工具"; Filename: "{app}\{#RuntimeDir}\app\restore\LevelManager.RestoreDesk.exe"; Parameters: "--data-root ""{code:DataDirectory}"""; Check: IsFull
Name: "{commonprograms}\{#ShortcutGroup}\卸载液位管理系统"; Filename: "{uninstallexe}"

[InstallDelete]
Type: files; Name: "{app}\安装向导使用说明.html"
Type: files; Name: "{app}\{#RuntimeDir}\docs\r24修订与升级说明.html"
Type: files; Name: "{commonprograms}\{#ShortcutGroup}\安装与升级说明.lnk"

[Run]
Filename: "{app}\{#RuntimeDir}\app\client\LevelManager.Desktop.exe"; Description: "立即打开液位管理系统"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallDelete]
Type: files; Name: "{app}\installation.json"
Type: files; Name: "{app}\{#RuntimeDir}\app\client\client.json"

[Code]
var
  ModePage: TInputOptionWizardPage;
  DataPage: TInputDirWizardPage;
  ConnectionPage: TInputQueryWizardPage;
  ConfirmPage: TInputOptionWizardPage;
  PortEdit: TNewEdit;
  LanBox: TNewCheckBox;
  ExistingLabel: TNewStaticText;
  ExistingFound, Prepared, Finished: Boolean;
  ExistingMode, OldRoot, OldData, OldPort, OldVersion, OldUrl: String;
  EnginePath, ResultPath, StatePath: String;

function IsFull: Boolean;
begin Result := ModePage.SelectedValueIndex = 0; end;

function UsesRetainedDatabase: Boolean;
begin
  Result := IsFull and not ExistingFound and FileExists(AddBackslash(DataPage.Values[0]) + 'level-manager.db');
end;

function DataDirectory(Param: String): String;
begin Result := DataPage.Values[0]; end;

function ReadResult: String;
begin
  Result := GetIniString('Result', 'Message', '检查结果已保存。', ResultPath);
end;

function PowerShellPath: String;
begin Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'); end;

function RunEngine(Action, Extra: String): Boolean;
var Code: Integer; Args: String;
begin
  DeleteFile(ResultPath);
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + EnginePath +
    '" -Action ' + Action + ' -ResultFile "' + ResultPath + '" -ServiceName "{#InstallerService}" ' + Extra;
  Result := Exec(PowerShellPath, Args, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  Log('Setup engine ' + Action + ': ' + ReadResult);
end;

function PlanArgs: String;
begin
  Result := '-InstallRoot "' + WizardDirValue + '" -StateFile "' + StatePath + '" -VersionDirectory "{#VersionDirectory}"';
  if IsFull then begin
    Result := Result + ' -Mode Full -DataRoot "' + DataPage.Values[0] + '" -Port ' + PortEdit.Text;
    if LanBox.Checked then Result := Result + ' -AllowLan';
    if UsesRetainedDatabase and ConfirmPage.Values[0] then Result := Result + ' -ReuseExistingData';
  end else Result := Result + ' -Mode Client -ServerUrl "' + ConnectionPage.Values[0] + '"';
end;

procedure InitializeWizard;
var Ini: String; PortLabel: TNewStaticText;
begin
#ifdef PreviewOnly
  WizardForm.Caption := '液位管理系统安装向导画面核对';
#endif
  ExtractTemporaryFile('SetupEngine.ps1');
  EnginePath := ExpandConstant('{tmp}\SetupEngine.ps1');
  ResultPath := ExpandConstant('{tmp}\setup-result.txt');
  StatePath := ExpandConstant('{tmp}\setup-transaction.json');
  ExistingFound := False;
#ifdef PreviewOnly
  if not RunEngine('Inspect', '-InstallRoot "' + WizardDirValue + '" -IgnoreInstalledService') then RaiseException(ReadResult);
#else
  if not RunEngine('Inspect', '-InstallRoot "' + WizardDirValue + '"') then RaiseException('无法检查已有安装。' + #13#10 + ReadResult);
#endif
  Ini := ResultPath;
  ExistingFound := GetIniInt('Existing', 'Found', 0, 0, 1, Ini) = 1;
  ExistingMode := GetIniString('Existing', 'Mode', '', Ini);
  OldRoot := GetIniString('Existing', 'InstallRoot', '', Ini);
  OldData := GetIniString('Existing', 'DataRoot', '', Ini);
  OldPort := GetIniString('Existing', 'Port', '8780', Ini);
  OldVersion := GetIniString('Existing', 'Version', '尚未安装', Ini);
  OldUrl := GetIniString('Existing', 'ServerUrl', 'http://127.0.0.1:8780/', Ini);
  ModePage := CreateInputOptionPage(wpWelcome, '选择安装内容', '这台电脑将如何使用液位管理系统',
    '本机连接仪表请选择完整安装；连接其他采集主机请选择仅安装客户端。', True, False);
  ModePage.Add('本机完整安装（后台服务和客户端）');
  ModePage.Add('仅安装客户端（连接其他采集主机）');
  ModePage.SelectedValueIndex := 0;
  ExistingLabel := TNewStaticText.Create(ModePage);
  ExistingLabel.Parent := ModePage.Surface;
  ExistingLabel.SetBounds(0, ScaleY(145), ModePage.SurfaceWidth, ScaleY(80));
  ExistingLabel.WordWrap := True;
  if ExistingFound then begin
    ExistingLabel.Caption := '已发现安装：' + OldVersion + #13#10 + '本次将升级原安装。程序目录和数据目录沿用原设置。';
    if ExistingMode = 'Client' then ModePage.SelectedValueIndex := 1;
    ModePage.CheckListBox.Enabled := False;
    WizardForm.DirEdit.Text := OldRoot;
    WizardForm.DirEdit.Enabled := False;
    WizardForm.DirBrowseButton.Enabled := False;
  end else ExistingLabel.Caption := '本次安装版本：r24。首次安装完成后，请建立管理员并导入供方授权。';
  DataPage := CreateInputDirPage(wpSelectDir, '选择业务数据位置', '数据库、历史和配置保存在哪里',
    '建议选择空间充足的本机固定磁盘。业务目录与程序目录分开保存。升级将沿用原目录。', False, '');
  DataPage.Add('业务数据目录');
  DataPage.Values[0] := OldData;
  PortLabel := TNewStaticText.Create(DataPage);
  PortLabel.Parent := DataPage.Surface;
  PortLabel.SetBounds(0, ScaleY(110), ScaleX(180), ScaleY(22));
  PortLabel.Caption := '后台端口（默认 8780）';
  PortEdit := TNewEdit.Create(DataPage);
  PortEdit.Parent := DataPage.Surface;
  PortEdit.SetBounds(0, ScaleY(140), ScaleX(155), ScaleY(24));
  PortEdit.Text := OldPort;
  LanBox := TNewCheckBox.Create(DataPage);
  LanBox.Parent := DataPage.Surface;
  LanBox.SetBounds(0, ScaleY(190), DataPage.SurfaceWidth, ScaleY(28));
  LanBox.Caption := '允许其他电脑连接（仅放行 Windows 专用网络）';
  LanBox.Checked := GetIniInt('Existing', 'AllowLan', 0, 0, 1, Ini) = 1;
  if ExistingFound then begin
    DataPage.Edits[0].Enabled := False; DataPage.Buttons[0].Enabled := False;
    PortEdit.Enabled := False; LanBox.Enabled := False;
  end;
  ConnectionPage := CreateInputQueryPage(DataPage.ID, '设置服务器地址', '连接已经安装后台的采集主机',
    '请输入采集主机的 IP 地址和端口，例如 http://192.168.1.10:8780/。');
  ConnectionPage.Add('服务器地址', False);
  ConnectionPage.Values[0] := OldUrl;
#ifdef SetupQa
  DataPage.Values[0] := ExpandConstant('{param:DATADIR|' + OldData + '}');
  PortEdit.Text := ExpandConstant('{param:PORT|' + OldPort + '}');
  if ExpandConstant('{param:MODE|Full}') = 'Client' then ModePage.SelectedValueIndex := 1;
  ConnectionPage.Values[0] := ExpandConstant('{param:SERVERURL|' + OldUrl + '}');
#endif
  ConfirmPage := CreateInputOptionPage(wpSelectTasks, '安装前确认', '确认现场准备情况',
    '升级时会停止原后台。卸载后重装时，向导会检查所选业务数据库，并在原业务目录中保存重装前副本。请先备份业务数据，结束现场仪表作业，并关闭旧客户端。', False, False);
  ConfirmPage.Add('已完成备份和现场作业，确认沿用所选业务数据继续安装');
#ifdef SetupQa
  ConfirmPage.Values[0] := True;
#endif
  WizardForm.WelcomeLabel2.Caption := '本向导将安装液位管理系统 r24。' + #13#10#13#10 +
    '您可以选择安装内容、程序位置、业务数据位置和桌面快捷方式。安装向导会配置 Windows 后台服务及所需运行环境。';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = DataPage.ID) and not IsFull) or
    ((PageID = ConnectionPage.ID) and IsFull) or
    ((PageID = ConfirmPage.ID) and not ExistingFound and not UsesRetainedDatabase);
end;

function SafeQuoted(Value: String): Boolean;
begin Result := (Pos('"', Value) = 0) and (Pos(#13, Value) = 0) and (Pos(#10, Value) = 0); end;

function NextButtonClick(CurPageID: Integer): Boolean;
var Port: Integer;
begin
  Result := True;
  if CurPageID = ConfirmPage.ID then
    if not ConfirmPage.Values[0] then begin MsgBox('请先备份、结束现场作业并勾选确认。', mbError, MB_OK); Result := False; end;
  if ((CurPageID = DataPage.ID) and IsFull) or ((CurPageID = ConnectionPage.ID) and not IsFull) then begin
    if not SafeQuoted(WizardDirValue) or not SafeQuoted(DataPage.Values[0]) or not SafeQuoted(ConnectionPage.Values[0]) then begin
      MsgBox('目录或地址含有无效字符，请重新填写。', mbError, MB_OK); Result := False; exit;
    end;
    Port := StrToIntDef(PortEdit.Text, 0);
    if IsFull and ((Port < 1024) or (Port > 65535)) then begin MsgBox('端口应为 1024 至 65535。', mbError, MB_OK); Result := False; exit; end;
#ifndef SetupQa
    ConfirmPage.Values[0] := False;
#endif
    WizardForm.NextButton.Enabled := False;
    try
#ifndef PreviewOnly
      if not RunEngine('Validate', PlanArgs) then begin MsgBox(ReadResult, mbError, MB_OK); Result := False; end;
#endif
    finally WizardForm.NextButton.Enabled := True; end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, User, Dir, InstallationType, Components, Group, Tasks: String): String;
begin
  if IsFull then Result := '安装内容：后台服务和客户端' + NewLine + '业务目录：' + DataPage.Values[0] + NewLine + '后台端口：' + PortEdit.Text
  else Result := '安装内容：仅客户端' + NewLine + '服务器地址：' + ConnectionPage.Values[0];
  Result := '程序版本：r24' + NewLine + Result + NewLine + '程序目录：' + WizardDirValue + NewLine + NewLine + Tasks;
  if ExistingFound then Result := Result + NewLine + NewLine + '原安装：' + OldVersion + NewLine + '升级保留原账号、配置、历史和授权。';
  if UsesRetainedDatabase then Result := Result + NewLine + NewLine +
    '重装方式：沿用原业务数据。安装前将检查数据库，并在业务目录的“重装备份”中保存副本。';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
#ifdef PreviewOnly
  Result := '此文件仅用于安装向导画面核对，不执行安装。';
#else
  Result := '';
#ifdef SetupQa
  ConfirmPage.Values[0] := True;
#endif
  if (ExistingFound or UsesRetainedDatabase) and not ConfirmPage.Values[0] then begin
    Result := '沿用原业务数据前，请返回确认已完成备份和现场作业。'; exit;
  end;
  if not RunEngine('Prepare', PlanArgs + ' -Confirmed') then begin
    Result := ReadResult;
    if FileExists(StatePath) then RunEngine('Rollback', '-InstallRoot "' + WizardDirValue + '" -StateFile "' + StatePath + '"');
    exit;
  end;
  Prepared := True;
#endif
end;

procedure InstallWebView;
var Exists: String; Code: Integer; Args: String;
begin
  if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Exists) and (Exists <> '') and (Exists <> '0.0.0.0') then exit;
  WizardForm.StatusLabel.Caption := '正在检查并安装 Microsoft Edge WebView2 运行环境…';
  ExtractTemporaryFile('MicrosoftEdgeWebView2RuntimeInstallerX64.exe');
  Args := '/silent /install';
  if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, Code) then RaiseException('运行环境安装程序无法启动。');
  if (Code <> 0) and (Code <> 3010) then RaiseException('WebView2 安装未完成，退出码 ' + IntToStr(Code));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    InstallWebView;
    WizardForm.StatusLabel.Caption := '正在配置后台和客户端并检查连接…';
    if not RunEngine('Commit', PlanArgs) then RaiseException(ReadResult);
    Finished := True;
  end;
end;

procedure DeinitializeSetup;
begin
  if Prepared and not Finished then
    if not RunEngine('Rollback', '-InstallRoot "' + WizardDirValue + '" -StateFile "' + StatePath + '"') then
      MsgBox('服务设置恢复需要处理，请保留安装日志并联系供方。' + #13#10 + ReadResult, mbError, MB_OK);
end;

function InitializeUninstall: Boolean;
var Code: Integer; Args: String;
begin
  Result := False;
#ifndef SetupQa
  if MsgBox('卸载将停止后台并移除程序。业务数据、历史和本机授权将保留。' + #13#10#13#10 + '请先备份并结束现场作业，关闭客户端。是否继续？', mbConfirmation, MB_YESNO) <> IDYES then exit;
#endif
  Args := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\setup\SetupEngine.ps1') + '" -Action Uninstall -InstallRoot "' + ExpandConstant('{app}') + '" -ServiceName "{#InstallerService}" -Confirmed';
  Result := Exec(PowerShellPath, Args, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  if not Result then MsgBox('后台服务未能正常退出或安装记录不匹配，已取消卸载。请关闭相关程序后再试。', mbError, MB_OK);
end;
