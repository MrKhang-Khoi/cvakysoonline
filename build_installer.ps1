# ==============================================================================
# SCRIPT DONG GOI BO CAI DAT & GOI UPDATE TU DONG CHUAN MICROSOFT WINDOWS
# Project: VGCA Sign Tool Pro (v2.5.0)
# ==============================================================================

$ErrorActionPreference = "Stop"
$appVersion = "2.5.0"
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

# 2. PUBLISH STANDALONE UPDATER TO STAGING DIRECTORY
Write-Host "`n[2/6] Publishing Standalone Updater (VGCASignToolPro.Updater)..." -ForegroundColor Yellow
$updaterStagingDir = Join-Path $projectDir "dist\updater_staging"
if (Test-Path $updaterStagingDir) { Remove-Item -Path $updaterStagingDir -Recurse -Force }
$updaterCsproj = Join-Path $projectDir "Updater\VGCASignToolPro.Updater.csproj"
dotnet publish $updaterCsproj -c Release -r win-x64 --self-contained true -o $updaterStagingDir

# 3. PUBLISH MAIN APP (SELF-CONTAINED WIN-X64)
Write-Host "`n[3/6] Publishing Main App (.NET 8 Self-Contained win-x64)..." -ForegroundColor Yellow
$mainCsproj = Join-Path $projectDir "VGCASignToolPro.csproj"
dotnet publish $mainCsproj -c Release -r win-x64 --self-contained true -o $distDir

# Copy ONLY Updater binaries into dist to guarantee ZERO overwrite of shared runtime DLLs
Write-Host "  + Integrating Updater binaries into Main App distribution..." -ForegroundColor Green
Get-ChildItem -Path $updaterStagingDir -Filter "VGCASignToolPro.Updater.*" | Copy-Item -Destination $distDir -Force
if (Test-Path $updaterStagingDir) { Remove-Item -Path $updaterStagingDir -Recurse -Force }

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

# Zero-Roster Architecture: Dam bao tuyet doi khong ton tai school_roster trong dist
Get-ChildItem -Path $distDir -Filter "*school_roster*" -Force -ErrorAction SilentlyContinue | Remove-Item -Force

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

$installerExe = Join-Path $outputInstallerDir "VGCASignToolPro_Setup_v$appVersion.exe"
if (-not (Test-Path $installerExe)) {
    throw "Error: Installer output file was not found after compilation: $installerExe"
}

$instSizeMb = [math]::Round(((Get-Item $installerExe).Length / 1MB), 2)
$instHash = (Get-FileHash -Path $installerExe -Algorithm SHA256).Hash

# 6. BUILD AUTO-UPDATE ZIP PACKAGE (EXCLUDING SENSITIVE USER DATA)
Write-Host "`n[6/6] Building Auto-Update Package (ZIP for UpdateService)..." -ForegroundColor Yellow
$updateZipPath = Join-Path $outputInstallerDir "VGCASignToolPro_Update_v$appVersion.zip"
if (Test-Path $updateZipPath) {
    Remove-Item -Path $updateZipPath -Force
}

