<#
.SYNOPSIS
    Script đóng gói bản cập nhật chuẩn hóa và bản chạy độc lập (Standalone) cho VGCASignToolPro.
.DESCRIPTION
    - Đóng gói bản Auto-Update ZIP (loại trừ dữ liệu cá nhân cục bộ).
    - Đóng gói bản Standalone Portable (chứa sẵn .NET 8 Runtime để copy sang máy tính khác).
    - Tự động tính toán mã băm SHA-256 đối soát toàn vẹn.
    - Xuất file cấu hình app_release.json đồng bộ với Firebase Control Plane.
.PARAMETER Version
    Phiên bản phát hành (mặc định: "2.1.0").
.PARAMETER BuildStandalone
    Tùy chọn tạo thêm gói Standalone độc lập (.NET 8 nhúng sẵn).
#>

param(
    [string]$Version = "2.1.0",
    [switch]$BuildStandalone = $true,
    [string]$DownloadUrl = "https://github.com/MrKhang-Khoi/cvakysoonline/releases/download/v2.1.0/VGCASignToolPro_Update_v2.1.0.zip",
    [string]$FallbackDownloadUrl = "https://script.google.com/macros/s/AKfycbyCVA-KySo-AutoUpdate/exec"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "   HE THONG DONG GOI CAP NHAT CHUAN HOA - VGCASignToolPro v$Version" -ForegroundColor Yellow
Write-Host "==================================================================" -ForegroundColor Cyan

$releasesDir = Join-Path $scriptDir "releases"
if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null
}

$tempPackDir = Join-Path $scriptDir "temp_pack"
if (Test-Path $tempPackDir) {
    Remove-Item -Path $tempPackDir -Recurse -Force
}

# -------------------------------------------------------------
# 1. BIÊN DỊCH VÀ XUẤT BẢN GÓI AUTO-UPDATE (FRAMEWORK-DEPENDENT)
# -------------------------------------------------------------
Write-Host "`n[1/4] Bien dich va xuat ban ban Auto-Update..." -ForegroundColor Green
$updatePublishDir = Join-Path $tempPackDir "publish_update"

& dotnet publish "$scriptDir\VGCASignToolPro.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $updatePublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Bien dich goi Auto-Update that bai!"
    exit 1
}

# Loại trừ các file dữ liệu cá nhân cục bộ nhạy cảm trước khi nén
$sensitiveFiles = @(
    "school_roster.json",
    "school_roster.json.hmac",
    "saved_account.json",
    "*.user",
    "*.log"
)
foreach ($pattern in $sensitiveFiles) {
    Get-ChildItem -Path $updatePublishDir -Filter $pattern -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
}

# Đóng gói ZIP Auto-Update
$updateZipName = "VGCASignToolPro_Update_v$Version.zip"
$updateZipPath = Join-Path $releasesDir $updateZipName
if (Test-Path $updateZipPath) {
    Remove-Item -Path $updateZipPath -Force
}

Write-Host "[2/4] Dang nen goi Auto-Update thanh $updateZipName..." -ForegroundColor Green
Compress-Archive -Path "$updatePublishDir\*" -DestinationPath $updateZipPath -CompressionLevel Optimal

$updateHashObj = Get-FileHash -Path $updateZipPath -Algorithm SHA256
$updateSha256 = $updateHashObj.Hash
$updateSizeMb = [math]::Round((Get-Item $updateZipPath).Length / 1MB, 2)

Write-Host "   => File ZIP: $updateZipName" -ForegroundColor White
Write-Host "   => Dung luong: $updateSizeMb MB" -ForegroundColor White
Write-Host "   => SHA-256: $updateSha256" -ForegroundColor Yellow

# -------------------------------------------------------------
# 2. BIÊN DỊCH VÀ ĐÓNG GÓI BẢN CHẠY ĐỘC LẬP (STANDALONE PORTABLE)
# -------------------------------------------------------------
$standaloneZipName = ""
$standaloneSha256 = ""
$standaloneSizeMb = 0

