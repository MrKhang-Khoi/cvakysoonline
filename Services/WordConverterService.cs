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

                StatusChanged?.Invoke($"Đang nạp cấu trúc tài liệu: {Path.GetFileName(wordFilePath)}...");
                wordDoc = wordApp.Documents.Open(
                    FileName: wordFilePath,
                    ReadOnly: true,
                    AddToRecentFiles: false,
                    Visible: false);

                StatusChanged?.Invoke("Đang tạo bản trình diễn trực quan (Filtered HTML)...");

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
                string content = File.ReadAllText(htmlFilePath, Encoding.UTF8);

                // Chèn CSS định dạng trang giấy A4 và kích hoạt chế độ chỉnh sửa trực tiếp
                string customStyles = @"
<style>
    html, body {
        background-color: #f1f5f9 !important;
        margin: 0;
        padding: 24px 0;
        font-family: 'Times New Roman', 'Segoe UI', serif;
    }
    body {
        display: flex !important;
        flex-direction: column !important;
        align-items: center !important;
    }
    .WordSection1, .WordSection2, body > div {
        background: #ffffff !important;
        width: 210mm !important;
        min-height: 297mm !important;
        padding: 20mm 20mm 20mm 25mm !important;
        box-shadow: 0 4px 15px rgba(0,0,0,0.18) !important;
        border: 1px solid #cbd5e1 !important;
        border-radius: 4px;
        box-sizing: border-box !important;
        outline: none !important;
        margin-bottom: 24px !important;
    }
</style>
<script>
    function enableEditing() {
        document.body.contentEditable = 'true';
        document.designMode = 'on';
        var sections = document.querySelectorAll('.WordSection1, .WordSection2, body > div');
        sections.forEach(function(sec) {
            sec.contentEditable = 'true';
        });
    }
    window.addEventListener('DOMContentLoaded', enableEditing);
    window.addEventListener('load', enableEditing);
    setTimeout(enableEditing, 500);
</script>
";

                if (content.Contains("</head>", StringComparison.OrdinalIgnoreCase))
                {
                    content = content.Replace("</head>", customStyles + "</head>", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    content = customStyles + content;
                }

                File.WriteAllText(htmlFilePath, content, Encoding.UTF8);
            }
            catch
            {
                // Bỏ qua nếu có lỗi nhỏ về encoding, file HTML vẫn hiển thị tốt
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

                StatusChanged?.Invoke($"Đang mở tài liệu: {Path.GetFileName(wordFilePath)}...");

                wordDoc = wordApp.Documents.Open(
                    FileName: wordFilePath,
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
        }
    }
}
