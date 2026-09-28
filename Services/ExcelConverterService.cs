using System;
using System.IO;
using System.Threading.Tasks;

namespace VGCASignToolPro.Services
{
    public class ExcelConverterService
    {
        public event Action<string>? StatusChanged;

        /// <summary>
        /// Đọc tệp Excel (.xlsx, .xls) và chuyển đổi thành chuỗi Base64 để nạp trực tiếp vào engine hiển thị bảng tính tương tác trên WebView2.
        /// </summary>
        public async Task<string> ReadExcelAsBase64Async(string excelFilePath)
        {
            if (string.IsNullOrWhiteSpace(excelFilePath) || !File.Exists(excelFilePath))
            {
                throw new FileNotFoundException($"Không tìm thấy tệp Excel: {excelFilePath}", excelFilePath);
            }

            StatusChanged?.Invoke($"Đang đọc cấu trúc dữ liệu bảng tính: {Path.GetFileName(excelFilePath)}...");

            byte[] fileBytes = await File.ReadAllBytesAsync(excelFilePath);
            if (fileBytes.Length == 0)
            {
                throw new InvalidDataException("Tệp bảng tính Excel có kích thước 0 byte hoặc bị rỗng.");
            }

            StatusChanged?.Invoke($"Đã nạp {fileBytes.Length / 1024} KB dữ liệu bảng tính. Sẵn sàng khởi tạo bảng xem & chỉnh sửa.");
            return Convert.ToBase64String(fileBytes);
        }

        /// <summary>
        /// Tạo đường dẫn tệp PDF đích an toàn từ tệp Excel nguồn, kiểm tra quyền ghi của thư mục.
        /// </summary>
        public string GenerateOutputPdfPath(string excelFilePath)
        {
            if (string.IsNullOrWhiteSpace(excelFilePath))
            {
                throw new ArgumentException("Đường dẫn tệp Excel không hợp lệ.", nameof(excelFilePath));
            }

            string baseDir = Path.GetDirectoryName(excelFilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            try
            {
                string testFile = Path.Combine(baseDir, $".perm_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch
            {
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            }

            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(excelFilePath);
            return Path.Combine(baseDir, $"{fileNameWithoutExt}_EXCEL.pdf");
        }
    }
}
