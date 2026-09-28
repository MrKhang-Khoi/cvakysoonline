; ==============================================================================
; KỊCH BẢN ĐÓNG GÓI BỘ CÀI ĐẶT CHUẨN WINDOWS - VGCA SIGN TOOL PRO
; Công nghệ: Inno Setup 6 (Chuẩn Microsoft Per-User Deployment)
; Tác giả: Ban Cơ Yếu Chính Phủ & Nhà Trường
; ==============================================================================

#define MyAppName "VGCA Sign Tool Pro"
#define MyAppVersion "2.1.0"
#define MyAppPublisher "Ban Co Yeu Chinh Phu & Nha Truong"
#define MyAppURL "https://github.com/MrKhang-Khoi/cvakysoonline"
#define MyAppExeName "VGCASignToolPro.exe"
#define MyAppSourceDir "..\dist\win-x64"

[Setup]
AppId={{9F5C7A2E-8B3D-4C61-9E2A-18F7B543D021}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Chuẩn Microsoft Per-User: Cài đặt vào %LocalAppData%\Programs không cần quyền Administrator
DefaultDirName={localappdata}\Programs\VGCASignToolPro
DisableDirPage=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Xuất file cài đặt
OutputDir=..\Output_Installer
OutputBaseFilename=VGCASignToolPro_Setup_v2.1
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

; Biểu tượng và gỡ cài đặt
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} (Gỡ cài đặt)
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 1. Toàn bộ các tệp tin chương trình Self-Contained x64
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "school_roster.json,school_roster.json.hmac,workflow_config.json,profiles.json,.roster.key,logs\*,WebView2Cache\*"

; 2. TỆP DỮ LIỆU NHÀ TRƯỜNG: BẢO VỆ TUYỆT ĐỐI (Chỉ sao chép nếu chưa tồn tại)
Source: "{#MyAppSourceDir}\school_roster.json"; DestDir: "{app}"; Flags: skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall
Source: "{#MyAppSourceDir}\school_roster.json.hmac"; DestDir: "{app}"; Flags: skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall
Source: "{#MyAppSourceDir}\workflow_config.json"; DestDir: "{app}"; Flags: skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Kiểm tra WebView2 Runtime đã có trên máy hay chưa
function IsWebView2Installed(): Boolean;
var
  VersionStr: String;
begin
  Result := RegQueryStringValue(HKEY_CURRENT_USER, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-69D3A8E3EFE7}', 'pv', VersionStr) or
            RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-69D3A8E3EFE7}', 'pv', VersionStr) or
            RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-69D3A8E3EFE7}', 'pv', VersionStr);
end;

// Kiểm tra phần mềm VGCA Virtual CSP v2.0 của Ban Cơ yếu
function IsVgcaVirtualCspInstalled(): Boolean;
begin
  Result := FileExists('C:\Program Files\VGCA\Virtual CSP v2.0\vgca_vcsp_v2_mgr.exe') or
            FileExists('C:\Users\Public\Desktop\VGCA Virtual CSP v2.0.lnk') or
            FileExists(ExpandConstant('{commonprograms}\VGCA\Virtual CSP v2.0.lnk'));
end;

function InitializeSetup(): Boolean;
var
  MsgPrompt: String;
begin
  Result := True;

  // Cảnh báo thân thiện nếu máy tính chưa có phần mềm Ban Cơ yếu
  if not IsVgcaVirtualCspInstalled() then
  begin
    MsgPrompt := 'LƯU Ý QUAN TRỌNG TỪ BAN CƠ YẾU CHÍNH PHỦ:' + #13#10 + #13#10 +
                 'Bộ cài đặt phát hiện máy tính này chưa cài phần mềm VGCA Virtual CSP v2.0.' + #13#10 +
                 'Phần mềm VGCA Sign Tool Pro vẫn sẽ được cài đặt bình thường. Tuy nhiên, để thực hiện ký số văn bản, ' +
                 'bạn cần cài đặt và đăng nhập ứng dụng VGCA Virtual CSP v2.0.' + #13#10 + #13#10 +
                 'Bạn có muốn tiếp tục cài đặt VGCA Sign Tool Pro không?';
    if MsgBox(MsgPrompt, mbInformation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  end;
end;
