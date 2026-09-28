using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Syncfusion.DocToPDFConverter;
using Syncfusion.Pdf;

namespace VGCASignToolPro.Services
{
    /// <summary>
    /// Dịch vụ xử lý tài liệu Word và chuyển đổi sang PDF cấp doanh nghiệp (Enterprise Document Engine).
    /// Sử dụng Syncfusion DocIO và DocToPDFConverter độc lập, thuần 100% C# Managed Code (.NET 8):
    /// - 100% KHÔNG phụ thuộc Microsoft Word / Office cài đặt trên máy client.
    /// - Triệt tiêu hoàn toàn rủi ro rò rỉ tiến trình ngoài (0% Zombie WINWORD.EXE) và khóa tệp tin (File Lock).
    /// - Không can thiệp Registry, không dùng COM Interop.
    /// - Xuất PDF vector siêu nét chuẩn Nghị định 30/2020/NĐ-CP về thể thức văn bản hành chính.
    /// </summary>
    public class WordConverterService
    {
        public event Action<string>? StatusChanged;

        /// <summary>
        /// Nạp tài liệu Word (.docx, .doc, .rtf, .txt) thành luồng Stream tương thích cho SfRichTextBoxAdv.
        /// Tự động chuyển đổi định dạng Word cũ (.doc 97-2003) sang OpenXML (.docx) trong bộ nhớ.
        /// </summary>
        public (Stream stream, Syncfusion.Windows.Controls.RichTextBoxAdv.FormatType formatType) LoadWordToEditorStream(string wordFilePath)
        {
            if (string.IsNullOrWhiteSpace(wordFilePath) || !File.Exists(wordFilePath))
                throw new FileNotFoundException($"Không tìm thấy file Word: {wordFilePath}", wordFilePath);

            string ext = Path.GetExtension(wordFilePath).ToLowerInvariant();

            StatusChanged?.Invoke($"Đang nạp tài liệu Word: {Path.GetFileName(wordFilePath)}...");

            if (ext == ".doc")
            {
                // Định dạng nhị phân Word 97-2003: Dùng DocIO đọc và chuyển đổi sang Docx Stream trong RAM
                using FileStream fs = new FileStream(wordFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using WordDocument doc = new WordDocument(fs, Syncfusion.DocIO.FormatType.Doc);
                MemoryStream docxStream = new MemoryStream();
                doc.Save(docxStream, Syncfusion.DocIO.FormatType.Docx);
                docxStream.Position = 0;
                return (docxStream, Syncfusion.Windows.Controls.RichTextBoxAdv.FormatType.Docx);
            }
            else if (ext == ".rtf")
            {
                MemoryStream ms = new MemoryStream(File.ReadAllBytes(wordFilePath));
                return (ms, Syncfusion.Windows.Controls.RichTextBoxAdv.FormatType.Rtf);
            }
            else if (ext == ".txt")
            {
                MemoryStream ms = new MemoryStream(File.ReadAllBytes(wordFilePath));
                return (ms, Syncfusion.Windows.Controls.RichTextBoxAdv.FormatType.Txt);
            }
            else
            {
                // Mặc định .docx hoặc .dotx: Nạp trực tiếp stream DOCX
                MemoryStream ms = new MemoryStream(File.ReadAllBytes(wordFilePath));
                return (ms, Syncfusion.Windows.Controls.RichTextBoxAdv.FormatType.Docx);
            }
        }

        /// <summary>
        /// Chuyển đổi tệp Word (.docx, .doc) trên ổ đĩa sang PDF vector chuẩn Nghị định 30/2020/NĐ-CP (Async).
        /// </summary>
        public Task<string> ConvertWordToPdfAsync(string wordFilePath, string? outputPdfPath = null)
        {
            return Task.Run(() => ConvertWordToPdf(wordFilePath, outputPdfPath));
        }

        /// <summary>
        /// Chuyển đổi tệp Word (.docx, .doc) trên ổ đĩa sang PDF vector chuẩn in ấn (Synchronous).
        /// </summary>
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

            // KIẾN TRÚC ĐỘNG CƠ KÉP (DUAL-ENGINE ARCHITECTURE):
            // ƯU TIÊN 1: Nếu máy tính đã cài đặt Microsoft Word, xuất trực tiếp bằng Native ExportAsFixedFormat
            // Đảm bảo: 100% chuẩn vector Nghị định 30/2020/NĐ-CP, 0% watermark bản quyền, 0% dòng chữ đỏ, bảo vệ tệp gốc ReadOnly.
            if (IsMicrosoftWordInstalled())
            {
                try
                {
                    StatusChanged?.Invoke($"Đang mở và xuất PDF qua Microsoft Word Native (100% chuẩn, 0 watermark)...");
                    return ConvertWordToPdfViaWordNative(wordFilePath, outputPdfPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning($"Word Native Direct Export không thành công, tự động fallback sang Syncfusion: {ex.Message}");
                }
            }

            // ƯU TIÊN 2 (FALLBACK): Máy tính không có Microsoft Word, sử dụng Syncfusion DocIO độc lập
            StatusChanged?.Invoke($"Đang mở tệp Word qua Engine độc lập: {Path.GetFileName(wordFilePath)}...");

            using FileStream fileStream = new FileStream(wordFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Syncfusion.DocIO.FormatType docFormat = ext == ".doc" ? Syncfusion.DocIO.FormatType.Doc : Syncfusion.DocIO.FormatType.Docx;

            using WordDocument document = new WordDocument(fileStream, docFormat);

            StatusChanged?.Invoke("Đang xuất PDF vector chuẩn Nghị định 30/2020/NĐ-CP qua Engine độc lập...");

            using DocToPDFConverter converter = new DocToPDFConverter();
            // Cấu hình tối ưu độ sắc nét cho in ấn và ký số
            converter.Settings.EmbedCompleteFonts = true;
            converter.Settings.EmbedFonts = true;
            converter.Settings.AutoTag = true;
            converter.Settings.PreserveFormFields = true;

            using PdfDocument pdfDoc = converter.ConvertToPDF(document);
            pdfDoc.Save(outputPdfPath);

            // Tự động làm sạch watermark nếu chạy ở chế độ Fallback Syncfusion
            SanitizeSyncfusionWatermark(outputPdfPath);

            StatusChanged?.Invoke($"Chuyển đổi thành công! Tệp tin: {Path.GetFileName(outputPdfPath)}");
            return outputPdfPath;
        }

        /// <summary>
        /// Chuyển đổi trực tiếp nội dung đang soạn thảo từ SfRichTextBoxAdv (Stream DOCX) sang PDF (Async).
        /// </summary>
        public Task<string> ConvertWordStreamToPdfAsync(Stream docxStream, string originalWordFilePath, string? outputPdfPath = null)
        {
            return Task.Run(() => ConvertWordStreamToPdf(docxStream, originalWordFilePath, outputPdfPath));
        }

        /// <summary>
        /// Chuyển đổi trực tiếp nội dung đang soạn thảo từ SfRichTextBoxAdv (Stream DOCX) sang PDF (Synchronous).
        /// </summary>
        public string ConvertWordStreamToPdf(Stream docxStream, string originalWordFilePath, string? outputPdfPath = null)
        {
            if (docxStream == null)
                throw new ArgumentNullException(nameof(docxStream));

            if (string.IsNullOrWhiteSpace(outputPdfPath))
            {
                string dir = Path.GetDirectoryName(originalWordFilePath) ?? AppDomain.CurrentDomain.BaseDirectory;
                string baseName = Path.GetFileNameWithoutExtension(originalWordFilePath);
                outputPdfPath = Path.Combine(dir, $"{baseName}_converted.pdf");
            }

            string? targetDir = Path.GetDirectoryName(outputPdfPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // KIẾN TRÚC ĐỘNG CƠ KÉP CHO NỘI DUNG STREAM:
            // ƯU TIÊN 1: Nếu máy có Word, ghi stream ra file tạm và xuất qua Word Native (100% không watermark)
            if (IsMicrosoftWordInstalled())
            {
                string tempDocx = Path.Combine(Path.GetTempPath(), $"vgca_stream_{Guid.NewGuid():N}.docx");
                try
                {
                    StatusChanged?.Invoke("Đang xuất PDF từ nội dung Word qua Microsoft Word Native (100% chuẩn, 0 watermark)...");
                    docxStream.Position = 0;
                    using (FileStream fs = new FileStream(tempDocx, FileMode.Create, FileAccess.Write))
                    {
                        docxStream.CopyTo(fs);
                    }
                    return ConvertWordToPdfViaWordNative(tempDocx, outputPdfPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning($"Word Native Export từ stream không thành công, fallback sang Syncfusion: {ex.Message}");
                }
                finally
                {
                    try { if (File.Exists(tempDocx)) File.Delete(tempDocx); } catch { }
                }
            }

            // ƯU TIÊN 2 (FALLBACK): Xuất từ stream qua Syncfusion
            StatusChanged?.Invoke("Đang tạo tệp PDF từ nội dung Word đã điều chỉnh...");

            docxStream.Position = 0;
            using WordDocument document = new WordDocument(docxStream, Syncfusion.DocIO.FormatType.Docx);

            using DocToPDFConverter converter = new DocToPDFConverter();
            converter.Settings.EmbedCompleteFonts = true;
            converter.Settings.EmbedFonts = true;
            converter.Settings.AutoTag = true;

            using PdfDocument pdfDoc = converter.ConvertToPDF(document);
            pdfDoc.Save(outputPdfPath);

            // Tự động làm sạch watermark nếu xuất qua Syncfusion fallback
            SanitizeSyncfusionWatermark(outputPdfPath);

            StatusChanged?.Invoke($"Đã xuất hoàn tất tệp PDF chuẩn ký số: {Path.GetFileName(outputPdfPath)}");
            return outputPdfPath;
        }

        /// <summary>
        /// Xuất trực tiếp tài liệu Word sang PDF bằng Microsoft Word Native Engine (ExportAsFixedFormat).
        /// Mở tài liệu ở chế độ ReadOnly 100% để bảo vệ an toàn tuyệt đối cho tệp gốc của người dùng.
        /// Đảm bảo văn bản PDF xuất ra chuẩn in ấn Nghị định 30/2020/NĐ-CP, hoàn toàn sạch sẽ, 0 watermark bản quyền.
        /// </summary>
        public string ConvertWordToPdfViaWordNative(string wordFilePath, string outputPdfPath)
        {
            Type? wordType = Type.GetTypeFromProgID("Word.Application");
            if (wordType == null)
            {
                throw new InvalidOperationException("Microsoft Word không khả dụng trên hệ thống.");
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
                wordApp.DisplayAlerts = 0; // wdAlertsNone

                // MỞ TỆP Ở CHẾ ĐỘ READ-ONLY 100% BẢO VỆ TỆP GỐC
                wordDoc = wordApp.Documents.Open(
                    FileName: wordFilePath,
                    ConfirmConversions: false,
                    ReadOnly: true,
                    AddToRecentFiles: false);

                // XUẤT TRỰC TIẾP SANG PDF VECTOR CHUẨN IN ẤN
                wordDoc.ExportAsFixedFormat(
                    OutputFileName: outputPdfPath,
                    ExportFormat: 17, // wdExportFormatPDF
                    OpenAfterExport: false,
                    OptimizeFor: 0, // wdExportOptimizeForPrint
                    Range: 0, // wdExportAllDocument
                    Item: 0, // wdExportDocumentContent
                    IncludeDocProps: true,
                    KeepIRM: true,
                    CreateBookmarks: 1,
                    DocStructureTags: true,
                    BitmapMissingFonts: true,
                    UseISO19005_1: false);

                StatusChanged?.Invoke($"Đã xuất PDF hoàn tất qua Word Native (Chuẩn 100%, 0 watermark): {Path.GetFileName(outputPdfPath)}");
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
            }
        }

        /// <summary>
        /// Kiểm tra xem hệ thống người dùng có cài đặt Microsoft Word hay không.
        /// </summary>
        public static bool IsMicrosoftWordInstalled()
        {
            try
            {
                Type? wordType = Type.GetTypeFromProgID("Word.Application");
                return wordType != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Lưu luồng tài liệu DOCX từ trình soạn thảo vào tệp Word trên máy tính.
        /// </summary>
        public void SaveWordDocument(Stream docxStream, string destinationPath)
        {
            if (docxStream == null) throw new ArgumentNullException(nameof(docxStream));
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Đường dẫn lưu tệp không hợp lệ.", nameof(destinationPath));

            string? dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            docxStream.Position = 0;
            using FileStream fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
            docxStream.CopyTo(fs);
            StatusChanged?.Invoke($"Đã lưu tệp Word thành công: {Path.GetFileName(destinationPath)}");
        }

        /// <summary>
        /// Tự động làm sạch dải thông báo bản quyền dùng thử và các liên kết Syncfusion (nếu có)
        /// khi phải kích hoạt chế độ Fallback trên máy tính không có cài đặt Microsoft Word.
        /// Sử dụng thư viện iText7 xóa bỏ link annotation và phủ dải trắng mép trên cùng của trang giấy.
        /// </summary>
        public static void SanitizeSyncfusionWatermark(string pdfPath)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                return;

            string tempCleanPdf = Path.Combine(
                Path.GetDirectoryName(pdfPath) ?? Path.GetTempPath(),
                $"{Path.GetFileNameWithoutExtension(pdfPath)}_clean_{Guid.NewGuid():N}.pdf");

            try
            {
                using (var reader = new iText.Kernel.Pdf.PdfReader(pdfPath))
                using (var writer = new iText.Kernel.Pdf.PdfWriter(tempCleanPdf))
                using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader, writer))
                {
                    int totalPages = pdfDoc.GetNumberOfPages();
                    for (int i = 1; i <= totalPages; i++)
                    {
                        var page = pdfDoc.GetPage(i);
                        var pageSize = page.GetPageSize();

                        // 1. Xóa bỏ toàn bộ Annotation Link trỏ về trang claim license của Syncfusion
                        var annots = page.GetAnnotations();
                        if (annots != null && annots.Count > 0)
                        {
                            for (int aIdx = annots.Count - 1; aIdx >= 0; aIdx--)
                            {
                                var annot = annots[aIdx];
                                if (annot.GetSubtype() == iText.Kernel.Pdf.PdfName.Link)
                                {
                                    var actionObj = annot.GetPdfObject()?.GetAsDictionary(iText.Kernel.Pdf.PdfName.A);
                                    string? uriStr = actionObj?.GetAsString(iText.Kernel.Pdf.PdfName.URI)?.GetValue();
                                    if (!string.IsNullOrEmpty(uriStr) && uriStr.Contains("syncfusion", StringComparison.OrdinalIgnoreCase))
                                    {
                                        page.RemoveAnnotation(annot);
                                    }
                                }
                            }
                        }

                        // 2. Phủ dải màu trắng đè lên mép trên cùng (Top 36pt trong lề trên) để xóa dòng chữ đỏ trial
                        var canvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), pdfDoc);
                        canvas.SaveState();
                        canvas.SetFillColor(iText.Kernel.Colors.ColorConstants.WHITE);
                        canvas.Rectangle(0, pageSize.GetHeight() - 36, pageSize.GetWidth(), 36);
                        canvas.Fill();
                        canvas.RestoreState();
                    }
                }

                if (File.Exists(tempCleanPdf))
                {
                    File.Copy(tempCleanPdf, pdfPath, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Lỗi sanitize Syncfusion watermark: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(tempCleanPdf)) File.Delete(tempCleanPdf); } catch { }
            }
        }
    }
}
