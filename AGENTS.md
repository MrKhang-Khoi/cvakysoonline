# VGCASignToolPro — Project Architecture & Release Protocol

Hướng dẫn bắt buộc dành cho Trợ lý Hermes Agent khi làm việc, sửa lỗi, và cập nhật dự án `VGCASignToolPro`.

## 1. Tổng quan Kiến trúc Dự án
- **Công nghệ**: .NET 8.0 Windows Desktop (WPF, C#, XAML), PAdES Digital Signing, VGCA USB Token PKI CSP.
- **Hệ thống Điều khiển (Control Plane)**: Firebase Realtime Database
  - URL: `https://cva-kyso-lienthong-default-rtdb.asia-southeast1.firebasedatabase.app`
  - Node phiên bản: `/app_release.json`
- **Máy chủ Lưu trữ Cập nhật (Distribution Plane)**: GitHub Releases
  - Repository: `https://github.com/MrKhang-Khoi/cvakysoonline`
- **Cơ chế Tự cập nhật OTA (`Services/UpdateService.cs`)**:
  - Ứng dụng khách gửi GET request lên `{firebaseUrl}/app_release.json`.
  - Nếu `version` trên Firebase > `CurrentVersionString`, ứng dụng sẽ tự động tải file zip từ `downloadUrl` (GitHub Releases) và khởi chạy `VGCASignToolPro.Updater.exe` để cập nhật.

---

## 2. Quy trình Bắt buộc khi Sửa lỗi và Cập nhật (Mandatory 8-Step Closed-Loop Release)

CẤM TUYỆT ĐỐI dừng lại ở việc tạo file zip trên đĩa cứng cục bộ rồi báo hoàn thành. Quy trình phát hành chỉ được coi là hoàn tất (Status: DONE) khi trải qua đủ 8 bước sau:

### Bước 1: Sửa lỗi Tinh chỉnh (Surgical Fix)
- Chỉ sửa đúng file, đúng hàm gây ra lỗi.
- Áp dụng CodeGraph (`codegraph callers <symbol>`, `codegraph impact <symbol>`) để kiểm tra tầm ảnh hưởng trước khi sửa.

### Bước 2: Biên dịch & Kiểm thử (Build & Verification)
- Chạy lệnh build Release:
  ```powershell
  dotnet build -c Release
  ```
  Bắt buộc đạt **0 errors, 0 warnings**.

### Bước 3: Nâng Phiên bản (Semantic Version Bump)
- Đồng bộ số phiên bản mới (ví dụ `2.4.0`) tại các vị trí:
  - `VGCASignToolPro.csproj`: `<Version>2.4.0</Version>`, `<AssemblyVersion>2.4.0.0</AssemblyVersion>`
  - `Services/UpdateService.cs`: `public string CurrentVersionString { get; private set; } = "2.4.0";`

### Bước 4: Đóng gói Nhị phân & Tính Checksum SHA-256
- Tạo gói cập nhật đè (chỉ chứa các dll/exe đã sửa) tại `releases/VGCASignToolPro_Update_v<version>.zip`.
- Tạo gói Portable đầy đủ tại `releases/VGCASignToolPro_v<version>_Standalone_Portable.zip`.
- Tính mã băm SHA-256 của file zip và cập nhật file `releases/app_release.json`:
  ```json
  {
    "version": "2.4.0",
    "versionCode": 240,
    "status": "ACTIVE",
    "mandatory": false,
    "title": "Bản cập nhật v2.4.0",
    "releaseDate": "YYYY-MM-DD",
    "changelog": [...],
    "downloadUrl": "https://github.com/MrKhang-Khoi/cvakysoonline/releases/download/v2.4.0/VGCASignToolPro_Update_v2.4.0.zip",
    "sha256": "<MÃ_SHA256_IN_HOA>"
  }
  ```

### Bước 5: Đồng bộ Mã nguồn Git (Git Push)
- Kiểm tra `git status`.
- Commit theo chuẩn Conventional Commits:
  ```powershell
  git add .
  git commit -m "fix/release: sua loi va cap nhat phien ban v<version>"
  git push origin main
  ```

### Bước 6: Phát hành GitHub Release (GitHub Release Publication)
- Kiểm tra đăng nhập GitHub CLI:
  ```powershell
  gh auth status
  ```
  - Nếu đã đăng nhập: Tạo Release và đính kèm gói update zip:
    ```powershell
    gh release create v<version> "releases/VGCASignToolPro_Update_v<version>.zip" --title "VGCA Sign Tool Pro v<version>" --notes-file "releases/notes.md"
    ```
  - Nếu chưa đăng nhập: **BÁO NGAY CHO NGƯỜI DÙNG** kèm hướng dẫn chạy `gh auth login`, không được tự ý bỏ qua bước này.

### Bước 7: Đồng bộ Metadata lên Firebase RTDB (OTA Control Plane Sync)
- Đẩy dữ liệu `app_release.json` lên Firebase qua REST API để các máy đang cài ứng dụng cũ phát hiện bản cập nhật:
  ```powershell
  $body = Get-Content -Raw -Encoding UTF8 "releases/app_release.json"
  Invoke-RestMethod -Uri "https://cva-kyso-lienthong-default-rtdb.asia-southeast1.firebasedatabase.app/app_release.json?auth=TOdsGaw1iflR0dCDtHqQ8pBii9UgiqLaq8nfF250" -Method Put -Body $body -ContentType "application/json; charset=utf-8"
  ```

### Bước 8: Đo đạc & Nghiệm thu Thực tế (Empirical Verification)
- Query lại Firebase RTDB và GitHub Releases xem bản mới đã sẵn sàng chưa (phải trả về mã HTTP 200 và phiên bản mới).
- Nếu người dùng cần kiểm tra nóng ngay trên máy cục bộ, hỗ trợ copy đè các file từ `bin/Release/net8.0-windows/` vào thư mục cài đặt `C:\Users\HPZBook\AppData\Local\Programs\VGCASignToolPro\`.
