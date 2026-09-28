# BÁO CÁO THẨM ĐỊNH MÃ NGUỒN CODEX (DEEP LINE-BY-LINE v4.0)

- **Thời gian**: 12:08:12 28/9/2026
- **Trạng thái**: ✅ **[APPROVED] - ĐÃ PHÊ DUYỆT HOÀN TOÀN**
- **Số lượng vi phạm**: 0 lỗi
- **Tóm tắt**: Toàn bộ 34 lát cắt của 1 file đã được phê duyệt 100%.

---

## 🎉 XÁC NHẬN:
Toàn bộ các file mã nguồn và lát cắt dòng code đã được thẩm định chi tiết và phê duyệt 100%.

---
### 📄 NGUYÊN VĂN PHẢN HỒI TỪ CODEX AUDITOR:
```text

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1 - 60 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: MainWindow.MainWindow | Khởi tạo service nội bộ, thiết lập giá trị mặc định chữ ký và gán event handler UI an toàn theo vòng đời Window.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 61 - 120 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: MainWindow_Loaded | Bọc try-catch toàn bộ tác vụ khởi tạo async void, chống crash UI thread, hiển thị thông báo lỗi.
CONTRACT: InitializePreviewEngineAsync | Khởi tạo WebView2 dùng thư mục cache LocalAppData tránh lỗi phân quyền, dò tìm fallback thư mục wwwroot tối đa 4 cấp cha, fail-fast ném lỗi rõ ràng khi thiếu control hoặc asset.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 121 - 180 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: CoreWebView2_WebMessageReceived | Parse an toàn JSON message từ WebView2 qua JsonDocument, validate chặt chẽ schema/type tọa độ tem ký, cập nhật state và đồng bộ UI an toàn.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 181 - 240 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: MainWindow.WebMessageHandling & UpdateStatus & InitializeVgcaAuthRoster | Bóc tách tin nhắn WebView2 an toàn qua JsonElement.TryGetProperty, cập nhật UI qua Dispatcher an toàn luồng, kiểm tra danh bạ giáo viên null-safe trước khi xử lý.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 241 - 300 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: GenerateAndDisplayVgcaQr | Sinh mã QR xác thực VGCA, kiểm tra null ảnh bitmap, bắt lỗi fail-safe qua UpdateStatus.
CONTRACT: OnVgcaLoginStateChanged | Đồng bộ UI qua Dispatcher.Invoke, kiểm tra null teacher an toàn, fallback chuỗi rỗng và hiển thị thông tin chứng thư chính xác.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 301 - 360 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: CmbRosterQuickSelect_SelectionChanged | Đồng bộ UI chứng thư theo thông tin giáo viên, pattern matching an toàn null, chuyển tab fail-safe khi chưa đăng nhập.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 361 - 420 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: BtnVgcaLogin_Click | Xác thực mật khẩu VGCA với fallback chứng thư số phần cứng, validate input fail-closed, null-safe trên kết quả Teacher model.
CONTRACT: BtnRefreshQr_Click | Khởi tạo event handler làm mới QR (ranh giới cắt dòng).

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 421 - 480 / Tổng 1989 dòng] ===
[APPROVED]

CONTRACT: BtnApproveQr_Click | Validate input `TxtVgcaCccd`, kiểm tra null `_vgcaAuth`, xử lý kết quả login với fallback null-coalescing cho UI.
CONTRACT: BtnVgcaLogout_Click | Xác nhận người dùng qua `MessageBox`, kiểm tra null `_vgcaAuth`, reset trạng thái xác thực và tạo lại mã QR.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 481 - 540 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: LoadCertificatesAndVerifyVGCALogin | Tải danh sách chứng thư có private key, binding UI ComboBox, tự động xác thực phần cứng VGCA và xử lý trạng thái đăng nhập an toàn null/fail-closed.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 541 - 600 / Tổng 1989 dòng] ===
[APPROVED]

CONTRACT: MainWindow_Activated | Kích hoạt cửa sổ tự động quét lại chứng thư VGCA khi chưa đăng nhập, bắt ngoại lệ an toàn qua TraceWarning.
CONTRACT: BtnSyncVgcaCertificate_Click | Kích hoạt đồng bộ chứng thư số, kiểm tra null và cờ IsVGCACertificate trước khi hiển thị thông tin UI, xử lý trạng thái lỗi fail-closed.
CONTRACT: BtnUnlockAccount_Click | Khởi tạo trích xuất dữ liệu CCCD từ ô nhập liệu giao diện.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 601 - 660 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: BtnUnlockAccount / BtnRefreshCertificates_Click / BtnLaunchVgcaManager_Click / CmbCertificates_SelectionChanged | Kiểm tra CCCD đầu vào, gọi service xác thực và mở ứng dụng CSP an toàn, binding dữ liệu CertificateModel lên UI qua pattern matching tránh NullReferenceException.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 661 - 720 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: LoadProfiles | Nạp danh sách profile chữ ký vào CmbProfiles, tự động chọn index 0 nếu có dữ liệu, bắt ngoại lệ UI an toàn.
CONTRACT: CmbProfiles_SelectionChanged | Kiểm tra type-safe qua pattern matching `is SignatureProfile`, đồng bộ kích thước/chế độ hiển thị và nạp ảnh con dấu có fallback an toàn.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 721 - 780 / Tổng 1989 dòng] ===
[APPROVED]

CONTRACT: SetAndPersistStampImage | Nạp ảnh con dấu an toàn với `BitmapCacheOption.OnLoad` và `Freeze()`, bắt ngoại lệ giải mã, cập nhật UI và lưu đường dẫn vào profile.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 781 - 840 / Tổng 1989 dòng] ===
[APPROVED]

CONTRACT: ClearStampImage | Reset sạch UI hiển thị con dấu, gán profile.ImagePath = null, lưu cấu hình an toàn qua _profileManager.SaveProfiles kèm try-catch bắt lỗi.
CONTRACT: BtnSelectStampImage_Click | Mở OpenFileDialog lọc định dạng ảnh chuẩn, kích hoạt SetAndPersistStampImage khi ShowDialog thành công.
CONTRACT: BtnTab2ChangeStamp_Click | Chuyển tiếp sự kiện chọn ảnh sang BtnSelectStampImage_Click, tái sử dụng logic không trùng lặp.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 841 - 900 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: MainWindow UI Profile Handlers | BtnClearStampImage_Click, BtnSaveProfile_Click, CmbDisplayMode_SelectionChanged validate dữ liệu UI an toàn, map mode chuẩn xác, bắt ngoại lệ try-catch khi lưu profile qua ProfileManager.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 901 - 960 / Tổng 1989 dòng] ===
[APPROVED]

CONTRACT: BtnGoToSignWorkspace_Click | Kiểm tra trạng thái đăng nhập `_vgcaAuth.IsLoggedIn` trước khi chuyển tab làm việc; chặn điều hướng nếu chưa xác thực.
CONTRACT: BtnSelectWord_Click | Mở hộp thoại chọn tệp Word, reset trạng thái tài liệu cũ, cập nhật trạng thái UI và gọi bất đồng bộ chuyển đổi sang HTML với kiểm tra null an toàn trên WebView2.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 961 - 1020 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: BtnSelectExcel_Click | Reset trạng thái tài liệu cũ, đọc file Excel bất đồng bộ, kiểm tra an toàn WebView2 và khởi tạo nạp viewer HTML đúng chuẩn.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1021 - 1080 / Tổng 1989 dòng] ===
[APPROVED]

CONTRACT: BtnExcelLandscape_Click & Excel WebView2 Loader | Điều hướng an toàn qua TaskCompletionSource kèm timeout 4s, tự hủy đăng ký sự kiện trong finally. DOM polling tối đa 3s. Tham số JS serialize qua JsonSerializer chống injection. Xử lý ngoại lệ đầy đủ, không rò rỉ trạng thái.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1081 - 1140 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: BtnExcelPortrait_Click, BtnSelectPdf_Click | Kiểm tra null CoreWebView2 an toàn, validate tệp PDF tồn tại trước khi nạp, reset state và bắt lỗi UI fail-closed khi nạp PDF thất bại.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1141 - 1200 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: BtnExecuteConvertDocument_Click | Điều hướng convert theo loại file Word/Excel hiện hành, chặn click khi thiếu file nguồn.
CONTRACT: ConvertExcelToPdfAsync | Kiểm tra file nguồn và CoreWebView2, áp cấu hình printSettings A4 vector, xuất PDF qua WebView2 fail-closed khi in thất bại.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1201 - 1260 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: ConvertExcelToPdfAsync (phần cuối) | Đồng bộ trạng thái UI, tải tệp PDF vào viewer, bắt ngoại lệ có ghi log/thông báo người dùng và tắt loading bar ở finally.
CONTRACT: ConvertWordToPdfAsync (phần đầu) | Kiểm tra null converter, kiểm tra tồn tại tệp Word gốc, mở hộp thoại xác nhận lưu thay đổi trước khi chuyển đổi.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1261 - 1320 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: ConvertToPdfAndReadySign | Trích xuất DOM đã chỉnh sửa từ WebView2 qua ExecuteScriptAsync, deserialize an toàn null, chuyển đổi Word sang PDF và nạp PDF viewer đúng quy trình async.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1321 - 1380 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: DisplayPdfInInteractiveViewerAsync | Validate kích thước file <= 30MB, đọc bất đồng bộ File.ReadAllBytesAsync, kiểm tra null-safe WebView2 trước và sau khi đọc file.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1381 - 1440 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: PdfPreviewControl Navigation & Script Readiness | Quản lý vòng đời điều hướng WebView2 an toàn bất đồng bộ, xử lý timeout và hủy đăng ký event handler chống rò rỉ bộ nhớ.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1441 - 1500 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: RollbackViewerUiState | Reset UI preview placeholder, disable stamp insertion, cập nhật status text fail-closed khi nạp PDF WebView2 lỗi.
CONTRACT: BtnInsertSignatureStamp_Click | Validate tệp PDF đích tồn tại, chặn thao tác và cảnh báo người dùng chuyển đổi tệp Word/Excel trước khi đóng dấu.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1501 - 1560 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: PrepareStampDataUrl | Validate stampPath (profile -> desktop fallback -> OpenFileDialog), đọc file async sang Data URL base64, parse TxtDimensions phòng thủ qua TryParse và Math.Max. Kiểm tra null WebView2 trước khi tương tác.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1561 - 1620 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: BtnSignDocument_Click | Kiểm tra tệp PDF tồn tại, xác thực chứng thư số hoạt động, phân giải tọa độ con dấu tương tác và fallback UI an toàn qua TryParse.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1621 - 1680 / Tổng 1989 dòng] ===
[APPROVED]

CONTRACT: PrepareSigningContext | Xác định stampPath từ text/profile/desktop fallback, kiểm tra quyền ghi thư mục xuất file (fallback về Desktop khi ghi thất bại), thiết lập outputPath và khởi tạo SigningRequest.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1681 - 1740 / Tổng 1989 dòng] ===
[APPROVED]

CONTRACT: BtnSignCurrentDocument_Click / DisplayPdfInInteractiveViewerAsync | Khởi tạo request ký số PAdES LTV, khóa nút UI chống race condition, await SignPdfAsync và DisplayPdfInInteractiveViewerAsync an toàn, null-check teacher context, bắt và hiển thị lỗi đầy đủ qua UI/MessageBox.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1741 - 1800 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: BtnSaveSignedCopy_Click, BtnOpenSignedPdfExternal_Click, BtnOpenSignedFolder_Click | Validate null/tồn tại file qua _lastSignedPdfPath trước khi copy/mở process; bắt ngoại lệ I/O và Process đầy đủ, không nuốt lỗi.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1801 - 1860 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: BtnGoToUploadWeb_Click, WebView2 formatting handlers | Chuyển tab UI an toàn. Lệnh định dạng WebView2 dùng null-conditional check, chuỗi lệnh cố định an toàn, bắt ngoại lệ mở explorer đầy đủ.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1861 - 1920 / Tổng 1989 dòng] ===
[APPROVED]

CONTRACT: BtnSelectSignedPdfForUpload_Click | Mở OpenFileDialog chọn PDF, gán path và doc title an toàn.
CONTRACT: BtnExecuteUpload_Click | Validate rỗng và tồn tại file, kiểm tra protocol HTTP/HTTPS, đóng gói UploadRequest, disable button chống spam click, await upload bất đồng bộ.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1921 - 1980 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: UploadResponseHandler | Xử lý phản hồi HTTP, bắt ngoại lệ kết nối, khôi phục `BtnExecuteUpload.IsEnabled` trong `finally`.
CONTRACT: BtnVerifyPdf_Click | Mở OpenFileDialog lọc PDF, gọi `_pdfVerifier.VerifyPdf(pdfPath)` phân tích chữ ký, cập nhật giao diện `ListSignatures.ItemsSource` và thông báo trạng thái.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1981 - 1989 / Tổng 1989 dòng] ===
[APPROVED]
CONTRACT: MainWindow End-Block | Hiển thị thông báo UI khi tài liệu không có chữ ký số. Đóng đầy đủ phạm vi khối lệnh, kết thúc class và namespace chuẩn xác.

```
