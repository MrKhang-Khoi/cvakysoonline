# BÁO CÁO THẨM ĐỊNH MÃ NGUỒN CODEX (DEEP LINE-BY-LINE v4.0)

- **Thời gian**: 11:37:05 28/9/2026
- **Trạng thái**: ✅ **[APPROVED] - ĐÃ PHÊ DUYỆT HOÀN TOÀN**
- **Số lượng vi phạm**: 0 lỗi
- **Tóm tắt**: Toàn bộ 32 lát cắt của 1 file đã được phê duyệt 100%.

---

## 🎉 XÁC NHẬN:
Toàn bộ các file mã nguồn và lát cắt dòng code đã được thẩm định chi tiết và phê duyệt 100%.

---
### 📄 NGUYÊN VĂN PHẢN HỒI TỪ CODEX AUDITOR:
```text

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1 - 60 / Tổng 1919 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 61 - 120 / Tổng 1919 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 121 - 180 / Tổng 1919 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 181 - 240 / Tổng 1919 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 241 - 300 / Tổng 1919 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 301 - 360 / Tổng 1919 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 361 - 420 / Tổng 1919 dòng] ===
[APPROVED]

CONTRACT: BtnVgcaLogin_Click | Validate input CCCD/password, authenticate via VGCA service, fallback check hardware cert with private key an toàn, unlock account khi CCCD khớp, cập nhật UI state đầy đủ.
CONTRACT: BtnRefreshQr_Click | Trigger tạo và render lại VGCA QR code.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 421 - 480 / Tổng 1919 dòng] ===
[APPROVED]

CONTRACT: BtnApproveQr_Click | Validate CCCD rỗng, check null `_vgcaAuth`, kiểm tra `result.Success` và null-safe `Teacher` properties, hiển thị lỗi fail-closed.
CONTRACT: BtnVgcaLogout_Click | Xác nhận qua MessageBox, check null `_vgcaAuth`, gọi logout và reset QR code an toàn.
CONTRACT: BtnGoToLoginTab_Click | Khởi tạo handler chuyển tab, tuân thủ slicing continuity invariant.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 481 - 540 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: LoadCertificatesAndVerifyVGCALogin | Tải chứng thư số khóa riêng, cập nhật ItemsSource ComboBox, map thuộc tính UI có null-coalescing, xác thực VGCA fail-closed theo trạng thái chứng thư.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 541 - 600 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: MainWindow_Activated | Kiểm tra `_vgcaAuth.IsLoggedIn` trước khi quét lại chứng thư, bắt và ghi log ngoại lệ, tránh crash UI thread khi kích hoạt cửa sổ.
CONTRACT: BtnSyncVgcaCertificate_Click | Đồng bộ chứng thư VGCA, kiểm tra an toàn `_currentActiveCert` null/valid trước khi hiển thị chi tiết, cập nhật UI trạng thái chính xác.
CONTRACT: BtnUnlockAccount_Click | Validate đầu vào CCCD tránh chuỗi rỗng trước khi xử lý mở khóa tài khoản.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 601 - 660 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: BtnRefreshCertificates_Click, BtnLaunchVgcaManager_Click, CmbCertificates_SelectionChanged | Xử lý sự kiện UI đồng bộ chứng thư và mở khóa tài khoản an toàn. Pattern matching `is CertificateModel` chống null dereference. Không nuốt lỗi, logic UI hợp lệ.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 661 - 720 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: LoadProfiles | Nạp danh sách profile vào ItemsSource an toàn, bắt ngoại lệ toàn diện, cập nhật Trace và Status khi lỗi.
CONTRACT: CmbProfiles_SelectionChanged | Kiểm tra kiểu an toàn qua pattern matching, validate đường dẫn file trước khi gán ảnh, tự động fallback và dọn dẹp state khi file không tồn tại.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 721 - 780 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: SetAndPersistStampImage | Nạp BitmapImage giải phóng file lock qua Freeze/BitmapCacheOption.OnLoad, bắt ngoại lệ decode ảnh, đồng bộ UI và persist profile an toàn.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 781 - 840 / Tổng 1919 dòng] ===
[APPROVED]

CONTRACT: ClearStampImage, BtnSelectStampImage_Click, BtnTab2ChangeStamp_Click | Dọn dẹp trạng thái UI con dấu an toàn, cập nhật profile và gọi hộp thoại chọn tệp ảnh chuẩn xác. Không leak tài nguyên, bắt và ghi log ngoại lệ đầy đủ.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 841 - 900 / Tổng 1919 dòng] ===
[APPROVED]

CONTRACT: BtnClearStampImage_Click | Gọi hàm xóa ảnh dấu, không logic thừa.
CONTRACT: BtnSaveProfile_Click | Validate kích thước, map enum chế độ hiển thị, kiểm tra file ảnh tồn tại qua File.Exists, try-catch bắt lỗi lưu profile và log Trace.
CONTRACT: CmbDisplayMode_SelectionChanged | Map SelectionIndex sang giá trị Mode trên profile được chọn, null-safe qua pattern matching.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 901 - 960 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: BtnGoToSignWorkspace_Click | Chặn chuyển tab khi chưa xác thực VGCA, chuyển tab an toàn.
CONTRACT: BtnSelectWord_Click | Mở hộp thoại chọn tệp Word, reset trạng thái workspace, chuyển đổi Word sang HTML bất đồng bộ và nạp URI an toàn vào CoreWebView2.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 961 - 1020 / Tổng 1919 dòng] ===
[APPROVED]

CONTRACT: BtnSelectExcel_Click | Mở file Excel an toàn, reset trạng thái UI, đọc async base64 và kiểm tra khởi tạo WebView2 đúng chuẩn.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1021 - 1080 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: LoadExcelPreview & BtnExcelLandscape_Click | Nạp preview Excel vào WebView2 an toàn với JSON serialization chống script injection; xử lý timeout navigation và giải phóng event handler trong finally; cập nhật hướng in landscape có kiểm tra null CoreWebView2.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1081 - 1140 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: BtnExcelPortrait_Click | Cập nhật hướng trang bảng tính an toàn qua WebView2 script tĩnh, kiểm tra null trước khi gọi.
CONTRACT: BtnSelectPdf_Click | Kiểm tra file tồn tại, cập nhật trạng thái UI, nạp PDF bất đồng bộ kèm khối try-catch xử lý lỗi fail-closed.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1141 - 1200 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: ConvertExcelToPdfAsync, ConvertWordToPdfAsync | Validate tệp đầu vào và trạng thái WebView2, cấu hình tham số trang in, xuất PDF vector qua PrintToPdfAsync và kiểm tra tệp kết quả.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1201 - 1260 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: ConvertWordToPdfAsync & ExcelToPdf continuation | Validate null và file tồn tại trước khi chuyển đổi, cập nhật trạng thái UI an toàn, ẩn loading indicator trong finally, quản lý async/await đầy đủ.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1261 - 1320 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: DisplayPdfInInteractiveViewerAsync | Kiểm tra khởi tạo WebView2, giới hạn kích thước file <= 30MB, đọc Base64 bất đồng bộ và kiểm tra an toàn vòng đời UI control sau await.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1321 - 1380 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: PdfViewerNavigation | Điều hướng WebView2 an toàn kèm timeout 10s, giải phóng event handler đầy đủ, kiểm tra JavaScript engine sẵn sàng trước khi nạp dữ liệu và xử lý fail-closed qua RollbackViewerUiState.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1381 - 1440 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: RollbackViewerUiState | Reset UI state, set status error fail-closed khi nạp PDF hỏng hoặc WebView2 bị hủy.
CONTRACT: BtnInsertSignatureStamp_Click | Validate file PDF hiện tại, resolve stamp image path qua UI/profile/fallback an toàn trước khi chèn.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1441 - 1500 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: InsertStampToViewer | Kiểm tra stampPath và file tồn tại, chuyển Base64 Data URL an toàn injection, parse kích thước fallback chặt chẽ, kiểm tra null CoreWebView2 trước khi gọi ExecuteScriptAsync.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1501 - 1560 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: BtnSignDocument_Click | Kiểm tra file PDF và chứng thư VGCA, đọc tọa độ trang ký và xác thực đường dẫn ảnh con dấu trước khi ký.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1561 - 1620 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: PrepareSigningRequest | Xử lý fallback stamp mặc định, ánh xạ display mode cho profile, probe quyền ghi thư mục xuất file an toàn và khởi tạo SigningRequest.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1621 - 1680 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: MainWindow.SigningWorkflow & BtnSaveSignedCopy_Click | Bất đồng bộ gọi ký PDF an toàn, reset trạng thái UI qua finally, validate null và file tồn tại trước khi lưu bản sao.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1681 - 1740 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: BtnSaveSignedCopy_Click | Lưu bản sao PDF qua SaveFileDialog, bắt ngoại lệ IO, báo UI chuẩn.
CONTRACT: BtnOpenSignedPdfExternal_Click | Kiểm tra file tồn tại, mở PDF bằng ứng dụng mặc định qua ProcessStartInfo UseShellExecute, bắt ngoại lệ UI.
CONTRACT: BtnOpenSignedFolder_Click | Kiểm tra file tồn tại, mở Windows Explorer chọn file đã ký an toàn.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1741 - 1800 / Tổng 1919 dòng] ===
[APPROVED]

CONTRACT: BtnGoToUploadWeb_Click | Chuyển TabControl sang index 2, không side-effect.
CONTRACT: BtnFormatBold_Click / BtnFormatItalic_Click / BtnFormatUnderline_Click / BtnAlignLeft_Click / BtnAlignCenter_Click / BtnAlignRight_Click / BtnAlignJustify_Click | Dùng null-conditional `?.` an toàn với CoreWebView2, thực thi JS static qua `ExecuteScriptAsync`, không rủi ro injection, không lỗi null ref.
CONTRACT: BtnSelectSignedPdfForUpload_Click | Khởi tạo `OpenFileDialog` chọn tệp PDF, tiếp tục cấu hình ở lát cắt kế tiếp.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1801 - 1860 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: BtnExecuteUpload_Click | Validate input file và URL máy chủ, khởi tạo UploadRequest, gọi upload bất đồng bộ qua _webUploader và render trạng thái phản hồi HTTP lên UI an toàn.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1861 - 1919 / Tổng 1919 dòng] ===
[APPROVED]
CONTRACT: BtnVerifyPdf_Click | Mở OpenFileDialog chọn PDF, gọi _pdfVerifier.VerifyPdf phân tích chữ ký, hiển thị kết quả lên ListSignatures và thông báo UI.

```
