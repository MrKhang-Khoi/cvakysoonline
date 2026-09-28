using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace VGCASignToolPro.Services
{
    public class WordConverterService
    {
        public event Action<string>? StatusChanged;

        // ═══════════════════════════════════════════════════════════════════════════════
        // Registry key chặn hộp thoại "Microsoft Word Security Notice" khi tài liệu chứa
        // trường INCLUDEPICTURE, INCLUDETEXT, LINK, v.v.
        // Đây là GIẢI PHÁP DUY NHẤT đáng tin cậy 100% — các API như AutomationSecurity,
        // DisplayAlerts, UpdateLinksAtOpen đều KHÔNG chặn được dialog này (đã được Microsoft
        // thiết kế cố tình vượt qua các cơ chế suppress thông thường).
        //
        // Ref: HKCU\Software\Microsoft\Office\16.0\Word\Security
        //   DisableWarningOnIncludeFieldsUpdate = 1 (REG_DWORD)
        // ═══════════════════════════════════════════════════════════════════════════════
        private const string WordSecurityRegPath = @"Software\Microsoft\Office\16.0\Word\Security";
        private const string DisableWarningValueName = "DisableWarningOnIncludeFieldsUpdate";

        /// <summary>
        /// Tạm thời vô hiệu hóa cảnh báo bảo mật "fields that share data with external files"
        /// bằng cách ghi Registry. Trả về giá trị gốc để khôi phục sau khi xong.
        /// </summary>
        private static (object? originalValue, bool existed) SuppressWordSecurityDialog()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(WordSecurityRegPath);
                if (key != null)
                {
                    object? original = key.GetValue(DisableWarningValueName);
                    bool existed = original != null;
                    key.SetValue(DisableWarningValueName, 1, RegistryValueKind.DWord);
                    return (original, existed);
                }
            }
            catch { /* Registry access bị hạn chế → bỏ qua */ }
            return (null, false);
        }

        /// <summary>
        /// Khôi phục lại giá trị Registry gốc sau khi đã hoàn thành xử lý Word COM.
        /// </summary>
        private static void RestoreWordSecuritySetting(object? originalValue, bool existed)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(WordSecurityRegPath, writable: true);
                if (key != null)
                {
                    if (existed && originalValue != null)
                    {
                        key.SetValue(DisableWarningValueName, originalValue, RegistryValueKind.DWord);
                    }
                    else if (!existed)
                    {
                        key.DeleteValue(DisableWarningValueName, throwOnMissingValue: false);
                    }
                }
            }
            catch { /* Không ảnh hưởng nếu khôi phục thất bại */ }
        }

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

            // Tạm thời ghi Registry để chặn dialog "Security Notice" TRƯỚC khi tạo Word instance
            var (regOriginalValue, regExisted) = SuppressWordSecurityDialog();

            dynamic? wordApp = null;
            dynamic? wordDoc = null;

            try
            {
                wordApp = Activator.CreateInstance(wordType);
                if (wordApp == null)
                    throw new InvalidOperationException("Không thể khởi động tiến trình Word.Application.");

                wordApp.Visible = false;
                wordApp.ScreenUpdating = false;

                // ═══════════════════════════════════════════════════════════════════════════════
                // FIX BUG: Triệt tiêu hoàn toàn hộp thoại "Microsoft Word Security Notice"
                // Nguyên nhân gốc: Word phát hiện <img src> trỏ ra ngoài thư mục (external files)
                //   từ các lần convert cũ (đường dẫn ../../../../Desktop/..._files/image001.gif)
                //   → hiện cảnh báo "fields that can share data with external files and websites"
                //
                // AutomationSecurity = 3 chỉ chặn macro, KHÔNG chặn dialog này.
                // Options.UpdateLinksAtOpen = false mới là fix chính xác cho dialog "external links".
                // ═══════════════════════════════════════════════════════════════════════════════
                wordApp.AutomationSecurity = 3;  // msoAutomationSecurityForceDisable
                wordApp.DisplayAlerts = 0;        // wdAlertsNone
                try { wordApp.Options.UpdateLinksAtOpen = false; } catch { }

                StatusChanged?.Invoke($"Đang nạp cấu trúc tài liệu: {Path.GetFileName(wordFilePath)}...");

                // Mở KHÔNG ReadOnly để Word có thể re-embed shapes/images vào document trước khi SaveAs HTML
                wordDoc = wordApp.Documents.Open(
                    FileName: wordFilePath,
                    ConfirmConversions: false,
                    ReadOnly: false,
                    AddToRecentFiles: false,
                    Visible: false);

                StatusChanged?.Invoke("Đang tạo bản trình diễn trực quan (Filtered HTML)...");

                // Bắt buộc thiết lập Encoding = 65001 (msoEncodingUTF8) để triệt tiêu 100% lỗi font tiếng Việt
                try
                {
                    wordDoc.WebOptions.Encoding = 65001; // msoEncodingUTF8
                }
                catch { }

                // ═══════════════════════════════════════════════════════════════════════════════
                // FIX BUG: Buộc Word nhúng tất cả hình ảnh thành embedded (không link ngoài)
                // Giải mã (Unlink) tất cả trường INCLUDEPICTURE để chuyển từ linked → embedded,
                // đảm bảo SaveAs HTML sẽ tạo bản sao ảnh trong _files/ folder mới
                // ═══════════════════════════════════════════════════════════════════════════════
                try
                {
                    dynamic fields = wordDoc.Fields;
                    // Duyệt ngược để tránh lệch index khi Unlink
                    for (int i = fields.Count; i >= 1; i--)
                    {
                        try
                        {
                            dynamic field = fields.Item(i);
                            int fieldType = (int)field.Type;
                            // wdFieldIncludePicture = 67, wdFieldLink = 56, wdFieldInclude = 36
                            if (fieldType == 67 || fieldType == 56 || fieldType == 36)
                            {
                                field.Unlink(); // Chuyển field thành nội dung tĩnh (static result)
                            }
                        }
                        catch { /* Bỏ qua field lỗi */ }
                    }
                }
                catch { /* Document có thể không có Fields collection */ }

                // 10 = wdFormatFilteredHTML (HTML sạch tối ưu cho hiển thị trang web)
                wordDoc.SaveAs2(
                    FileName: outputHtmlPath,
                    FileFormat: 10,
                    AddToRecentFiles: false);

                // QUAN TRỌNG: Đóng tài liệu Word ngay lập tức để giải phóng hoàn toàn khóa tệp tin (File Lock)
                // Đóng KHÔNG lưu thay đổi vào file gốc (chỉ lưu HTML ở trên)
                wordDoc.Close(0); // wdDoNotSaveChanges = 0
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
                    try { wordDoc.Close(0); } catch { }
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

                // Khôi phục Registry về trạng thái ban đầu sau khi Word đã đóng
                RestoreWordSecuritySetting(regOriginalValue, regExisted);
            }

            // Sau khi Word đã đóng và nhả toàn bộ File Handle:
            // 1. Sửa đường dẫn ảnh hỏng (img src trỏ external/cũ) bằng cách copy ảnh về _files/ hoặc embed base64
            FixBrokenImagePaths(outputHtmlPath);
            // 2. Nhúng CSS trang A4 và contentEditable
            StatusChanged?.Invoke("Tài liệu đã được tải lên khung chỉnh sửa trực tiếp.");
            EnhanceHtmlForDirectEditing(outputHtmlPath);

            return outputHtmlPath;
        }

        /// <summary>
        /// Sửa đường dẫn ảnh bị hỏng trong HTML xuất từ Word.
        /// Nguyên nhân: Word cache đường dẫn ảnh từ lần convert cũ (../../../../Desktop/..._files/image001.gif)
        /// thay vì tạo bản sao ảnh mới trong thư mục _files/ đi kèm file HTML hiện tại.
        /// Phương thức này: quét tất cả img src → tìm file ảnh gốc → copy về _files/ đúng → cập nhật src.
        /// </summary>
        private static void FixBrokenImagePaths(string htmlFilePath)
        {
            if (!File.Exists(htmlFilePath)) return;

            try
            {
                string htmlDir = Path.GetDirectoryName(htmlFilePath) ?? "";
                string htmlBaseName = Path.GetFileNameWithoutExtension(htmlFilePath);
                string localFilesFolder = $"{htmlBaseName}_files";
                string localFilesFolderPath = Path.Combine(htmlDir, localFilesFolder);

                string content = File.ReadAllText(htmlFilePath, Encoding.UTF8);

                // Regex bắt tất cả <img ... src="..." ...>
                var imgSrcPattern = new System.Text.RegularExpressions.Regex(
                    @"(<img[^>]*\bsrc\s*=\s*"")([^""]+)("")",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

                bool hasChanges = false;
                content = imgSrcPattern.Replace(content, match =>
                {
                    string prefix = match.Groups[1].Value;
                    string rawSrc = match.Groups[2].Value;
                    string suffix = match.Groups[3].Value;

                    // Nếu src đã là relative path trỏ đúng vào _files folder hiện tại → bỏ qua
                    if (rawSrc.StartsWith(localFilesFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        return match.Value;
                    }

                    // Nếu src là base64 data URI → bỏ qua
                    if (rawSrc.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        return match.Value;
                    }

                    // Bỏ qua URL mạng — không xử lý ảnh online (chống crash NotSupportedException)
                    if (rawSrc.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        rawSrc.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        rawSrc.StartsWith("//", StringComparison.Ordinal))
                    {
                        return match.Value;
                    }

                    // Giải mã URL encoding trong src (ví dụ: %20 → space)
                    string decodedSrc;
                    try { decodedSrc = Uri.UnescapeDataString(rawSrc); }
                    catch { return match.Value; }

                    // Bỏ qua đường dẫn tuyệt đối trỏ ra ngoài (chống LFI / Arbitrary File Read)
                    if (Path.IsPathRooted(decodedSrc))
                    {
                        return match.Value;
                    }

                    // Giải quyết đường dẫn tương đối từ thư mục HTML (../../../../Desktop/..._files/image001.gif)
                    string? resolvedPath = null;
                    try
                    {
                        string candidatePath = Path.GetFullPath(Path.Combine(htmlDir, decodedSrc));
                        // Ràng buộc an ninh: chỉ chấp nhận file nằm trong phạm vi thư mục người dùng
                        // (bao gồm cả AppData\Temp và Desktop nơi Word có thể xuất _files/)
                        string userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        if (!string.IsNullOrEmpty(userRoot) &&
                            candidatePath.StartsWith(userRoot, StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(candidatePath))
                        {
                            resolvedPath = candidatePath;
                        }
                    }
                    catch { /* Path không hợp lệ → bỏ qua */ }

                    if (resolvedPath != null)
                    {
                        // Tìm thấy file ảnh gốc → copy về _files/ folder đúng
                        if (!Directory.Exists(localFilesFolderPath))
                        {
                            Directory.CreateDirectory(localFilesFolderPath);
                        }

                        string imageFileName = Path.GetFileName(resolvedPath);
                        string destinationPath = Path.Combine(localFilesFolderPath, imageFileName);

                        try
                        {
                            File.Copy(resolvedPath, destinationPath, overwrite: true);
                            hasChanges = true;
                            return $"{prefix}{localFilesFolder}/{imageFileName}{suffix}";
                        }
                        catch
                        {
                            // Copy thất bại → thử embed base64
                        }
                    }

                    // Fallback: Không tìm được file ảnh gốc → thử embed base64 nếu tìm thấy file
                    // với tên tương tự trong bất kỳ thư mục _files nào trong cùng tempDir
                    string imageBaseName = Path.GetFileName(decodedSrc);
                    if (!string.IsNullOrEmpty(imageBaseName) && !string.IsNullOrEmpty(htmlDir))
                    {
                        try
                        {
                            // Tìm file ảnh trong tất cả thư mục _files cùng cấp (từ các lần convert cũ)
                            string[] candidateFiles = Directory.GetFiles(htmlDir, imageBaseName, SearchOption.AllDirectories);
                            if (candidateFiles.Length > 0)
                            {
                                string foundFile = candidateFiles[0];
                                if (!Directory.Exists(localFilesFolderPath))
                                {
                                    Directory.CreateDirectory(localFilesFolderPath);
                                }

                                string destPath = Path.Combine(localFilesFolderPath, imageBaseName);
                                File.Copy(foundFile, destPath, overwrite: true);
                                hasChanges = true;
                                return $"{prefix}{localFilesFolder}/{imageBaseName}{suffix}";
                            }
                        }
                        catch { /* Bỏ qua lỗi tìm kiếm */ }
                    }

                    // Không thể khôi phục → giữ nguyên src gốc
                    return match.Value;
                });

                if (hasChanges)
                {
                    File.WriteAllText(htmlFilePath, content, Encoding.UTF8);
                }
            }
            catch
            {
                // Không làm gián đoạn luồng chính nếu fix ảnh thất bại
            }
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

            // Tạm thời ghi Registry để chặn dialog "Security Notice" TRƯỚC khi tạo Word instance
            var (regOriginalValue, regExisted) = SuppressWordSecurityDialog();

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
                try { wordApp.Options.UpdateLinksAtOpen = false; } catch { }

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

                RestoreWordSecuritySetting(regOriginalValue, regExisted);
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

            // Tạm thời ghi Registry để chặn dialog "Security Notice" TRƯỚC khi tạo Word instance
            var (regOriginalValue, regExisted) = SuppressWordSecurityDialog();

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
                try { wordApp.Options.UpdateLinksAtOpen = false; } catch { }

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

                RestoreWordSecuritySetting(regOriginalValue, regExisted);
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