if ($BuildStandalone) {
    Write-Host "`n[3/4] Bien dich ban Standalone Portable (chua san .NET 8 Runtime)..." -ForegroundColor Green
    $standalonePublishDir = Join-Path $tempPackDir "publish_standalone"

    & dotnet publish "$scriptDir\VGCASignToolPro.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -o $standalonePublishDir

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Bien dich ban Standalone that bai!"
        exit 1
    }

    # Loại bỏ file nhạy cảm
    foreach ($pattern in $sensitiveFiles) {
        Get-ChildItem -Path $standalonePublishDir -Filter $pattern -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
    }

    $standaloneZipName = "VGCASignToolPro_v${Version}_Standalone_Portable.zip"
    $standaloneZipPath = Join-Path $releasesDir $standaloneZipName
    if (Test-Path $standaloneZipPath) {
        Remove-Item -Path $standaloneZipPath -Force
    }

    Write-Host "Dang nen goi Standalone thanh $standaloneZipName..." -ForegroundColor Green
    Compress-Archive -Path "$standalonePublishDir\*" -DestinationPath $standaloneZipPath -CompressionLevel Optimal

    $standaloneHashObj = Get-FileHash -Path $standaloneZipPath -Algorithm SHA256
    $standaloneSha256 = $standaloneHashObj.Hash
    $standaloneSizeMb = [math]::Round((Get-Item $standaloneZipPath).Length / 1MB, 2)

    Write-Host "   => File ZIP: $standaloneZipName" -ForegroundColor White
    Write-Host "   => Dung luong: $standaloneSizeMb MB" -ForegroundColor White
    Write-Host "   => SHA-256: $standaloneSha256" -ForegroundColor Yellow
}

# -------------------------------------------------------------
# 3. TẠO TỆP JSON ĐỒNG BỘ CHO FIREBASE CONTROL PLANE
# -------------------------------------------------------------
Write-Host "`n[4/4] Tao cau hinh app_release.json cho Firebase Control Plane..." -ForegroundColor Green

$releaseDateStr = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
$vParts = $Version.Split('.')
$vCode = 200
if ($vParts.Length -ge 3) {
    $vCode = ([int]$vParts[0] * 100) + ([int]$vParts[1] * 10) + [int]$vParts[2]
} elseif ($vParts.Length -ge 2) {
    $vCode = ([int]$vParts[0] * 100) + ([int]$vParts[1] * 10)
}

$appReleaseJson = @{
    version = $Version
    versionCode = $vCode
    releaseDate = $releaseDateStr
    title = "Ban Nang Cap He Thong Chuan Windows v$Version"
    status = "ACTIVE"
    mandatory = $false
    minSupportedVersion = "2.0.0"
    downloadUrl = $DownloadUrl
    fallbackDownloadUrl = $FallbackDownloadUrl
    sha256 = $updateSha256
    packageSizeMb = $updateSizeMb
    changelog = @(
        "Bo sung trinh nang cap VGCASignToolPro.Updater.exe vao bo phat hanh",
        "Co che tai kep Dual-Source tu dong chuyen huong khi mat ket noi",
        "Tu dong phat hien va huong dan cai dat WebView2 Runtime tren Windows 10 cu",
        "Ho tro ban phat hanh doc lap Standalone khong can cai san .NET 8 Runtime"
    )
}

$jsonOutput = $appReleaseJson | ConvertTo-Json -Depth 5
$jsonFilePath = Join-Path $releasesDir "app_release.json"
[System.IO.File]::WriteAllText($jsonFilePath, $jsonOutput, [System.Text.Encoding]::UTF8)

# Dọn dẹp thư mục tạm
if (Test-Path $tempPackDir) {
    Remove-Item -Path $tempPackDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n==================================================================" -ForegroundColor Cyan
Write-Host "   HOAN TAT DONG GOI THANH CONG!" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "Thu muc xuat ban: $releasesDir" -ForegroundColor White
Write-Host "File Auto-Update: $updateZipName ($updateSizeMb MB)" -ForegroundColor White
if ($BuildStandalone) {
    Write-Host "File Standalone : $standaloneZipName ($standaloneSizeMb MB)" -ForegroundColor White
}
Write-Host "File Firebase   : app_release.json" -ForegroundColor White
Write-Host "`nNoi dung JSON san sang dong bo len Firebase:" -ForegroundColor Yellow
Write-Host $jsonOutput -ForegroundColor Cyan
