using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace VGCASignToolPro.Services
{
    public class WordConverterService
    {
        public event Action<string>? StatusChanged;

        /// <summary>
        /// Chuyển đổi tệp Word (.docx, .doc) sang định dạng HTML tương thích A4 để hiển thị trực tiếp và cho phép điều chỉnh
        /// ngay bên trong giao diện phần mềm (không cần mở cửa sổ Word rời bên ngoài).
        /// </summary>
        public Task<string> ConvertWordToEditableHtmlAsync(string wordFilePath, string? outputHtmlPath = null)
        {
            return Task.Run(() => ConvertWordToEditableHtml(wordFilePath, outputHtmlPath));
        }

        public string ConvertWordToEditableHtml(string wordFilePath, string? outputHtmlPath = null)
        {
            if (string.IsNullOrWhiteSpace(wordFilePath) || !File.Exists(wordFilePath))
                throw new FileNotFoundException($"Không tìm thấy file Word: {wordFilePath}", wordFilePath);

            string tempDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

            // Tạo tên file HTML duy nhất (kèm timestamp/guid) để triệt tiêu hoàn toàn xung đột khóa tệp (File Lock)
            if (string.IsNullOrWhiteSpace(outputHtmlPath))
            {
                string baseName = Path.GetFileNameWithoutExtension(wordFilePath);
                string uniqueToken = Guid.NewGuid().ToString("N").Substring(0, 8);
                outputHtmlPath = Path.Combine(tempDir, $"{baseName}_{uniqueToken}.html");
            }

            StatusChanged?.Invoke("Đang khởi tạo engine hiển thị trực tiếp tài liệu Word...");

            Type? wordType = Type.GetTypeFromProgID("Word.Application");
            if (wordType == null)
                throw new InvalidOperationException("Không tìm thấy Microsoft Word trên hệ thống.");

            dynamic? wordApp = null;
            dynamic? wordDoc = null;

            try
            {
                wordApp = Activator.CreateInstance(wordType);
                if (wordApp == null)
                    throw new InvalidOperationException("Không thể khởi động tiến trình Word.Application.");

                wordApp.Visible = false;
                wordApp.ScreenUpdating = false;

                // Triệt tiêu hoàn toàn hộp thoại "Microsoft Word Security Notice" khi tài liệu chứa
                // trường liên kết ngoài (External Links / Fields) — ngăn chặn modal chặn đứng tiến trình nền
                // msoAutomationSecurityForceDisable = 3: Vô hiệu hóa toàn bộ macro và cảnh báo bảo mật
                // wdAlertsNone = 0: Triệt tiêu mọi hộp thoại cảnh báo của Word
                wordApp.AutomationSecurity = 3;
                wordApp.DisplayAlerts = 0;

                StatusChanged?.Invoke($"Đang nạp cấu trúc tài liệu: {Path.GetFileName(wordFilePath)}...");
                wordDoc = wordApp.Documents.Open(
                    FileName: wordFilePath,
                    ConfirmConversions: false,
                    ReadOnly: true,
                    AddToRecentFiles: false,
                    Visible: false);

                StatusChanged?.Invoke("Đang tạo bản trình diễn trực quan (Filtered HTML)...");

                // Bắt buộc thiết lập Encoding = 65001 (msoEncodingUTF8) để triệt tiêu 100% lỗi font tiếng Việt trên tiêu đề H1, H2 và nội dung
                try
                {
                    wordDoc.WebOptions.Encoding = 65001; // msoEncodingUTF8
                }
                catch { }

                // 10 = wdFormatFilteredHTML (HTML sạch tối ưu cho hiển thị trang web)
                wordDoc.SaveAs2(
                    FileName: outputHtmlPath,
                    FileFormat: 10,
                    AddToRecentFiles: false);

                // QUAN TRỌNG: Đóng tài liệu Word ngay lập tức để giải phóng hoàn toàn khóa tệp tin (File Lock)
                wordDoc.Close(false);
                try { Marshal.ReleaseComObject(wordDoc); } catch { }
                wordDoc = null;

                wordApp.Quit(false);
                try { Marshal.ReleaseComObject(wordApp); } catch { }
                wordApp = null;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Lỗi nạp tài liệu: {ex.Message}");
                throw new InvalidOperationException($"Không thể tạo bản xem trước cho file Word: {ex.Message}", ex);
            }
            finally
            {
                if (wordDoc != null)
                {
                    try { wordDoc.Close(false); } catch { }
                    try { Marshal.ReleaseComObject(wordDoc); } catch { }
                    wordDoc = null;
                }

                if (wordApp != null)
                {
                    try { wordApp.Quit(false); } catch { }
                    try { Marshal.ReleaseComObject(wordApp); } catch { }
                    wordApp = null;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // Sau khi Word đã đóng và nhả toàn bộ File Handle, tiến hành nhúng CSS trang A4 và contentEditable
            StatusChanged?.Invoke("Tài liệu đã được tải lên khung chỉnh sửa trực tiếp.");
            EnhanceHtmlForDirectEditing(outputHtmlPath);

            return outputHtmlPath;
        }

        private static void EnhanceHtmlForDirectEditing(string htmlFilePath)
        {
            if (!File.Exists(htmlFilePath)) return;

            try
            {
                // Đọc tệp với UTF-8
                string content = File.ReadAllText(htmlFilePath, Encoding.UTF8);

                // Triệt tiêu mọi khai báo charset Windows-1252 / ISO cũ từ Word nếu có
                content = System.Text.RegularExpressions.Regex.Replace(
                    content,
                    @"charset=[a-zA-Z0-9_\-]+",
                    "charset=utf-8",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // Phát hiện hướng trang (Landscape vs Portrait) dựa trên thông số @page của Word
                bool isLandscape = content.Contains("size:841", StringComparison.OrdinalIgnoreCase) ||
                                   content.Contains("size: 841", StringComparison.OrdinalIgnoreCase) ||
                                   content.Contains("landscape", StringComparison.OrdinalIgnoreCase);

                string pageWidth = isLandscape ? "297mm" : "210mm";
                string pageHeight = isLandscape ? "210mm" : "297mm";

                // Chèn CSS định dạng trang giấy chuẩn, bảo toàn vị trí nét kẻ (relative anchors) và kích hoạt chế độ chỉnh sửa trực tiếp
                string customStyles = $@"
<meta http-equiv=""Content-Type"" content=""text/html; charset=utf-8"">
<style id=""vgca-editor-style"">
    * {{
        font-family: 'Times New Roman', 'Segoe UI', Arial, Tahoma, sans-serif !important;
    }}
    html, body {{
        background-color: #f1f5f9 !important;
        margin: 0;
        padding: 24px 0;
        font-family: 'Times New Roman', 'Segoe UI', Arial, Tahoma, sans-serif !important;
    }}
    body {{
        display: flex !important;
        flex-direction: column !important;
        align-items: center !important;
    }}
    div.WordSection1, div.WordSection2, div[class^=""WordSection""] {{
        background: #ffffff !important;
        width: {pageWidth} !important;
        min-height: {pageHeight} !important;
        box-shadow: 0 4px 15px rgba(0,0,0,0.18) !important;
        border: 1px solid #cbd5e1 !important;
        border-radius: 4px;
        box-sizing: border-box !important;
        outline: none !important;
        margin-bottom: 24px !important;
    }}
    h1, h2, h3, h4, h5, h6, .MsoTitle, .MsoSubtitle {{
        font-family: 'Times New Roman', 'Segoe UI', Arial, Tahoma, sans-serif !important;
        color: #0f172a !important;
    }}
    p.MsoNormal, p {{
        position: relative !important;
    }}
</style>
<script id=""vgca-editor-script"">
    function enableEditing() {{
        document.body.contentEditable = 'true';
        document.designMode = 'on';
        var sections = document.querySelectorAll('div.WordSection1, div.WordSection2, div[class^=""WordSection""]');
        sections.forEach(function(sec) {{
            sec.contentEditable = 'true';
        }});
        window.__vgcaIsModified = false;
        window.addEventListener('input', function() {{ window.__vgcaIsModified = true; }});
    }}
    window.addEventListener('DOMContentLoaded', enableEditing);
    window.addEventListener('load', enableEditing);
    setTimeout(enableEditing, 500);
</script>
";

                if (content.Contains("<head>", StringComparison.OrdinalIgnoreCase))
                {
                    content = content.Replace("<head>", "<head>" + customStyles, StringComparison.OrdinalIgnoreCase);
                }
                else if (content.Contains("</head>", StringComparison.OrdinalIgnoreCase))
                {
                    content = content.Replace("</head>", customStyles + "</head>", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    content = customStyles + content;
                }

                File.WriteAllText(htmlFilePath, content, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Không thể chèn style bổ sung vào file HTML xem trước: {ex.Message}");
            }
        }

        /// <summary>
        /// Chuyển đổi tài liệu Word sang PDF chuẩn Nghị định 30/2020/NĐ-CP
        /// </summary>
        public Task<string> ConvertWordToPdfAsync(string wordFilePath, string? outputPdfPath = null)
        {
            return Task.Run(() => ConvertWordToPdf(wordFilePath, outputPdfPath));
        }

        public string ConvertWordToPdf(string wordFilePath, string? outputPdfPath = null)
        {
            if (string.IsNullOrWhiteSpace(wordFilePath))
                throw new ArgumentException("Đường dẫn file Word không được để trống.", nameof(wordFilePath));

            if (!File.Exists(wordFilePath))
                throw new FileNotFoundException($"Không tìm thấy file Word: {wordFilePath}", wordFilePath);

            string ext = Path.GetExtension(wordFilePath).ToLowerInvariant();
            if (ext != ".doc" && ext != ".docx" && ext != ".rtf" && ext != ".dotx")
                throw new InvalidOperationException($"Định dạng file {ext} không được hỗ trợ. Vui lòng chọn file .docx hoặc .doc.");

            if (string.IsNullOrWhiteSpace(outputPdfPath))
            {
                string dir = Path.GetDirectoryName(wordFilePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                string baseName = Path.GetFileNameWithoutExtension(wordFilePath);
                outputPdfPath = Path.Combine(dir, $"{baseName}_converted.pdf");
            }

            string? targetDir = Path.GetDirectoryName(outputPdfPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            StatusChanged?.Invoke("Đang khởi tạo engine Microsoft Word (Late-Bound COM)...");

            Type? wordType = Type.GetTypeFromProgID("Word.Application");
            if (wordType == null)
            {
                throw new InvalidOperationException(
                    "Không tìm thấy Microsoft Word trên hệ thống. Vui lòng đảm bảo máy tính đã cài đặt Word.");
            }

            dynamic? wordApp = null;
            dynamic? wordDoc = null;

            try
            {
                wordApp = Activator.CreateInstance(wordType);
                if (wordApp == null)
                    throw new InvalidOperationException("Không thể khởi động tiến trình Word.Application.");

                wordApp.Visible = false;
                wordApp.ScreenUpdating = false;

                // Triệt tiêu hộp thoại "Microsoft Word Security Notice" (cùng cơ chế ConvertWordToEditableHtml)
                wordApp.AutomationSecurity = 3; // msoAutomationSecurityForceDisable
                wordApp.DisplayAlerts = 0;       // wdAlertsNone

                StatusChanged?.Invoke($"Đang mở tài liệu: {Path.GetFileName(wordFilePath)}...");

                wordDoc = wordApp.Documents.Open(
                    FileName: wordFilePath,
                    ConfirmConversions: false,
                    ReadOnly: true,
                    AddToRecentFiles: false,
                    Visible: false);

                StatusChanged?.Invoke("Đang chuyển đổi và xuất sang định dạng PDF chuẩn Nghị định 30/2020/NĐ-CP...");

                wordDoc.ExportAsFixedFormat(
                    OutputFileName: outputPdfPath,
                    ExportFormat: 17, // wdExportFormatPDF
                    OpenAfterExport: false,
                    OptimizeFor: 0, // wdExportOptimizeForPrint
                    Range: 0, // wdExportAllDocument
                    From: 1,
                    To: 1,
                    Item: 0, // wdExportDocumentContent
                    IncludeDocProps: true,
                    KeepIRM: true,
                    CreateBookmarks: 1, // wdExportCreateHeadingBookmarks
                    DocStructureTags: true,
                    BitmapMissingFonts: true,
                    UseISO19005_1: false);

                StatusChanged?.Invoke($"Chuyển đổi thành công! Tệp tin: {Path.GetFileName(outputPdfPath)}");
                return outputPdfPath;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Lỗi chuyển đổi Word sang PDF: {ex.Message}");
                throw new InvalidOperationException($"Không thể xuất file Word sang PDF: {ex.Message}", ex);
            }
            finally
            {
                if (wordDoc != null)
                {
                    try { wordDoc.Close(false); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Lỗi đóng Word COM doc: {ex.Message}"); }
                    try { Marshal.ReleaseComObject(wordDoc); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Lỗi giải phóng Word COM doc: {ex.Message}"); }
                    wordDoc = null;
                }

                if (wordApp != null)
                {
                    try { wordApp.Quit(false); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Lỗi đóng Word COM app: {ex.Message}"); }
                    try { Marshal.ReleaseComObject(wordApp); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Lỗi giải phóng Word COM app: {ex.Message}"); }
                    wordApp = null;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        /// <summary>
        /// Lưu nội dung HTML đã chỉnh sửa trực tiếp từ giao diện vào tệp Word (.docx/.doc) và xuất sang PDF chuẩn vector in ấn.
        /// </summary>
        public Task<string> SaveEditedHtmlAndConvertToPdfAsync(
            string htmlContent,
            string originalWordFilePath,
            string workingHtmlPath,
            bool saveToOriginalWordFile,
            string? outputPdfPath = null)
        {
            return Task.Run(() => SaveEditedHtmlAndConvertToPdf(
                htmlContent, originalWordFilePath, workingHtmlPath, saveToOriginalWordFile, outputPdfPath));
        }

        public string SaveEditedHtmlAndConvertToPdf(
            string htmlContent,
            string originalWordFilePath,
            string workingHtmlPath,
            bool saveToOriginalWordFile,
            string? outputPdfPath = null)
        {
            if (string.IsNullOrWhiteSpace(htmlContent))
            {
                throw new ArgumentException("Nội dung HTML không được để trống.", nameof(htmlContent));
            }

            if (string.IsNullOrWhiteSpace(originalWordFilePath))
            {
                throw new ArgumentException("Đường dẫn tệp Word không được để trống.", nameof(originalWordFilePath));
            }

            // Làm sạch nội dung HTML trước khi chuyển giao cho Microsoft Word
            string cleanHtml = CleanHtmlForWordConversion(htmlContent);

            // Ghi nội dung HTML đã làm sạch vào tệp tạm thời cùng thư mục với workingHtmlPath để bảo toàn đường dẫn ảnh (_files)
            string targetHtmlDir = Path.GetDirectoryName(workingHtmlPath) ?? Path.GetTempPath();
            string cleanHtmlPath = Path.Combine(targetHtmlDir, $"{Path.GetFileNameWithoutExtension(workingHtmlPath)}_clean.htm");
            File.WriteAllText(cleanHtmlPath, cleanHtml, new UTF8Encoding(false));

            if (string.IsNullOrWhiteSpace(outputPdfPath))
            {
                string dir = Path.GetDirectoryName(originalWordFilePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                string baseName = Path.GetFileNameWithoutExtension(originalWordFilePath);
                outputPdfPath = Path.Combine(dir, $"{baseName}_converted.pdf");
            }

            string? targetPdfDir = Path.GetDirectoryName(outputPdfPath);
            if (!string.IsNullOrEmpty(targetPdfDir) && !Directory.Exists(targetPdfDir))
            {
                Directory.CreateDirectory(targetPdfDir);
            }

            StatusChanged?.Invoke("Đang khởi tạo engine Microsoft Word (Late-Bound COM) để xuất văn bản đã sửa...");

            Type? wordType = Type.GetTypeFromProgID("Word.Application");
            if (wordType == null)
            {
                throw new InvalidOperationException("Không tìm thấy Microsoft Word trên hệ thống.");
            }

            dynamic? wordApp = null;
            dynamic? wordDoc = null;

            try
            {
                wordApp = Activator.CreateInstance(wordType);
                if (wordApp == null)
                {
                    throw new InvalidOperationException("Không thể khởi động tiến trình Word.Application.");
                }

                wordApp.Visible = false;
                wordApp.ScreenUpdating = false;

                // Triệt tiêu hộp thoại "Microsoft Word Security Notice" (cùng cơ chế ConvertWordToEditableHtml)
                wordApp.AutomationSecurity = 3; // msoAutomationSecurityForceDisable
                wordApp.DisplayAlerts = 0;       // wdAlertsNone

                StatusChanged?.Invoke("Đang nạp dữ liệu văn bản đã chỉnh sửa vào engine Microsoft Word...");

                // Mở file HTML với Encoding = 65001 (msoEncodingUTF8) để bảo toàn 100% tiếng Việt có dấu
                wordDoc = wordApp.Documents.Open(
                    FileName: cleanHtmlPath,
                    ConfirmConversions: false,
                    ReadOnly: false,
                    AddToRecentFiles: false,
                    PasswordDocument: Type.Missing,
                    PasswordTemplate: Type.Missing,
                    Revert: Type.Missing,
                    WritePasswordDocument: Type.Missing,
                    WritePasswordTemplate: Type.Missing,
                    Format: Type.Missing,
                    Encoding: 65001,
                    Visible: false);

                // Nếu người dùng chọn lưu vào tệp Word gốc trên máy tính
                if (saveToOriginalWordFile)
                {
                    StatusChanged?.Invoke($"Đang lưu các thay đổi trực tiếp vào tệp Word: {Path.GetFileName(originalWordFilePath)}...");

                    // Đảm bảo tệp không bị thuộc tính ReadOnly khóa
                    if (File.Exists(originalWordFilePath))
                    {
                        FileInfo fi = new FileInfo(originalWordFilePath);
                        if (fi.IsReadOnly)
                        {
                            fi.IsReadOnly = false;
                        }
                    }

                    string ext = Path.GetExtension(originalWordFilePath).ToLowerInvariant();
                    int saveFormat = ext.Equals(".doc", StringComparison.OrdinalIgnoreCase) ? 0 : 16; // 0 = wdFormatDocument, 16 = wdFormatDocumentDefault (.docx)

                    try
                    {
                        wordDoc.SaveAs2(
                            FileName: originalWordFilePath,
                            FileFormat: saveFormat,
                            AddToRecentFiles: false);
                    }
                    catch (COMException ex)
                    {
                        System.Diagnostics.Trace.TraceWarning($"Tệp Word gốc bị khóa bởi ứng dụng khác: {ex.Message}");
                        string dir = Path.GetDirectoryName(originalWordFilePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                        string name = Path.GetFileNameWithoutExtension(originalWordFilePath);
                        string backupPath = Path.Combine(dir, $"{name}_edited{ext}");
                        wordDoc.SaveAs2(FileName: backupPath, FileFormat: saveFormat, AddToRecentFiles: false);
                        StatusChanged?.Invoke($"Tệp Word gốc bị khóa bởi ứng dụng khác. Đã lưu bản sửa tại: {Path.GetFileName(backupPath)}");
                    }

                    StatusChanged?.Invoke($"Đã lưu thành công tệp Word: {Path.GetFileName(originalWordFilePath)}");
                }

                // Xuất văn bản sang PDF chuẩn vector in ấn Nghị định 30/2020/NĐ-CP
                StatusChanged?.Invoke($"Đang xuất tài liệu sang PDF: {Path.GetFileName(outputPdfPath)}...");
                wordDoc.ExportAsFixedFormat(
                    OutputFileName: outputPdfPath,
                    ExportFormat: 17, // wdExportFormatPDF
                    OpenAfterExport: false,
                    OptimizeFor: 0, // wdExportOptimizeForPrint
                    Range: 0, // wdExportAllDocument
                    From: 1,
                    To: 1,
                    Item: 0, // wdExportDocumentContent
                    IncludeDocProps: true,
                    KeepIRM: true,
                    CreateBookmarks: 1,
                    DocStructureTags: true,
                    BitmapMissingFonts: true,
                    UseISO19005_1: false);

                StatusChanged?.Invoke($"Đã chuyển đổi hoàn tất sang PDF: {Path.GetFileName(outputPdfPath)}");
                return outputPdfPath;
            }
            finally
            {
                if (wordDoc != null)
                {
                    try { wordDoc.Close(false); } catch { }
                    try { Marshal.ReleaseComObject(wordDoc); } catch { }
                    wordDoc = null;
                }

                if (wordApp != null)
                {
                    try { wordApp.Quit(false); } catch { }
                    try { Marshal.ReleaseComObject(wordApp); } catch { }
                    wordApp = null;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();

                try
                {
                    if (File.Exists(cleanHtmlPath))
                    {
                        File.Delete(cleanHtmlPath);
                    }
                }
                catch { }
            }
        }

        private static string CleanHtmlForWordConversion(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            // Loại bỏ tag style và script phục vụ soạn thảo trên WebView2
            string cleaned = System.Text.RegularExpressions.Regex.Replace(
                html,
                @"<style id=""vgca-editor-style"">[\s\S]*?</style>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned,
                @"<script id=""vgca-editor-script"">[\s\S]*?</script>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Loại bỏ các thuộc tính contenteditable
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned,
                @"\s+contenteditable(=[""'][^""']*[""'])?",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return cleaned;
        }
    }
}
