using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Word = Microsoft.Office.Interop.Word;

namespace VGCASignToolPro.Services
{
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

            // Ensure destination directory exists
            string? targetDir = Path.GetDirectoryName(outputPdfPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            StatusChanged?.Invoke("Đang khởi động engine Microsoft Word...");

            Word.Application? wordApp = null;
            Word.Document? wordDoc = null;

            try
            {
                wordApp = new Word.Application
                {
                    Visible = false,
                    ScreenUpdating = false
                };

                StatusChanged?.Invoke($"Đang mở tài liệu: {Path.GetFileName(wordFilePath)}...");
                object missing = Type.Missing;
                object readOnly = true;
                object isVisible = false;
                object fileName = wordFilePath;

                wordDoc = wordApp.Documents.Open(
                    ref fileName,
                    ref missing,
                    ref readOnly,
                    ref missing,
                    ref missing,
                    ref missing,
                    ref missing,
                    ref missing,
                    ref missing,
                    ref missing,
                    ref missing,
                    ref isVisible,
                    ref missing,
                    ref missing,
                    ref missing,
                    ref missing);

                StatusChanged?.Invoke("Đang xuất tài liệu sang chuẩn PDF (giữ nguyên lề & font)...");

                wordDoc.ExportAsFixedFormat(
                    outputPdfPath,
                    Word.WdExportFormat.wdExportFormatPDF,
                    OpenAfterExport: false,
                    OptimizeFor: Word.WdExportOptimizeFor.wdExportOptimizeForPrint,
                    Range: Word.WdExportRange.wdExportAllDocument,
                    From: 1,
                    To: 1,
                    Item: Word.WdExportItem.wdExportDocumentContent,
                    IncludeDocProps: true,
                    KeepIRM: true,
                    CreateBookmarks: Word.WdExportCreateBookmarks.wdExportCreateHeadingBookmarks,
                    DocStructureTags: true,
                    BitmapMissingFonts: true,
                    UseISO19005_1: false);

                StatusChanged?.Invoke($"Chuyển đổi hoàn tất: {Path.GetFileName(outputPdfPath)}");
                return outputPdfPath;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Lỗi chuyển đổi Word sang PDF: {ex.Message}");
                throw new InvalidOperationException($"Lỗi khi chuyển đổi file Word sang PDF: {ex.Message}", ex);
            }
            finally
            {
                if (wordDoc != null)
                {
                    try
                    {
                        wordDoc.Close(false);
                    }
                    catch { }
                    Marshal.ReleaseComObject(wordDoc);
                    wordDoc = null;
                }

                if (wordApp != null)
                {
                    try
                    {
                        wordApp.Quit(false);
                    }
                    catch { }
                    Marshal.ReleaseComObject(wordApp);
                    wordApp = null;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }
}