$tempZipDir = Join-Path ([System.IO.Path]::GetTempPath()) ("VGCASignToolPro_ZipStaging_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempZipDir -Force | Out-Null

try {
    # Sao chép toàn bộ tệp tin phân phối Self-Contained x64 vào gói cập nhật,
    # bảo vệ tuyệt đối dữ liệu người dùng (danh bạ nhà trường, cấu hình ký số, cache).
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

                # Tạo thêm bản sao dự phòng .new.exe và .new.dll để tương thích 100% với các phiên bản cũ
                if ($_.Name -ieq "VGCASignToolPro.Updater.exe") {
                    $newExePath = Join-Path $destParent "VGCASignToolPro.Updater.new.exe"
                    Copy-Item -Path $full -Destination $newExePath -Force
                } elseif ($_.Name -ieq "VGCASignToolPro.Updater.dll") {
                    $newDllPath = Join-Path $destParent "VGCASignToolPro.Updater.new.dll"
                    Copy-Item -Path $full -Destination $newDllPath -Force
                }
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
$releaseObj = @{
    version = $appVersion
    minSupportedVersion = "2.0.0"
    releaseDate = (Get-Date -Format "yyyy-MM-dd")
    title = "Ban cap nhat VGCA Sign Tool Pro v$appVersion"
    description = "Chuan hoa toan dien giao dien nguoi dung va cau tu UX theo he quy chieu Microsoft Fluent Design 2, Apple HIG va Google Material Design 3."
    downloadUrl = "https://github.com/MrKhang-Khoi/cvakysoonline/releases/download/v$appVersion/VGCASignToolPro_Update_v$appVersion.zip"
    installerUrl = "https://github.com/MrKhang-Khoi/cvakysoonline/releases/download/v$appVersion/VGCASignToolPro_Setup_v$appVersion.exe"
    sha256 = $zipHash
    installerSha256 = $instHash
    mandatory = $false
    status = "ACTIVE"
    changelog = @(
        "Chuan hoa toan bo typography va cau tu sang Sentence case theo Microsoft Writing Style Guide",
        "Thay the toan bo Unicode Emoji mau bang he thong Segoe MDL2 Assets vector glyphs sac net cao cap",
        "Toi uu hoa he thong phan cap hanh dong (Action Hierarchy) va bang mau thanh cong cu Toolbar",
        "Loai bo danh so thu tu tab roi rac, tinh gon nhan dieu huong chuan Fluent Design",
        "Bao toan 100% tinh nang ky so PAdES LTV, hop thu trinh ky SSE va dong bo danh ba"
    )
}
$jsonPayload = $releaseObj | ConvertTo-Json -Depth 5
Write-Host $jsonPayload -ForegroundColor Green

# Tu dong dong bo metadata len Firebase Realtime Database bang byte UTF-8
Write-Host "`nDang dong bo metadata cap nhat len Firebase Realtime Database (UTF-8 Bytes)..." -ForegroundColor Yellow
$firebaseEndpoint = "https://cva-kyso-lienthong-default-rtdb.asia-southeast1.firebasedatabase.app/app_release.json?auth=TOdsGaw1iflR0dCDtHqQ8pBii9UgiqLaq8nfF250"
try {
    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($jsonPayload)
    $null = Invoke-RestMethod -Uri $firebaseEndpoint -Method Put -Body $bodyBytes -ContentType "application/json; charset=utf-8"
    Write-Host "[OK] Da dong bo thanh cong metadata v$appVersion (UTF-8 chuan) len Firebase Control Plane!" -ForegroundColor Green
}
catch {
    Write-Host "[WARN] Khong the tu dong dong bo Firebase: $_" -ForegroundColor Yellow
}

# 4. TU DONG DANG TAI LEN GITHUB RELEASES (NEU CO GH CLI VA CREDENTIAL)
Write-Host "`nDang kiem tra va dong bo Release len GitHub Releases..." -ForegroundColor Yellow
try {
    $cred = echo "protocol=https`nhost=github.com" | git credential fill
    $token = ($cred | Select-String "password=").Line.Replace("password=", "").Trim()
    if ($token) {
        $env:GH_TOKEN = $token
        $tag = "v$($releaseObj.version)"
        $notes = "- " + ($releaseObj.changelog -join "`n- ")
        
        $relExists = gh release view $tag -R MrKhang-Khoi/cvakysoonline 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  + Release $tag da ton tai tren GitHub, dong bo upload/ghi de assets..." -ForegroundColor Cyan
            gh release upload $tag $installerExe $updateZipPath --clobber -R MrKhang-Khoi/cvakysoonline
        } else {
            Write-Host "  + Tao moi Release $tag va upload assets..." -ForegroundColor Cyan
            gh release create $tag $installerExe $updateZipPath --title $releaseObj.title --notes $notes -R MrKhang-Khoi/cvakysoonline
        }
        Write-Host "[OK] Da phat hanh tron goi thanh cong $tag len GitHub Releases!" -ForegroundColor Green
    }
} catch {
    Write-Host "[WARN] Khong the tu dong phat hanh GitHub Release: $_" -ForegroundColor Yellow
}

Write-Host "=================================================================" -ForegroundColor Green
