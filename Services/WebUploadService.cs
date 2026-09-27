using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using VGCASignToolPro.Models;

namespace VGCASignToolPro.Services
{
    public class WebUploadService
    {
        public event Action<string>? StatusChanged;

        public async Task<UploadResult> UploadSignedPdfAsync(UploadRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.FilePath) || !File.Exists(request.FilePath))
                throw new FileNotFoundException("Tệp tin PDF cần gửi không tồn tại.", request.FilePath);
            if (string.IsNullOrWhiteSpace(request.UploadUrl))
                throw new ArgumentException("Chưa cung cấp địa chỉ URL máy chủ Web tiếp nhận.", nameof(request));

            StatusChanged?.Invoke($"Đang chuẩn bị gói tin mạng gửi tới: {request.UploadUrl}...");

            var stopwatch = Stopwatch.StartNew();

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            // Gán Authorization Header nếu có
            if (!string.IsNullOrWhiteSpace(request.AuthToken))
            {
                if (request.AuthToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    httpClient.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", request.AuthToken.Substring(7).Trim());
                }
                else
                {
                    httpClient.DefaultRequestHeaders.Add("Authorization", request.AuthToken);
                }
            }

            // Gán các Headers tùy chỉnh nếu có
            foreach (var header in request.ExtraHeaders)
            {
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var form = new MultipartFormDataContent();

            // Đính kèm tệp PDF đã ký
            byte[] fileBytes = await File.ReadAllBytesAsync(request.FilePath);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            string fileName = Path.GetFileName(request.FilePath);
            form.Add(fileContent, "file", fileName);

            // Đính kèm các trường thông tin metadata
            if (!string.IsNullOrWhiteSpace(request.DocumentTitle))
                form.Add(new StringContent(request.DocumentTitle), "title");

            if (!string.IsNullOrWhiteSpace(request.SignerName))
                form.Add(new StringContent(request.SignerName), "signer");

            if (!string.IsNullOrWhiteSpace(request.Department))
                form.Add(new StringContent(request.Department), "department");

            if (!string.IsNullOrWhiteSpace(request.OriginalFileName))
                form.Add(new StringContent(request.OriginalFileName), "original_file");

            form.Add(new StringContent(DateTime.Now.ToString("o")), "upload_time");
            form.Add(new StringContent(fileBytes.Length.ToString()), "file_size");

            // Đính kèm các trường form tùy biến
            foreach (var field in request.ExtraFormData)
            {
                form.Add(new StringContent(field.Value), field.Key);
            }

            StatusChanged?.Invoke($"Đang tải tệp ({fileBytes.Length:N0} bytes) lên máy chủ Web...");

            try
            {
                var response = await httpClient.PostAsync(request.UploadUrl, form);
                stopwatch.Stop();

                string responseBody = await response.Content.ReadAsStringAsync();
                int statusCode = (int)response.StatusCode;

                StatusChanged?.Invoke($"Máy chủ phản hồi HTTP {statusCode} ({response.ReasonPhrase}) trong {stopwatch.ElapsedMilliseconds}ms");

                return new UploadResult
                {
                    IsSuccess = response.IsSuccessStatusCode,
                    StatusCode = statusCode,
                    ResponseBody = responseBody,
                    ServerMessage = response.ReasonPhrase,
                    ResponseTimeMs = stopwatch.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                StatusChanged?.Invoke($"Lỗi kết nối mạng: {ex.Message}");
                return new UploadResult
                {
                    IsSuccess = false,
                    StatusCode = 0,
                    ResponseBody = $"Lỗi gửi gói tin: {ex.Message}",
                    ServerMessage = ex.GetType().Name,
                    ResponseTimeMs = stopwatch.ElapsedMilliseconds
                };
            }
        }
    }
}
