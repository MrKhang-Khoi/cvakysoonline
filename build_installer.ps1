# ==============================================================================
# SCRIPT DONG GOI BO CAI DAT & GOI UPDATE TU DONG CHUAN MICROSOFT WINDOWS
# Project: VGCA Sign Tool Pro (v2.1.0)
# ==============================================================================

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$projectDir = $scriptDir
$distDir = Join-Path $projectDir "dist\win-x64"
$outputInstallerDir = Join-Path $projectDir "Output_Installer"
$innoCompiler = "C:\Users\HPZBook\AppData\Local\Programs\Inno Setup 6\ISCC.exe"

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " START PACKAGING VGCA SIGN TOOL PRO (MICROSOFT WINDOWS STANDARD) " -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

# 1. CLEAN DIRECTORIES
Write-Host "`n[1/6] Cleaning distribution directories..." -ForegroundColor Yellow
if (Test-Path $distDir) {
    Remove-Item -Path $distDir -Recurse -Force
}
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

if (-not (Test-Path $outputInstallerDir)) {
    New-Item -ItemType Directory -Path $outputInstallerDir -Force | Out-Null
}

# 2. PUBLISH MAIN APP (SELF-CONTAINED WIN-X64)
Write-Host "`n[2/6] Publishing Main App (.NET 8 Self-Contained win-x64)..." -ForegroundColor Yellow
$mainCsproj = Join-Path $projectDir "VGCASignToolPro.csproj"
dotnet publish $mainCsproj -c Release -r win-x64 --self-contained true -o $distDir

# 3. PUBLISH STANDALONE UPDATER
Write-Host "`n[3/6] Publishing Standalone Updater (VGCASignToolPro.Updater)..." -ForegroundColor Yellow
$updaterCsproj = Join-Path $projectDir "Updater\VGCASignToolPro.Updater.csproj"
dotnet publish $updaterCsproj -c Release -r win-x64 --self-contained true -o $distDir

# 4. COPY APP-LOCAL C++ RUNTIME DLLS (ZERO-DEPENDENCY)
Write-Host "`n[4/6] Bundling App-Local C++ CRT DLLs (vcruntime140, msvcp140)..." -ForegroundColor Yellow
$crtDlls = @("vcruntime140.dll", "msvcp140.dll", "vcruntime140_1.dll")
foreach ($dll in $crtDlls) {
    $sysPath = Join-Path $env:SystemRoot "System32\$dll"
    if (Test-Path $sysPath) {
        Copy-Item -Path $sysPath -Destination $distDir -Force
        Write-Host "  + Added App-Local: $dll" -ForegroundColor Green
    }
}

# Ensure fresh school_roster.json is copied and no stale hmac interferes with first-run seal
$sourceRoster = Join-Path $projectDir "school_roster.json"
if (Test-Path $sourceRoster) {
    Copy-Item -Path $sourceRoster -Destination $distDir -Force
}
if (Test-Path (Join-Path $distDir "school_roster.json.hmac")) {
    Remove-Item -Path (Join-Path $distDir "school_roster.json.hmac") -Force
}

$sourceIcon = Join-Path $projectDir "app_icon.ico"
if (Test-Path $sourceIcon) {
    Copy-Item -Path $sourceIcon -Destination $distDir -Force
}

$sourceRedist = Join-Path $projectDir "installer\redist"
if (Test-Path $sourceRedist) {
    $targetRedist = Join-Path $distDir "redist"
    if (-not (Test-Path $targetRedist)) { New-Item -ItemType Directory -Path $targetRedist -Force | Out-Null }
    Copy-Item -Path "$sourceRedist\*" -Destination $targetRedist -Force
    Write-Host "  + Bundled VGCA Redistributable Packages to dist\win-x64\redist" -ForegroundColor Green
}

# 5. COMPILE INNO SETUP INSTALLER (.EXE)
Write-Host "`n[5/6] Compiling Official Inno Setup Single-Installer (.exe)..." -ForegroundColor Yellow
if (-not (Test-Path $innoCompiler)) {
    throw "Inno Setup Compiler not found at: $innoCompiler"
}

$issFile = Join-Path $projectDir "installer\VGCASignToolPro_Setup.iss"
& $innoCompiler $issFile

$installerExe = Join-Path $outputInstallerDir "VGCASignToolPro_Setup_v2.1.exe"
if (-not (Test-Path $installerExe)) {
    throw "Error: Installer output file was not found after compilation: $installerExe"
}

$instSizeMb = [math]::Round(((Get-Item $installerExe).Length / 1MB), 2)
$instHash = (Get-FileHash -Path $installerExe -Algorithm SHA256).Hash

