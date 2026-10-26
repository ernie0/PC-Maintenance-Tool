; 電腦維護工具安裝程式。請用 build-installer.ps1 編譯，它會先跑測試、發佈程式再呼叫 ISCC。
#ifndef PublishDir
  #error PublishDir must be supplied by build-installer.ps1
#endif
#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1
#endif
; 使用說明書由 docs\manual\build-manual.ps1 產生；有這個版本的 PDF 才一起安裝。
#define ManualPdf AddBackslash(SourcePath) + "..\dist\電腦維護工具使用說明書-" + AppVersion + ".pdf"
; 加 /DFrameworkDependent 編譯精簡版：程式不自帶 .NET，改用電腦上的 .NET 10 桌面執行環境，安裝檔約 4 MB。
#ifdef FrameworkDependent
  #define SetupSuffix "-lite"
  #define EditionName "（精簡版）"
  ; 電腦沒有執行環境時，安裝前從 Microsoft 下載這個版本來安裝。換版本時版本與 SHA-256 一起改：SHA-256 由下載的檔案
  ; 自行計算，並先核對 Microsoft 版本清單（release-metadata/10.0/releases.json）的 SHA-512 與檔案的 Microsoft 簽章。
  #define RuntimeVersion "10.0.14"
  #define RuntimeSha256 "91c2795a7b1e2480caa83fce9514dc2e2d21e277d9a297311faba6040bad7330"
  #define RuntimeFile "windowsdesktop-runtime-" + RuntimeVersion + "-win-x64.exe"
  #define RuntimeUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/" + RuntimeVersion + "/" + RuntimeFile
#else
  #define SetupSuffix ""
  #define EditionName ""
#endif

