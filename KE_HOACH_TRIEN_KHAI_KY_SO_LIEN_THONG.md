# KẾ HOẠCH & LỘ TRÌNH KỸ THUẬT TRIỂN KHAI HỆ THỐNG TRÌNH KÝ SỐ BÁO CÁO LIÊN THÔNG
## ĐƠN VỊ: TRƯỜNG THCS CHU VĂN AN - ĐĂK HÀ, QUẢNG NGÃI
### PHIÊN BẢN V2.0 (ĐÃ ĐIỀU CHỈNH TOÀN DIỆN THEO CHUẨN THẨM ĐỊNH CODEX REASONING ENGINE)

---

## 1. PHẠM VI NGHIỆP VỤ & TẢI TRỌNG THỰC TẾ (DOMAIN CONSTRAINTS)

### 1.1. Phạm vi áp dụng chính xác:
* **Áp dụng Ký liên thông:** CHỈ DÀNH CHO CÁC LOẠI BÁO CÁO HÀNH CHÍNH & CHUYÊN MÔN CỦA TRƯỜNG:
  * Báo cáo sơ kết / tổng kết chuyên môn học kỳ & năm học (Tổ trưởng $\rightarrow$ Ban Giám hiệu).
  * Báo cáo kiểm kê tài sản, cơ sở vật chất, thiết bị dạy học (Cán bộ phụ trách $\rightarrow$ Hiệu phó $\rightarrow$ Hiệu trưởng).
  * Báo cáo đánh giá thi đua, khen thưởng, kỷ luật (Công đoàn / Ban thi đua $\rightarrow$ Hiệu trưởng).
  * Báo cáo phổ cập giáo dục, kiểm định chất lượng giáo dục.
* **KHÔNG áp dụng Ký liên thông:**
  * Giáo án, kế hoạch bài dạy của giáo viên: Ký cá nhân đơn lẻ trên máy tính, lưu trực tiếp, không đưa lên đám mây liên thông.
  * Học bạ học sinh: Quản lý theo phân hệ quản lý điểm/học bạ riêng của ngành.

### 1.2. Phân tích tải trọng thực tế (Workload & Traffic Analysis):
* **Số lượng tệp tin:** Khoảng **30 - 60 báo cáo / học kỳ** (tương đương 5 - 10 báo cáo / tháng).
* **Băng thông & Lưu trữ:** Mỗi báo cáo PDF khoảng $1 - 3\text{ MB}$. Tổng dung lượng 1 năm học không vượt quá $300\text{ MB}$.
* **Kết luận tải trọng:** Nhu cầu thực tế chiếm chưa đến **2%** hạn mức miễn phí của Google Drive (15 GB) và Firebase Spark (1 GB, 50.000 lượt đọc/ngày). Hoàn toàn triệt tiêu nguy cơ nghẽn kết nối hay cạn kiệt dung lượng.

---

## 2. KIẾN TRÚC HỆ THỐNG HỢP NHẤT V2.0 (ALL-IN-ONE SECURE ARCHITECTURE)

Khắc phục hoàn toàn lỗ hổng phân mảnh (Web riêng, App riêng) mà Codex đã cảnh báo, hệ thống phiên bản v2.0 áp dụng mô hình **Hợp nhất Tất-Cả-Trong-Một vào Phần mềm Máy trạm `VGCASignToolPro`**, được bảo vệ bởi cổng đám mây an toàn:

```
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                        TẦNG 1: TRẠM KÝ SỐ MÁY TRẠM HỢP NHẤT                            │
│                 (PHẦN MỀM VGCASignToolPro - .NET 8 WPF x64 TRÊN MÁY GV)                │
│                                                                                        │
│   • Tab 1: Xác thực VGCA Virtual CSP & Quét USB Token (Ban Cơ yếu Chính phủ G2).       │
│   • Tab 2: HỘP THƯ TRÌNH KÝ BÁO CÁO (Thay thế hoàn toàn Web rời):                      │
│            - Mục 📥 Báo Cáo Chờ Tôi Duyệt (Bấm 1-click tự động nạp vào khung ký).      │
│            - Mục 📤 Báo Cáo Tôi Đã Gửi (Hiển thị tiến độ & Nút [🔴 Thu Hồi] thông minh).│
│            - Mục 📁 Kho Báo Cáo Đã Hoàn Tất Toàn Trường (Tra cứu, tải về).            │
│   • Tab 3: Soạn thảo, Chuyển Word/Excel sang PDF, Kéo thả con dấu & Ký số PAdES.       │
│   • Tab 4: Xác minh chữ ký số (Verify & Audit).                                        │
│   • Ký số chuẩn PAdES Append Mode + Đóng dấu thời gian TSA (http://ca.gov.vn/tsa).     │
└───────────────────────────────────┬────────────────────────────────────────────────────┘
                                    │
                                    │ HTTPS API Call (Giao thức an toàn bảo mật)
                                    ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│            TẦNG 2: CỔNG AN TOÀN ĐÁM MÂY (SECURE SERVERLESS CLOUD GATEWAY)              │
│                 (CLOUDFLARE WORKERS API PROXY - GÓI MIỄN PHÍ 100K REQ/NGÀY)            │
│                                                                                        │
│   • Bảo mật Tuyệt đối: Giữ kín Google Service Account credentials.json trên Cloud.    │
│   • Máy trạm giáo viên KHÔNG BAO GIỜ lưu file khóa credentials (triệt tiêu dịch ngược).│
│   • Xác thực định danh giáo viên qua Token trước khi cho phép Đọc / Ghi tệp.           │
└───────────────────────────────────┬────────────────────────────────────────────────────┘
                                    │
                         REST API   │  Google Drive API v3
                                    ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                   TẦNG 3: BỘ LƯU TRỮ VÀ ĐỒNG BỘ TRẠNG THÁI THỜI GIAN THỰC              │
│                                                                                        │
│   1. Google Firebase Realtime Database (Gói Spark 0đ):                                 │
│      - Quản lý trạng thái luồng (flowStatus) và Khóa băm mật mã (baseHash).            │
│      - Bắn tín hiệu chuông báo giữa các máy tính trong vòng < 200ms.                   │
│   2. Google Drive Shared Folder (0đ):                                                  │
│      - Lưu trữ các tệp PDF báo cáo theo phiên bản: File_v1.pdf -> File_v2.pdf.         │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. MÁY TRẠNG THÁI FSM V2.0 & CHỐNG XUNG ĐỘT GHI ĐÈ (ANTI-SPLIT-BRAIN)

### 3.1. Bảng quy đổi trạng thái luồng:

| Trạng Thái | Ý Nghĩa Kỹ Thuật | Phía Người Lập Báo Cáo (GV A) | Phía Người Duyệt Ký (GV B / BGH) |
| :--- | :--- | :--- | :--- |
| **`WAITING_OPEN`** | Báo cáo đã nộp lên mây, GV B chưa nhấp mở. | **NÚT [🔴 THU HỒI] SÁNG**. Được phép rút lại sửa đổi. | Nhận chuông báo tinh tinh: *"Có báo cáo mới cần duyệt"*. |
| **`ACQUIRED_LOCKED`** | GV B vừa nhấp đúp vào báo cáo trên app. | **NÚT THU HỒI BỊ KHÓA MỜ XÁM**. Thông báo: *"Đang được xử lý"*. | Báo cáo tự động tải ngầm về RAM/Temp và mở sẵn khung ký. |
| **`SIGNED_COMMITTED`** | GV B ký xong và nộp thành công bản v2. | Hiển thị con dấu xanh: *"GV B đã duyệt ký"*. | Chuyển quyền tiếp tục cho Hiệu trưởng đóng dấu đỏ. |
| **`RECALLED`** | GV A đã thu hồi thành công trước khi GV B mở. | Báo cáo rút về máy GV A để biên tập lại. | Tự động biến mất khỏi mục chờ duyệt của GV B. |
| **`LOCK_TIMEOUT`** | GV B mở nhưng để quá 60 phút không ký. | **NÚT THU HỒI TỰ ĐỘNG MỞ LẠI** kèm cảnh báo quá hạn. | Cảnh báo: *"Phiên làm việc đã hết hạn. Vui lòng nạp lại"*. |

### 3.2. Cơ chế Chốt chặn Mã băm (Cryptographic Base Hash Check - Khắc phục cảnh báo Codex):
Để loại trừ 100% rủi ro ghi đè khi xảy ra `LOCK_TIMEOUT` (trường hợp GV B quay lại ký trên bản cũ trong máy sau khi GV A đã thu hồi):
1. Khi nạp báo cáo vào máy, phần mềm ghi nhận mã băm gốc: $\text{Hash}_{\text{origin}} = \text{SHA256}(\text{PDF})$.
2. Khi GV B bấm nộp báo cáo đã ký: Phần mềm gửi kèm $\text{Hash}_{\text{origin}}$ lên Cloud Gateway.
3. Gateway đối soát nguyên tử (Atomic Check):
   $$\text{Nếu } \text{Hash}_{\text{origin}} == \text{Firebase.current\_base\_hash} \implies \text{CHẤP NHẬN GHI NỘP}$$
   $$\text{Nếu } \text{Hash}_{\text{origin}} \neq \text{Firebase.current\_base\_hash} \implies \text{TỪ CHỐI GHI, CẢNH BÁO BẢN CŨ ĐÃ BỊ THU HỒI}$$

---

## 4. QUY TRÌNH NGƯỜI DÙNG 2-CLICK LIỀN MẠCH (SEAMLESS UX)

Giáo viên không cần thao tác tải file thủ công, không vào thư mục `Downloads`, toàn bộ diễn ra trong 1 cửa sổ:

* **Bước 1 (Người lập báo cáo - GV A):**
  1. Mở `VGCASignToolPro`, chọn file Word báo cáo (hoặc PDF).
  2. Kéo thả con dấu chữ ký của mình $\rightarrow$ Bấm **[Ký Số]**.
  3. Bấm **[Chuyển Duyệt Báo Cáo]** $\rightarrow$ Chọn tên "Cô Mai (Tổ trưởng)" hoặc "Thầy Hiệu trưởng" $\rightarrow$ Bấm Gửi.
* **Bước 2 (Người duyệt - GV B / Hiệu trưởng):**
  1. Mở `VGCASignToolPro`, app phát chuông *"Tinh Tinh"* báo có văn bản mới trong tab **Hộp Thư Trình Ký**.
  2. Bấm đúp vào tên báo cáo $\rightarrow$ App **tự động kéo file về nạp thẳng vào màn hình xem trước** (chữ ký Thầy A đã có sẵn trên trang).
  3. Cắm USB Token, kéo thả con dấu của mình $\rightarrow$ Bấm **[Ký Số Phê Duyệt]**.
  4. App tự động đẩy bản hoàn thiện lên kho chung và cập nhật trạng thái đã hoàn tất!

---

## 5. LỘ TRÌNH TRIỂN KHAI 3 TUẦN TINH GỌN (REVISED ROADMAP)

* **Tuần 1: Thiết lập Cổng Bảo mật Đám mây & Database**
  * Tác vụ 1.1: Khởi tạo Firebase Realtime Database quản lý danh mục báo cáo và cờ trạng thái `flowStatus`.
  * Tác vụ 1.2: Triển khai 01 Cloudflare Worker API Proxy (miễn phí) để lưu giữ an toàn Service Account Google Drive, cung cấp API tải/đẩy file cho phần mềm máy trạm.
* **Tuần 2: Nâng cấp Giao diện & Động cơ `VGCASignToolPro`**
  * Tác vụ 2.1: Bổ sung Tab **"Hộp Thư Trình Ký Báo Cáo"** (Inbox/Outbox) trong `MainWindow.xaml`.
  * Tác vụ 2.2: Tích hợp module đồng bộ tự động `ReportWorkflowService.cs` kết nối với Gateway.
  * Tác vụ 2.3: Kích hoạt tham số Dấu thời gian TSA Ban Cơ yếu (`http://ca.gov.vn/tsa`) và `StampingProperties().UseAppendMode()`.
