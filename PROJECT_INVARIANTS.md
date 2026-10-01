# BẢN KHẾ ƯỚC BẤT BIẾN DỰ ÁN (PROJECT INVARIANTS CONTRACT)
## VGCASignToolPro - Hệ Thống Ký Số Điện Tử Ban Cơ Yếu Chính Phủ

> **NGUYÊN TẮC BẤT DI BẤT DỊCH**: Bất kỳ AI hay lập trình viên nào khi chỉnh sửa mã nguồn của dự án này BẮT BUỘC phải tuân thủ nghiêm ngặt các ranh giới bất biến dưới đây. Tuyệt đối không được phép phá vỡ các tính năng đã ổn định.

---

### ĐIỀU 1: RANH GIỚI HÌNH 2 - KHUNG ĐỊNH VỊ CHỮ KÝ (STAMP BADGE INVARIANT)
1. **Mục tiêu**: Ẩn thanh tiêu đề xanh (`.stamp-info-bar` hiển thị `Trang X • X:..., Y:... • WxH`) để người dùng nhìn rõ 100% dòng chữ bên dưới (như "NGƯỜI SOẠN", "HIỆU TRƯỞNG") khi căn chỉnh chữ ký.
2. **Ranh giới bất biến (CẤM VI PHẠM)**:
   - **CHỈ ĐƯỢC PHÉP CAN THIỆP TẦNG THỊ GIÁC (CSS)**: Ẩn `.stamp-info-bar` bằng CSS (`display: none !important;`).
   - **TUYỆT ĐỐI KHÔNG XÓA HOẶC LÀM HỎNG TẦNG LOGIC**:
     - Hàm tính toán tọa độ `calculateAndDispatchCoordinates()` phải tiếp tục chạy 100% khi người dùng kéo thả hoặc co giãn.
     - Luồng gửi thông điệp `postMessage({ type: 'stampCoordinatesChanged', coords: coords })` về C# WPF phải được bảo toàn nguyên vẹn.
     - Bốn núm co giãn góc (`.stamp-resize-handle`), sự kiện kéo thả (`setupStampDragAndResize`), và nút xóa đỏ `(X)` (`.stamp-close-btn`) phải giữ nguyên 100% hoạt động.
   - Đồng bộ đồng thời cả 2 file: `wwwroot/pdf_viewer.html` và `publish/wwwroot/pdf_viewer.html`.

---

### ĐIỀU 2: RANH GIỚI HÌNH 1 - DANH SÁCH BÁO CÁO TAB 3 (OUTBOX REPORT INVARIANT)
1. **Mục tiêu**: Loại bỏ hiện tượng lag / đơ máy khi nhấp chọn hoặc xem lại báo cáo đã gửi ở Tab 3.
2. **Ranh giới bất biến (CẤM VI PHẠM)**:
   - Khi bấm xem báo cáo, bắt buộc hiển thị `ShowLoadingOverlay` ngay lập tức để người dùng biết hệ thống đang xử lý, khóa nút tránh double-click.
   - **CẤM GỌI `SanitizeSyncfusionWatermark`** trên các tệp PDF đã tải về trong `DownloadFileAsync` (vì đây là tệp đã ký số hợp lệ của Ban Cơ Yếu, việc quét lại bằng iText sẽ phá hỏng chữ ký số và gây nghẽn CPU).
   - Tận dụng bộ nhớ đệm (Cache) tệp cục bộ trên ổ đĩa nếu snapshot báo cáo đã có sẵn.
   - Tuyệt đối không can thiệp làm thay đổi logic ký duyệt ở Tab 1 hoặc logic xác minh chữ ký ở Tab 4.

---

### ĐIỀU 3: RANH GIỚI CHUYỂN ĐỔI WORD SANG PDF (WORD CONVERTER INVARIANT)
1. **Mục tiêu**: Tối ưu tốc độ mở và xuất file Word sang PDF chuẩn A4 Nghị định 30/2020/NĐ-CP (nhanh như Python ~300ms - 600ms).
2. **Ranh giới bất biến (CẤM VI PHẠM)**:
   - **Áp dụng Strategy Pattern kèm phao cứu sinh Fail-Safe**:
     - Nhánh 1 (Tối ưu): Thử dùng cơ chế Word COM Warm/Fast Instance.
     - Nhánh 2 (Fallback): Nếu máy người dùng KHÔNG CÓ Office hoặc gặp BẤT KỲ lỗi gì, hệ thống tự động rơi về (Fallback) đúng 100% logic Syncfusion DocIO cũ.
   - **CẤM XÓA BỎ BỘ CHUYỂN ĐỔI SYNCFUSION CŨ**: Vì nó là chiếc phao cứu sinh đảm bảo phần mềm chạy được trên cả những máy tính không cài đặt Microsoft Word.

---

### ĐIỀU 4: BẢO VỆ CÁC HÀM DÙNG CHUNG CỐT LÕI (CORE SHARED FUNCTIONS)
1. Tuyệt đối không tự ý thay đổi chữ ký (signature) hoặc hành vi của các hàm dùng chung:
   - `LoadPdfDocumentAsync(string pdfPath)`
   - `DisplayPdfInInteractiveViewerAsync(string pdfPath)`
   - `SignPdf(SigningRequest request)`
   - `NetX509ExternalSignature`
2. Mọi tính năng tối ưu cục bộ phải được đóng gói gọn trong phạm vi ngữ cảnh gọi của nó, không làm ảnh hưởng các luồng nghiệp vụ khác.

---

### ĐIỀU 5: QUY TRÌNH KIỂM SOÁT 5 BƯỚC TRƯỚC KHI BÀN GIAO (MANDATORY GATEKEEPER)
Mỗi bước sửa đổi bắt buộc phải vượt qua:
1. `dotnet build -c Release`: Đạt `0 Warning(s)`, `0 Error(s)`.
2. Kiểm thử F12 DevTools và DOM Browser: 0 lỗi Console Exception.
3. Vượt qua bộ lọc `Alibaba Open-Code-Review Pre-Commit Hook` (không var, không lỏng `==`, không nuốt lỗi `catch`, không mock data).
4. Tạo mốc Git Commit riêng biệt đóng băng từng tính năng trước khi sang tính năng kế tiếp.
