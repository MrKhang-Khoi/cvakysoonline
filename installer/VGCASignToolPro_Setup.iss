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

; Biểu tượng chính thức của ứng dụng & gỡ cài đặt
SetupIconFile=app_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} (Gỡ cài đặt)
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; MẶC ĐỊNH 100% ĐƯỢC CHỌN: Tạo icon Desktop & Tự khởi động cùng Windows
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Tự động khởi động cùng Windows khi bật máy (Sẵn sàng nhận lệnh ký số)"; GroupDescription: "Cấu hình hệ thống:"
Name: "installvgca"; Description: "Tự động cài đặt phần mềm ký số VGCA Sign Tool v3.0.6 chính thức của Ban Cơ Yếu Chính Phủ"; GroupDescription: "Thành phần phụ trợ Ban Cơ Yếu:"; Check: not IsVgcaInstalled

[Files]
; 1. Toàn bộ các tệp tin chương trình Self-Contained x64
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "school_roster.json,school_roster.json.hmac,workflow_config.json,profiles.json,.roster.key,logs\*,WebView2Cache\*"

; 2. Biểu tượng chính thức của ứng dụng
Source: "app_icon.ico"; DestDir: "{app}"; Flags: ignoreversion

; 3. TỆP DỮ LIỆU NHÀ TRƯỜNG: BẢO VỆ TUYỆT ĐỐI (Chỉ sao chép nếu chưa tồn tại)
Source: "{#MyAppSourceDir}\school_roster.json"; DestDir: "{app}"; Flags: skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall
Source: "{#MyAppSourceDir}\workflow_config.json"; DestDir: "{app}"; Flags: skipifsourcedoesntexist onlyifdoesntexist uninsneveruninstall

; 4. GÓI TÍCH HỢP ALL-IN-ONE: Bộ cài VGCA Sign Tool v3.0.6 chính thức của Ban Cơ Yếu Chính Phủ
Source: "redist\*"; DestDir: "{tmp}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "redist\*"; DestDir: "{app}\redist"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Đăng ký khởi động cùng Windows khi bật máy
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VGCASignToolPro"; ValueData: """{app}\{#MyAppExeName}"" --autostart"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
; Cài đặt nền tảng VGCA Sign Tool v3.0.6 của Ban Cơ Yếu Chính Phủ nếu máy chưa có
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\VGCASignToolSetup.msi"""; Description: "Cài đặt phần mềm ký số VGCA Sign Tool v3.0.6 chính thức của Ban Cơ Yếu Chính Phủ"; Flags: skipifdoesntexist nowait postinstall; Tasks: installvgca; Check: not IsVgcaInstalled
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

// Kiểm tra toàn diện phần mềm VGCA (VGCA Sign Tool v3.0.6 hoặc Virtual CSP v2.0) đã có trên máy hay chưa
function IsVgcaInstalled(): Boolean;
begin
  Result := FileExists('C:\Program Files (x86)\VGCA\VGCASignTool\VGCASignTool.exe') or
            FileExists('C:\Program Files\VGCA\VGCASignTool\VGCASignTool.exe') or
            FileExists('C:\Program Files (x86)\VGCA\Virtual CSP v2.0\vgca_vcsp_v2_mgr.exe') or
            FileExists('C:\Program Files\VGCA\Virtual CSP v2.0\vgca_vcsp_v2_mgr.exe') or
            FileExists('C:\Windows\System32\vgca_vcsp11_v2.dll') or
            RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{7FBD335C-4ED2-4ECC-8A17-BDF454F15E75}') or
            RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7FBD335C-4ED2-4ECC-8A17-BDF454F15E75}') or
            RegKeyExists(HKEY_LOCAL_MACHINE, 'SYSTEM\CurrentControlSet\Control\Cryptography\Providers\VGCA RSSP KSP v2.0') or
            FileExists('C:\Users\Public\Desktop\VGCA Sign Tool.lnk') or
            FileExists('C:\Users\Public\Desktop\VGCA Virtual CSP v2.0.lnk') or
            FileExists(ExpandConstant('{commonprograms}\VGCA\VGCA Sign Tool.lnk')) or
            FileExists(ExpandConstant('{commonprograms}\VGCA\Virtual CSP v2.0.lnk'));
end;

function InitializeSetup(): Boolean;
var
  MsgPrompt: String;
begin
  Result := True;

  // Nếu máy tính chưa cài đặt VGCA và không có bộ cài song hành trong redist
  if not IsVgcaInstalled() and not FileExists(ExpandConstant('{tmp}\VGCASignToolSetup.msi')) then
  begin
    MsgPrompt := 'THÔNG BÁO TỪ BAN CƠ YẾU CHÍNH PHỦ:' + #13#10 + #13#10 +
                 'Phần mềm VGCA Sign Tool Pro sẽ được cài đặt và tự động tạo biểu tượng ngoài Desktop.' + #13#10 + #13#10 +
                 '• Nếu Thầy/Cô sử dụng USB Token phần cứng: Có thể cắm USB và ký số được ngay.' + #13#10 +
                 '• Nếu cần ký số văn bản định dạng PDF Ban Cơ Yếu: Cần cài thêm ứng dụng VGCA Sign Tool.' + #13#10 + #13#10 +
                 'Bấm "OK" để tiếp tục cài đặt phần mềm.';
    MsgBox(MsgPrompt, mbInformation, MB_OK);
  end;
end;