* **Tuần 3: Kiểm thử Chuyên sâu & Chuyển giao Thực tế**
  * Tác vụ 3.1: Kiểm thử kịch bản thu hồi khi chưa mở và chặn thu hồi khi đã mở.
  * Tác vụ 3.2: Kiểm thử bẫy mã băm SHA-256 chống ghi đè khi hết hạn 60 phút.
  * Tác vụ 3.3: Hướng dẫn nhanh cho các Tổ trưởng chuyên môn và Ban Giám hiệu sử dụng.

---

## 6. ĐÁP ỨNG TIÊU CHUẨN PHÁP LÝ & AN TOÀN

1. **Nghị định 130/2018/NĐ-CP:** Chữ ký số thực hiện hoàn toàn trên chip phần cứng của USB Token / KSP nội bộ, không đưa khóa bí mật ra ngoài mạng. Kiểm tra tính hợp lệ chứng thư số tại thời điểm ký.
2. **Nghị định 30/2020/NĐ-CP:** Định dạng xuất ra là PDF chuẩn quốc tế, hình ảnh con dấu đỏ cơ quan đúng kích thước tỷ lệ thực, có dấu thời gian TSA chứng thực thời điểm ký.
3. **Nghị định 13/2023/NĐ-CP (Bảo vệ dữ liệu):** Báo cáo hành chính không lưu tràn lan, toàn bộ tệp tin được phân quyền đọc/ghi nghiêm ngặt qua cổng Gateway có xác thực danh tính.
