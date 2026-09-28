# BÁO CÁO THẨM ĐỊNH MÃ NGUỒN CODEX (DEEP LINE-BY-LINE v4.0)

- **Thời gian**: 13:09:06 28/9/2026
- **Trạng thái**: ✅ **[APPROVED] - ĐÃ PHÊ DUYỆT HOÀN TOÀN**
- **Số lượng vi phạm**: 0 lỗi
- **Tóm tắt**: Toàn bộ 39 lát cắt của 1 file đã được phê duyệt 100%.

---

## 🎉 XÁC NHẬN:
Toàn bộ các file mã nguồn và lát cắt dòng code đã được thẩm định chi tiết và phê duyệt 100%.

---
### 📄 NGUYÊN VĂN PHẢN HỒI TỪ CODEX AUDITOR:
```text

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1 - 60 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 61 - 120 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 121 - 180 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 181 - 240 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 241 - 300 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 301 - 360 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 361 - 420 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 421 - 480 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 481 - 540 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 541 - 600 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 601 - 660 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 661 - 720 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 721 - 780 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 781 - 840 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 841 - 900 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 901 - 960 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 961 - 1020 / Tổng 2291 dòng] ===
[APPROVED]
(Kế thừa từ cache đã duyệt)

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1021 - 1080 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: SetProcessingBusyState | Khóa mở UI controls theo trạng thái bận, đổi con trỏ chuột an toàn null.
CONTRACT: Cleanup (phần cuối hàm đóng tệp) | Reset trạng thái UI, dọn viewer WebView2 kèm bắt ngoại lệ, kích hoạt thu hồi bộ nhớ an toàn.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1081 - 1140 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: BtnCloseCurrentFile_Click, BtnSelectWord_Click, LoadWordDocumentAsync | Kiểm tra cờ bận _isBusyProcessing chống race condition, xác thực tồn tại tệp vật lý trước khi nạp, dọn dẹp cờ trạng thái tài liệu cũ an toàn.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1141 - 1200 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: LoadWordFileHandler, BtnSelectExcel_Click | Quản lý state bận qua `finally`, null-check `CoreWebView2` an toàn, chặn re-entry trước khi mở Excel.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1201 - 1260 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: LoadExcelDocumentAsync | Validate file path, cập nhật UI state, đọc Base64 và hook NavigationCompleted an toàn tài nguyên.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1261 - 1320 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: BtnExcelLandscape_Click & ExcelWebView2Loader | Điều hướng WebView2 an toàn với timeout 5s, gỡ NavigationCompleted trong finally, serialize JSON chống JS Injection, xử lý lỗi fail-closed và giải phóng busy state đúng chuẩn.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1321 - 1380 / Tổng 2291 dòng] ===
[APPROVED]

CONTRACT: BtnExcelPortrait_Click | Đổi hướng trang in Excel qua WebView2 script, kiểm tra null trước khi gọi.
CONTRACT: BtnSelectPdf_Click | Kiểm tra trạng thái bận, xác nhận đóng tài liệu cũ trước khi mở dialog chọn file PDF.
CONTRACT: LoadPdfDocumentAsync | Validate đường dẫn file tồn tại, thiết lập trạng thái bận và reset state trước khi nạp tài liệu.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1381 - 1440 / Tổng 2291 dòng] ===
[APPROVED]

CONTRACT: DisplayPdfInInteractiveViewerAsync / LoadPdf tail-end | Cập nhật trạng thái UI, nạp PDF bất đồng bộ, bắt lỗi và giải phóng trạng thái bận qua finally SetProcessingBusyState(false).
CONTRACT: BtnExecuteConvertDocument_Click | Chặn re-entrancy bằng _isBusyProcessing, kiểm tra tồn tại tệp Excel/Word trước khi gọi ConvertExcelToPdfAsync/ConvertWordToPdfAsync, thông báo nếu chưa nạp tệp.
CONTRACT: ConvertExcelToPdfAsync head | Kiểm tra guard clause chống xử lý trùng lặp và xác thực tệp Excel nguồn tồn tại.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1441 - 1500 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: ConvertExcelToPdfAsync / MainWindow UI Handler | Validate WebView2 và file Excel, tạo cấu hình in PDF A4, xuất file qua PrintToPdfAsync, cập nhật UI và chuyển tiếp sang DisplayPdfInInteractiveViewerAsync fail-safe.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1501 - 1560 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: ConvertWordToPdfAsync | Chặn re-entrancy qua `_isBusyProcessing`, validate `_wordConverter` và file Word tồn tại trước khi xử lý, dialog xác nhận phân nhánh `Yes/No/Cancel` an toàn, kiểm tra null `CoreWebView2` trước khi thao tác.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1561 - 1620 / Tổng 2291 dòng] ===
[APPROVED]

CONTRACT: ConvertToPdfAndSign / DisplayPdfInInteractiveViewerAsync | DOM trích xuất sạch qua ExecuteScriptAsync, parse JSON an toàn, convert PDF bất đồng bộ, dọn state Word, cập nhật UI và nạp viewer tương tác đúng luồng.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1621 - 1680 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: DisplayPdfInInteractiveViewerAsync | Kiểm tra sẵn sàng WebView2, giới hạn kích thước file <= 30MB, đọc bất đồng bộ Base64 và xử lý an toàn vòng đời đối tượng UI sau await.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1681 - 1740 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: WebView2 PDF Viewer Navigation & Initialization | Xử lý điều hướng WebView2 kèm timeout 10s, giải phóng event handler `NavigationCompleted`, kiểm tra sẵn sàng của JS engine bằng polling và serialize base64 an toàn chống injection.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1741 - 1800 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: RollbackViewerUiState | Reset UI state khi viewer lỗi. Trace exception đầy đủ, fail-closed khóa nút thao tác.
CONTRACT: BtnInsertSignatureStamp_Click | Chặn race condition bằng _isBusyProcessing, validate tồn tại file PDF và resolve đường dẫn stamp từ TextBox hoặc SelectedItem profile.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1801 - 1860 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: InsertStampToViewer | Lấy đường dẫn ảnh con dấu/chữ ký, chuyển base64 Data URL an toàn, phân tích kích thước hiển thị và truyền vào WebView2 qua script execution an toàn.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1861 - 1920 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: BtnSignDocument_Click | Kiểm tra trạng thái xử lý, tệp PDF hợp lệ, chứng thư số VGCA trước khi ký. Phân tích tọa độ và kích thước tem ký an toàn với giá trị fallback mặc định.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1921 - 1980 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: Chuẩn bị tham số ký (Resolve Stamp & Output Path) | Xác định đường dẫn ảnh con dấu tuần tự (UI -> Profile -> Desktop fallback -> null), probe quyền ghi thư mục nguồn có fallback Desktop an toàn, tạo cấu hình SigningRequest chuẩn xác.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 1981 - 2040 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: DisplayPdfInInteractiveViewerAsync / SignPdfUIWorkflow | Điều phối luồng ký async an toàn: await ký số và render viewer, kiểm tra null `_vgcaAuth.CurrentTeacher` trước khi gán Tab 3, xử lý `try/catch` bắt lỗi tường minh không nuốt exception.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 2041 - 2100 / Tổng 2291 dòng] ===
[APPROVED]

CONTRACT: BtnSaveSignedCopy_Click | Validate path trước khi copy, dùng SaveFileDialog, bắt lỗi IO hiển thị thông báo.
CONTRACT: BtnOpenSignedPdfExternal_Click | Validate file tồn tại, dùng ProcessStartInfo ShellExecute mở ứng dụng mặc định, bắt lỗi runtime đầy đủ.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 2101 - 2160 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: BtnOpenSignedFolder_Click | Validate file tồn tại, mở explorer chọn file an toàn qua try-catch, báo lỗi UI rõ ràng.
CONTRACT: BtnGoToUploadWeb_Click | Chuyển tab index trực tiếp, không logic thừa.
CONTRACT: Format & Align Handlers (2127-2160) | Dùng null-conditional `CoreWebView2?.` gọi script tĩnh an toàn, không rủi ro injection hay crash UI.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 2161 - 2220 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: BtnSelectSignedPdfForUpload_Click | Mở OpenFileDialog lọc PDF, gán path và trích xuất title vào UI.
CONTRACT: BtnExecuteUpload_Click | Validate file path và scheme URL http/https, đóng gói UploadRequest, disable nút upload chống double-click trước khi await task upload.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 2221 - 2280 / Tổng 2291 dòng] ===
[APPROVED]

CONTRACT: BtnVerifyPdf_Click & UploadResultHandler | Phục hồi UI control trong finally khi kết thúc upload HTTP. Dialog chọn PDF kiểm tra kết quả `ShowDialog() == true`, gọi `_pdfVerifier.VerifyPdf` cập nhật ListSignatures và status đúng luồng UI.

=== LÁT CẮT: MainWindow.xaml.cs [Dòng 2281 - 2291 / Tổng 2291 dòng] ===
[APPROVED]
CONTRACT: HandleNoSignature UI & EndOfFile | Cập nhật trạng thái và hiển thị thông báo khi tài liệu không có chữ ký số; đóng khối lệnh kết thúc MainWindow an toàn.

```