# 6. BUILD AUTO-UPDATE ZIP PACKAGE (EXCLUDING SENSITIVE USER DATA)
Write-Host "`n[6/6] Building Auto-Update Package (ZIP for UpdateService)..." -ForegroundColor Yellow
$updateZipPath = Join-Path $outputInstallerDir "VGCASignToolPro_Update_v2.1.0.zip"
if (Test-Path $updateZipPath) {
    Remove-Item -Path $updateZipPath -Force
}

$tempZipDir = Join-Path ([System.IO.Path]::GetTempPath()) ("VGCASignToolPro_ZipStaging_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempZipDir -Force | Out-Null

try {
    # Copy files excluding user configs and caches
    Get-ChildItem -Path $distDir -Recurse | ForEach-Object {
        $full = $_.FullName
        $relPath = $full.Substring($distDir.Length).TrimStart('\', '/')
        if ([string]::IsNullOrWhiteSpace($relPath)) { return }

        $excludedNames = @("school_roster.json", "school_roster.json.hmac", "workflow_config.json", "profiles.json", ".roster.key", "logs", "WebView2Cache")
        
        $shouldExclude = $false
        foreach ($ex in $excludedNames) {
            if ($_.Name -ieq $ex -or $relPath -ilike "*$ex*") {
                $shouldExclude = $true
                break
            }
        }

        if (-not $shouldExclude) {
            $dest = Join-Path $tempZipDir $relPath
            if ($_.PSIsContainer) {
                if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }
            } else {
                $destParent = Split-Path -Parent $dest
                if (-not (Test-Path $destParent)) { New-Item -ItemType Directory -Path $destParent -Force | Out-Null }
                Copy-Item -Path $full -Destination $dest -Force
            }
        }
    }

    # Compress staging folder to ZIP
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($tempZipDir, $updateZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
}
finally {
    if (Test-Path $tempZipDir) {
        Remove-Item -Path $tempZipDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$zipSizeMb = [math]::Round(((Get-Item $updateZipPath).Length / 1MB), 2)
$zipHash = (Get-FileHash -Path $updateZipPath -Algorithm SHA256).Hash

Write-Host "`n=================================================================" -ForegroundColor Green
Write-Host " PACKAGING AND RELEASE GENERATION COMPLETED SUCCESSFULLY! " -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
Write-Host "1. OFFICIAL INSTALLER (Fresh install on new machines):" -ForegroundColor White
Write-Host "   Path:   $installerExe" -ForegroundColor Cyan
Write-Host "   Size:   $instSizeMb MB" -ForegroundColor White
Write-Host "   SHA256: $instHash" -ForegroundColor Yellow
Write-Host ""
Write-Host "2. AUTO-UPDATE ZIP PACKAGE (For in-app OTA update):" -ForegroundColor White
Write-Host "   Path:   $updateZipPath" -ForegroundColor Cyan
Write-Host "   Size:   $zipSizeMb MB" -ForegroundColor White
Write-Host "   SHA256: $zipHash" -ForegroundColor Yellow
Write-Host ""
Write-Host "3. FIREBASE /app_release.json METADATA PAYLOAD:" -ForegroundColor White
$jsonPayload = @"
{
  "version": "2.1.0",
  "minSupportedVersion": "2.0.0",
  "releaseDate": "$(Get-Date -Format 'yyyy-MM-dd')",
  "title": "Bản cập nhật VGCA Sign Tool Pro v2.1.0",
  "description": "Nâng cấp giao diện Fluent UI, tích hợp cơ chế JIT quét chứng thư 10ms, hỗ trợ sửa đổi CCCD trên Web và Auto-Update độc lập.",
  "downloadUrl": "https://github.com/MrKhang-Khoi/cvakysoonline/releases/download/v2.1.0/VGCASignToolPro_Update_v2.1.0.zip",
  "installerUrl": "https://github.com/MrKhang-Khoi/cvakysoonline/releases/download/v2.1.0/VGCASignToolPro_Setup_v2.1.exe",
  "sha256": "$zipHash",
  "installerSha256": "$instHash",
  "mandatory": false,
  "status": "ACTIVE",
  "changelog": [
    "Triệt tiêu hoàn toàn polling timer chạy ngầm, chuyển sang cơ chế quét JIT tức thời khi ký",
    "Mở khóa chỉnh sửa CCCD trên cổng Web đồng thời tự động cập nhật liên thông sang Desktop",
    "Tích hợp hệ thống Auto-Update tự động qua Firebase RTDB và tải tệp từ GitHub Releases",
    "Đóng gói chuẩn Microsoft Windows Per-User Installer không đòi quyền Administrator",
    "Tích hợp thư viện C++ App-Local giải quyết dứt điểm lỗi thiếu VC++ Redistributable"
  ]
}
"@
Write-Host $jsonPayload -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Green