[Setup]
; AppId 固定不變，升級時才會覆蓋同一套安裝。
AppId={{1350D837-621C-42D9-9A3D-20551989FA77}
AppName=電腦維護工具
AppVersion={#AppVersion}
AppVerName=電腦維護工具 {#AppVersion}
DefaultDirName={autopf}\PcMaintenance
DefaultGroupName=電腦維護工具
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=PcMaintenance-Setup-{#AppVersion}{#SetupSuffix}
SetupIconFile=..\src\PcMaintenance.App\Assets\app.ico
UninstallDisplayIcon={app}\PcMaintenance.exe
UninstallDisplayName=電腦維護工具
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
VersionInfoVersion={#AppVersion}
VersionInfoProductName=電腦維護工具
VersionInfoDescription=電腦維護工具安裝程式{#EditionName}

[Languages]
Name: "chinesetraditional"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"

[Tasks]
Name: "desktopicon"; Description: "建立桌面捷徑"; GroupDescription: "捷徑："; Flags: unchecked
Name: "autoclean"; Description: "每位使用者登入時自動清理暫存檔與快取（間隔天數依公司設定，預設 7 天）"; GroupDescription: "自動維護："; Flags: unchecked

[Registry]
; 自動清理：登入時以使用者本身的權限在背景執行，未到間隔天數就直接結束；取消勾選重裝或解除安裝時移除。
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PcMaintenanceAutoClean"; ValueData: """{app}\PcMaintenance.exe"" --auto-clean"; Flags: uninsdeletevalue; Tasks: autoclean
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "PcMaintenanceAutoClean"; Flags: deletevalue; Tasks: not autoclean

#ifdef FrameworkDependent
[InstallDelete]
; 從完整版換成精簡版時，完整版自帶的 WPF 原生檔不能留在程式旁邊，否則會蓋過電腦上執行環境的同名檔。
Type: files; Name: "{app}\D3DCompiler_47_cor3.dll"
Type: files; Name: "{app}\PenImc_cor3.dll"
Type: files; Name: "{app}\PresentationNative_cor3.dll"
Type: files; Name: "{app}\vcruntime140_cor3.dll"
Type: files; Name: "{app}\wpfgfx_cor3.dll"

#endif
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "settings.example.json"; DestDir: "{commonappdata}\PcMaintenance"; Flags: ignoreversion
; 授權條款與第三方元件授權；完整版內含 .NET 執行環境，散布時要附上它的授權聲明。
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
#if FileExists(ManualPdf)
Source: "{#ManualPdf}"; DestDir: "{app}"; DestName: "使用說明書.pdf"; Flags: ignoreversion
#endif
; 公司設定：安裝時加 /SETTINGS="設定檔路徑" 才會寫入；解除安裝時保留。
Source: "{code:SettingsSource}"; DestDir: "{commonappdata}\PcMaintenance"; DestName: "settings.json"; Flags: external ignoreversion uninsneveruninstall; Check: SettingsGiven

[Icons]
Name: "{autoprograms}\電腦維護工具"; Filename: "{app}\PcMaintenance.exe"
Name: "{autodesktop}\電腦維護工具"; Filename: "{app}\PcMaintenance.exe"; Tasks: desktopicon
#if FileExists(ManualPdf)
Name: "{autoprograms}\電腦維護工具使用說明書"; Filename: "{app}\使用說明書.pdf"
#endif

[Run]
; 以原本的使用者身分啟動，不沿用安裝程式的系統管理員權限。
Filename: "{app}\PcMaintenance.exe"; Description: "啟動電腦維護工具"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// 相對路徑以安裝檔所在資料夾為準，方便和安裝檔放在同一個共用資料夾。
function SettingsSource(Param: String): String;
begin
  Result := ExpandConstant('{param:SETTINGS|}');
  if (Result <> '') and (ExtractFileDrive(Result) = '') then
    Result := AddBackslash(ExpandConstant('{src}')) + Result;
end;

function SettingsGiven: Boolean;
begin
  Result := SettingsSource('') <> '';
end;

#ifdef FrameworkDependent
const
  // 訊息在句子之間自己換行，避免精靈頁面把最後一兩個字擠到下一行。
  FullSetupHint = '也可以改用完整版安裝檔 PcMaintenance-Setup-{#AppVersion}.exe，'#13#10'它內含執行環境，不需要另外下載安裝。';

var
  DownloadPage: TDownloadWizardPage;
  RuntimePage: TOutputMarqueeProgressWizardPage;
  RuntimeNeedsRestart: Boolean;

// 精簡版靠電腦上的 .NET 10 桌面執行環境（任何 10.x）執行；Microsoft 的安裝檔把它放在這裡。
function DesktopRuntimeInstalled: Boolean;
var
  Found: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), Found);
  if Result then FindClose(Found);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('下載 .NET 10 桌面執行環境',
    '這台電腦還沒有精簡版需要的 .NET 10 桌面執行環境，'#13#10'正在從 Microsoft 官方網站下載（約 58 MB）。', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
  RuntimePage := CreateOutputMarqueeProgressPage('安裝 .NET 10 桌面執行環境',
    '正在安裝 Microsoft .NET 10 桌面執行環境，可能需要幾分鐘，'#13#10'完成後會接著安裝電腦維護工具。');
end;

procedure AddMemoSection(var Memo: String; const Section, NewLine: String);
begin
  if Section = '' then Exit;
  if Memo <> '' then Memo := Memo + NewLine + NewLine;
  Memo := Memo + Section;
end;

// 按下安裝前先讓使用者知道會從網路下載多少東西。
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '';
  AddMemoSection(Result, MemoUserInfoInfo, NewLine);
  AddMemoSection(Result, MemoDirInfo, NewLine);
  AddMemoSection(Result, MemoTypeInfo, NewLine);
  AddMemoSection(Result, MemoComponentsInfo, NewLine);
  AddMemoSection(Result, MemoGroupInfo, NewLine);
  AddMemoSection(Result, MemoTasksInfo, NewLine);
  if not DesktopRuntimeInstalled then
    AddMemoSection(Result, '安裝前會先處理：' + NewLine + Space +
      '從 Microsoft 下載並安裝 .NET 10 桌面執行環境 {#RuntimeVersion}（約 58 MB，需要網路）', NewLine);
end;

// 下載 Microsoft 的 .NET 10 桌面執行環境（核對 SHA-256）並安裝；成功傳回空字串，否則傳回給使用者看的原因。
function InstallDesktopRuntime: String;
var
  ResultCode: Integer;
begin
  Result := '';
  DownloadPage.Clear;
  DownloadPage.Add('{#RuntimeUrl}', '{#RuntimeFile}', '{#RuntimeSha256}');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      // 例外訊息夾著英文的內部錯誤，只寫進安裝記錄（/LOG）給資訊人員看。
      Log('下載 .NET 10 桌面執行環境失敗：' + GetExceptionMessage);
      if DownloadPage.AbortedByUser then
        Result := '已停止下載 .NET 10 桌面執行環境，沒有安裝任何東西。'
      else
        Result := '無法從 Microsoft 下載 .NET 10 桌面執行環境。'#13#10'請確認這台電腦能連上網際網路後再執行一次。';
    end;
  finally
    DownloadPage.Hide;
  end;

  if Result = '' then
  begin
    RuntimePage.Show;
    try
      RuntimePage.Animate;
      if not Exec(ExpandConstant('{tmp}\{#RuntimeFile}'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        Result := '無法執行 .NET 10 桌面執行環境的安裝程式（' + SysErrorMessage(ResultCode) + '）。'
      else if ResultCode = 1618 then
        Result := '另一個安裝程式正在執行（例如 Windows 更新），.NET 10 桌面執行環境沒有裝上。'#13#10'請等它完成後再執行一次。'
      else if ((ResultCode <> 0) and (ResultCode <> 3010)) or not DesktopRuntimeInstalled then
        Result := '.NET 10 桌面執行環境沒有裝上（安裝程式結束代碼 ' + IntToStr(ResultCode) + '）。'#13#10'請重新開機後再執行一次。'
      else
        RuntimeNeedsRestart := (ResultCode = 3010);
    finally
      RuntimePage.Hide;
    end;
  end;

  if Result <> '' then
    Result := Result + #13#10#13#10 + FullSetupHint;
end;

function NeedRestart: Boolean;
begin
  Result := RuntimeNeedsRestart;
end;

#endif
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if SettingsGiven and not FileExists(SettingsSource('')) then
  begin
    Result := '找不到 /SETTINGS 指定的公司設定檔：' + SettingsSource('');
    Exit;
  end;
#ifdef FrameworkDependent
  // 公司設定檔先確認過，有錯就不用白白下載。
  if not DesktopRuntimeInstalled then
    Result := InstallDesktopRuntime;
#endif
end;
