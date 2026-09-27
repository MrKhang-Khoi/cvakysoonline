using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VGCASignToolPro.Services
{
    /// <summary>
    /// Dịch vụ chuyển đổi tài liệu Word sang PDF sử dụng cơ chế Late-Bound COM (IDispatch),
    /// không phụ thuộc vào các thư viện Primary Interop Assembly (PIA) cũ như office.dll hay Microsoft.Office.Interop.Word.
    /// Đảm bảo tương thích 100% trên .NET 8 x64 và mọi phiên bản Microsoft Office (2013, 2016, 2019, 2021, Office 365).
    /// </summary>
    public class WordConverterService
    {
        public event Action<string>? StatusChanged;

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

            // Đảm bảo thư mục đích tồn tại
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
                    "Không tìm thấy dịch vụ Microsoft Word trên hệ điều hành Windows. Vui lòng đảm bảo máy tính đã cài đặt Word (Office 2013/2016/2019/2021/365).");
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

                StatusChanged?.Invoke($"Đang mở tài liệu: {Path.GetFileName(wordFilePath)}...");

                // Mở tài liệu Word ở chế độ ReadOnly an toàn
                wordDoc = wordApp.Documents.Open(
                    FileName: wordFilePath,
                    ReadOnly: true,
                    AddToRecentFiles: false,
                    Visible: false);

                StatusChanged?.Invoke("Đang chuyển đổi và xuất sang định dạng PDF chuẩn Nghị định 30/2020/NĐ-CP...");

                // 17 = wdExportFormatPDF, 0 = wdExportOptimizeForPrint, 0 = wdExportAllDocument, 0 = wdExportDocumentContent
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

        /// <summary>
        /// Mở tài liệu Word trực tiếp trên ứng dụng Microsoft Word của hệ thống để người dùng xem/chỉnh sửa
        /// </summary>
        public static void OpenWordDocumentInViewer(string wordFilePath)
        {
            if (!File.Exists(wordFilePath))
                throw new FileNotFoundException("Tệp tin Word không tồn tại.", wordFilePath);

            var psi = new ProcessStartInfo
            {
                FileName = wordFilePath,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
    }
}
