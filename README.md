# VGCA Sign Tool Pro (Phiên Bản Nâng Cấp Tích Hợp)

Phần mềm Ký số Văn bản Điện tử Chuẩn PAdES kết hợp Chuyển đổi Tự động Word sang PDF và Cổng Giao tiếp Tải lên Web (Web Gateway).

Được thiết kế và phát triển nâng cấp trực tiếp từ logic cốt lõi của **VGCA Sign Tool (Ban Cơ yếu Chính phủ)**, đáp ứng đầy đủ quy chuẩn văn bản điện tử theo **Nghị định 30/2020/NĐ-CP** và **Nghị định 130/2018/NĐ-CP**.

---

## 🌟 Các Tính Năng Nổi Bật

### 1. Tự Động Chuyển Đổi Word (.doc / .docx) Sang PDF
- Cho phép người dùng chọn trực tiếp tệp tin Word từ máy tính.
- Tự động gọi engine Microsoft Word Interop để chuyển đổi sang PDF với độ chính xác **100% về font chữ (Times New Roman), căn lề trang, tiêu đề và bảng biểu hành chính**.
- Tự động nạp tệp PDF vừa xuất vào khung ký và trình diễn xem trước trực quan.

### 2. Ký Số Văn Bản PDF Chuẩn PAdES LTV (Ban Cơ yếu & Doanh Nghiệp)
- Tự động quét và nạp toàn bộ danh sách chứng thư số hợp lệ từ **Windows Certificate Store** (hỗ trợ cả **USB Token phần cứng** như Viettel-CA, VNPT-CA, FPT-CA, BKAV-CA, VGCA và giải pháp **Remote Signing KSP / Virtual CSP v2.0** của Ban Cơ yếu).
- Tích hợp cầu nối mật mã an toàn phần cứng: Mã băm SHA-256 được chuyển trực tiếp vào chip Secure Element của USB Token để ký, **khóa bí mật (Private Key) không bao giờ bị lộ ra ngoài RAM máy tính**.
- Hỗ trợ đóng **Dấu thời gian chuẩn Quốc gia (TSA)** qua máy chủ Ban Cơ yếu: `http://ca.gov.vn/tsa` (RFC 3161).
- Chế độ **Append Mode** (PAdES Detached): Cho phép nhiều người cùng ký trên một văn bản mà không làm hỏng chữ ký của người ký trước.
- Hỗ trợ chèn ảnh con dấu đỏ cơ quan, chữ ký tay quét và thông tin người ký trực quan trên từng trang.

### 3. Tải Tệp Tin Đã Ký Lên Cổng Thông Tin Web
- Tích hợp module gửi gói tin HTTP/HTTPS `multipart/form-data` chuẩn REST API.
- Cho phép chỉ định URL cổng nhận văn bản (ví dụ hệ thống Quản lý văn bản & Điều hành, Cổng dịch vụ công, Hệ thống LMS trường học).
- Đính kèm đầy đủ metadata: Mã định danh Token, Tiêu đề văn bản, Họ tên người ký, Đơn vị/Phòng ban, Tên file Word gốc, Thời điểm ký.
- Giám sát gói tin và hiển thị phản hồi mạng thật (HTTP Status Code, Độ trễ ms, JSON Response).

### 4. Xác Minh Chữ Ký Số & Tính Toàn Vẹn (Verify)
- Mở và quét mọi chữ ký số có trong tệp PDF.
- Báo cáo rõ ràng: Người ký, Ngày ký, Dấu thời gian, Toàn vẹn (Hợp lệ hay đã bị can thiệp chỉnh sửa sau khi ký).

---

## 📁 Cấu Trúc Mã Nguồn

```
C:\Users\HPZBook\Desktop\VGCASignToolPro\
├── Models\
│   ├── CertificateModel.cs      # Đối tượng quản lý thông tin chứng thư số X.509
│   ├── SignatureProfile.cs      # Mẫu chữ ký (Cá nhân, Cơ quan, Ký nháy, Số, Ngày)
│   ├── SigningRequest.cs        # Tham số cấu hình tiến trình ký số PAdES
│   └── UploadRequest.cs         # Cấu hình gói tin gửi lên Web
├── Services\
│   ├── WordConverterService.cs  # Dịch vụ chuyển đổi Word sang PDF giữ nguyên định dạng
│   ├── CertificateService.cs    # Dịch vụ quét kho chứng thư số Windows Store
│   ├── NetX509ExternalSignature.cs # Cầu nối an toàn giữa USB Token/KSP với iText 9
│   ├── PdfSigningService.cs     # Engine ký số PDF PAdES LTV & TSA
│   ├── PdfVerifierService.cs    # Engine kiểm tra và xác minh chữ ký số
│   ├── ProfileManager.cs        # Quản lý hồ sơ mẫu ký (tương thích signerprofiles.json)
│   └── WebUploadService.cs      # Dịch vụ đẩy file đã ký lên Cổng Web qua HTTPS
├── publish\                     # Thư mục ứng dụng đã đóng gói Release sẵn sàng chạy
│   └── VGCASignToolPro.exe      # File thực thi chính của phần mềm
├── MainWindow.xaml              # Giao diện người dùng WPF chuẩn Windows
├── MainWindow.xaml.cs           # Mã điều khiển giao diện & luồng nghiệp vụ
└── VGCASignToolPro.csproj       # Cấu hình dự án .NET 8 x64
```

---

## 🚀 Hướng Dẫn Sử Dụng

1. Nhấp đúp vào shortcut **`VGCA Sign Tool Pro`** ngoài màn hình Desktop (hoặc mở file `publish\VGCASignToolPro.exe`).
2. **Ký văn bản từ file Word:**
   - Tại Tab *Ký Số & Xử Lý Văn Bản*, nhấn nút **[📄 Chọn File Word (.docx)]**.
   - Phần mềm sẽ tự động mở ngầm Word, chuyển đổi sang PDF chuẩn và hiển thị nội dung trên khung xem trước.
   - Chọn chứng thư số từ danh sách thả xuống (cắm USB Token vào máy nếu dùng token phần cứng).
   - Chọn mẫu chữ ký (Ký cá nhân / Ký cơ quan), chọn ảnh con dấu (nếu có), căn chỉnh trang ký và tọa độ.
   - Nhấn **[✍ TIẾN HÀNH KÝ SỐ TÀI LIỆU]**. Nhập mã PIN Token nếu hệ thống yêu cầu.
3. **Gửi file đã ký lên Web:**
   - Sau khi ký xong, nhấn nút **[🚀 Chuyển Sang Gửi File Lên Web ➡]**.
   - Nhập địa chỉ URL của hệ thống Web tiếp nhận và nhấn **[TIẾN HÀNH GỬI TÀI LIỆU LÊN CỔNG WEB]**.
   - Xem kết quả phản hồi mạng thật (HTTP 200 / JSON) tại khung bên phải.
