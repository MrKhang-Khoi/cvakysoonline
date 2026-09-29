using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VGCASignToolPro.Services
{
    public class ReportModel
    {
        [JsonProperty("reportId")]
        public string ReportId { get; set; } = string.Empty;

        [JsonProperty("title")]
        public string Title { get; set; } = string.Empty;

        [JsonProperty("senderName")]
        public string SenderName { get; set; } = string.Empty;

        [JsonProperty("senderCccd")]
        public string SenderCccd { get; set; } = string.Empty;

        [JsonProperty("approverName")]
        public string ApproverName { get; set; } = string.Empty;

        [JsonProperty("approverCccd")]
        public string ApproverCccd { get; set; } = string.Empty;

        [JsonProperty("status")]
        public string Status { get; set; } = "WAITING_OPEN";

        [JsonProperty("baseHash")]
        public string BaseHash { get; set; } = string.Empty;

        [JsonProperty("signedHash")]
        public string SignedHash { get; set; } = string.Empty;

        [JsonProperty("driveFileId")]
        public string DriveFileId { get; set; } = string.Empty;

        [JsonProperty("notes")]
        public string Notes { get; set; } = string.Empty;

        [JsonProperty("createdAt")]
        public long CreatedAt { get; set; }

        [JsonProperty("updatedAt")]
        public long UpdatedAt { get; set; }

        [JsonProperty("lockedBy")]
        public string LockedBy { get; set; } = string.Empty;

        [JsonProperty("lockedAt")]
        public long LockedAt { get; set; }

        [JsonProperty("signedBy")]
        public string SignedBy { get; set; } = string.Empty;

        [JsonProperty("signedAt")]
        public long SignedAt { get; set; }

        [JsonProperty("recalledAt")]
        public long RecalledAt { get; set; }

        // UI Helpers
        [JsonIgnore]
        public string CreatedTimeFormatted
        {
            get
            {
                if (CreatedAt <= 0) return "--:--";
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).ToLocalTime();
                return dt.ToString("dd/MM/yyyy HH:mm");
            }
        }

        [JsonIgnore]
        public string StatusText => Status switch
        {
            "WAITING_OPEN" => "Chưa mở (Chờ nhận)",
            "ACQUIRED_LOCKED" => "Đã nhận (Đang xem)",
            "SIGNED_COMMITTED" => "Đã duyệt & Ký số",
            "RECALLED" => "Đã thu hồi",
            "LOCK_TIMEOUT" => "Quá hạn 60 phút",
            _ => Status
        };

        [JsonIgnore]
        public string StatusBadgeColor => Status switch
        {
            "WAITING_OPEN" => "#F59E0B",     // Vàng hổ phách
            "ACQUIRED_LOCKED" => "#0284C7",  // Xanh dương
            "SIGNED_COMMITTED" => "#16A34A", // Xanh lá
            "RECALLED" => "#DC2626",         // Đỏ
            "LOCK_TIMEOUT" => "#64748B",     // Xám
            _ => "#475569"
        };

        [JsonIgnore]
        public string SignedHashDisplay => string.IsNullOrWhiteSpace(SignedHash) 
            ? "Chưa có (Chờ ký)" 
            : (SignedHash.Length > 16 ? $"{SignedHash[..6]}...{SignedHash[^6..]}" : SignedHash);

        [JsonIgnore]
        public string SignedHashColor => string.IsNullOrWhiteSpace(SignedHash) ? "#94A3B8" : "#0F766E";

        [JsonIgnore]
        public string SignedHashTooltip => string.IsNullOrWhiteSpace(SignedHash)
            ? "Văn bản chưa hoàn tất ký số cấp 2 (Chưa sinh mã băm H2)"
            : $"Mã băm toàn vẹn cấp 2 (H2 - SHA256):\n{SignedHash}";

        [JsonIgnore]
        public bool IsRecallable => Status == "WAITING_OPEN" || (Status == "ACQUIRED_LOCKED" && LockedAt > 0 && (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LockedAt > 3600000));

        [JsonIgnore]
        public bool CanApprove => Status == "WAITING_OPEN" || Status == "ACQUIRED_LOCKED" || Status == "LOCK_TIMEOUT";
    }

    public class ReportWorkflowService
    {
        private const string DefaultFirebaseUrl = "https://cva-kyso-lienthong-default-rtdb.asia-southeast1.firebasedatabase.app";
        private const long LockTimeoutMs = 60 * 60 * 1000; // 60 phút

        private readonly HttpClient _http;
        private readonly string _firebaseUrl;
        private readonly string _secret;

        public ReportWorkflowService(string? firebaseUrl = null, string? secret = null)
        {
            var config = LoadConfiguration();
            _firebaseUrl = (firebaseUrl ?? config.Url).TrimEnd('/');
            _secret = secret ?? config.Secret;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        private static (string Url, string Secret) LoadConfiguration()
        {
            string envUrl = Environment.GetEnvironmentVariable("CVA_FIREBASE_URL") ?? string.Empty;
            string envSecret = Environment.GetEnvironmentVariable("CVA_FIREBASE_SECRET") ?? string.Empty;

            if (!string.IsNullOrEmpty(envUrl) && !string.IsNullOrEmpty(envSecret))
            {
                return (envUrl, envSecret);
            }

            string[] searchPaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "workflow_config.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "workflow_config.json")
            };

            foreach (string p in searchPaths)
            {
                if (File.Exists(p))
                {
                    try
                    {
                        string json = File.ReadAllText(p, Encoding.UTF8);
                        var obj = JObject.Parse(json);
                        string url = obj["firebaseUrl"]?.ToString() ?? DefaultFirebaseUrl;
                        string sec = obj["databaseSecret"]?.ToString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(sec))
                        {
                            return (url, sec);
                        }
                    }
                    catch
                    {
                        // Bỏ qua lỗi cú pháp và chuyển sang cấu hình dự phòng
                    }
                }
            }

            return (!string.IsNullOrEmpty(envUrl) ? envUrl : DefaultFirebaseUrl, envSecret);
        }

        public static string ComputeSha256(string filePath)
        {
            if (!File.Exists(filePath)) return string.Empty;
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha256.ComputeHash(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        private string BuildUrl(string path)
        {
            if (string.IsNullOrEmpty(_secret))
            {
                return $"{_firebaseUrl}/{path}.json";
            }
            return $"{_firebaseUrl}/{path}.json?auth={_secret}";
        }

        /// <summary>
        /// Lấy toàn bộ danh sách báo cáo từ Firebase Realtime Database
        /// </summary>
        public async Task<List<ReportModel>> GetAllReportsAsync()
        {
            try
            {
                string url = BuildUrl("reports");
                var response = await _http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return new List<ReportModel>();
                }

                string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content) || content == "null")
                {
                    return new List<ReportModel>();
                }

                var dict = JsonConvert.DeserializeObject<Dictionary<string, ReportModel>>(content);
                if (dict == null) return new List<ReportModel>();

                var list = new List<ReportModel>();
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                foreach (var kvp in dict)
                {
                    var item = kvp.Value;
                    if (item == null) continue;
                    item.ReportId = kvp.Key;

                    // Kiểm tra tự động quá hạn 60 phút
                    if (item.Status == "ACQUIRED_LOCKED" && item.LockedAt > 0 && (now - item.LockedAt > LockTimeoutMs))
                    {
                        item.Status = "LOCK_TIMEOUT";
                    }

                    list.Add(item);
                }

                return list.OrderByDescending(r => r.UpdatedAt).ToList();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi GetAllReportsAsync: {ex}");
                return new List<ReportModel>();
            }
        }

        /// <summary>
        /// Gửi trình ký một báo cáo mới (Người lập nộp lên)
        /// </summary>
        public async Task<(bool Success, string Message, string ReportId)> SubmitReportAsync(
            string title,
            string senderName,
            string senderCccd,
            string approverName,
            string approverCccd,
            string localPdfPath,
            string notes = "")
        {
            try
            {
                if (!File.Exists(localPdfPath))
                {
                    return (false, "Tệp PDF không tồn tại trên máy tính.", string.Empty);
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string reportId = $"REP_{DateTime.Now:yyyyMMdd}_{now.ToString()[^6..]}";
                string baseHash = ComputeSha256(localPdfPath);

                // 1. Tải tệp PDF lên Firebase /files
                byte[] fileBytes = await File.ReadAllBytesAsync(localPdfPath).ConfigureAwait(false);
                string fileDataBase64 = Convert.ToBase64String(fileBytes);
                string fileId = $"FILE_{reportId}_V1";

                var filePayload = new
                {
                    fileName = Path.GetFileName(localPdfPath),
                    fileData = fileDataBase64,
                    fileSize = fileBytes.Length,
                    createdAt = now
                };

                string fileUrl = BuildUrl($"files/{fileId}");
                var fileContent = new StringContent(JsonConvert.SerializeObject(filePayload), Encoding.UTF8, "application/json");
                var fileRes = await _http.PutAsync(fileUrl, fileContent).ConfigureAwait(false);
                if (!fileRes.IsSuccessStatusCode)
                {
                    return (false, $"Lỗi khi lưu trữ tệp lên đám mây: {fileRes.StatusCode}", string.Empty);
                }

                // 2. Tạo bản ghi báo cáo trong /reports
                var report = new ReportModel
                {
                    ReportId = reportId,
                    Title = title,
                    SenderName = senderName,
                    SenderCccd = senderCccd,
                    ApproverName = approverName,
                    ApproverCccd = approverCccd,
                    Status = "WAITING_OPEN",
                    BaseHash = baseHash,
                    DriveFileId = fileId,
                    Notes = notes,
                    CreatedAt = now,
                    UpdatedAt = now
                };

                string reportUrl = BuildUrl($"reports/{reportId}");
                var reportContent = new StringContent(JsonConvert.SerializeObject(report), Encoding.UTF8, "application/json");
                var reportRes = await _http.PutAsync(reportUrl, reportContent).ConfigureAwait(false);

                if (!reportRes.IsSuccessStatusCode)
                {
                    return (false, $"Lỗi khi nộp báo cáo: {reportRes.StatusCode}", string.Empty);
                }

                return (true, "Đã gửi trình ký báo cáo thành công!", reportId);
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi hệ thống: {ex.Message}", string.Empty);
            }
        }

        /// <summary>
        /// Người duyệt nhấp mở file: Chiếm khóa ACQUIRED_LOCKED và tải file PDF về máy tạm
        /// </summary>
        public async Task<(bool Success, string Message, string? DownloadedFilePath, string? BaseHash)> AcquireReportForReviewAsync(
            string reportId,
            string approverCccd)
        {
            try
            {
                string reportUrl = BuildUrl($"reports/{reportId}");
                var getRes = await _http.GetAsync(reportUrl).ConfigureAwait(false);
                if (!getRes.IsSuccessStatusCode)
                {
                    return (false, "Không tìm thấy thông tin báo cáo trên hệ thống.", null, null);
                }

                string content = await getRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                var report = JsonConvert.DeserializeObject<ReportModel>(content);
                if (report == null)
                {
                    return (false, "Dữ liệu báo cáo bị lỗi.", null, null);
                }

                if (report.Status == "RECALLED")
                {
                    return (false, "Văn bản này đã được người lập thu hồi!", null, null);
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Kiểm tra xung đột khóa
                if (report.Status == "ACQUIRED_LOCKED")
                {
                    bool isExpired = report.LockedAt > 0 && (now - report.LockedAt > LockTimeoutMs);
                    if (!isExpired && !string.IsNullOrEmpty(report.LockedBy) && report.LockedBy != approverCccd)
                    {
                        return (false, "Báo cáo đang được xử lý bởi người duyệt khác!", null, null);
                    }
                }

                // Cập nhật trạng thái khóa
                var lockPayload = new
                {
                    status = "ACQUIRED_LOCKED",
                    lockedBy = approverCccd,
                    lockedAt = now,
                    updatedAt = now
                };
                var patchContent = new StringContent(JsonConvert.SerializeObject(lockPayload), Encoding.UTF8, "application/json");
                var patchRes = await _http.PatchAsync(reportUrl, patchContent).ConfigureAwait(false);
                if (!patchRes.IsSuccessStatusCode)
                {
                    return (false, "Không thể khóa báo cáo để xử lý.", null, null);
                }

                // Tải nội dung tệp PDF
                string fileUrl = BuildUrl($"files/{report.DriveFileId}");
                var fileRes = await _http.GetAsync(fileUrl).ConfigureAwait(false);
                if (!fileRes.IsSuccessStatusCode)
                {
                    return (false, "Không thể tải tệp đính kèm từ máy chủ.", null, null);
                }

                string fileJson = await fileRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                var fileObj = JObject.Parse(fileJson);
                string? base64Data = fileObj["fileData"]?.ToString();

                if (string.IsNullOrEmpty(base64Data))
                {
                    return (false, "Nội dung tệp đính kèm rỗng.", null, null);
                }

                byte[] pdfBytes = Convert.FromBase64String(base64Data);
                string tempDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro_Reports");
                Directory.CreateDirectory(tempDir);
                string localFilePath = Path.Combine(tempDir, $"{reportId}_Review.pdf");
                await File.WriteAllBytesAsync(localFilePath, pdfBytes).ConfigureAwait(false);
                WordConverterService.SanitizeSyncfusionWatermark(localFilePath);

                return (true, "Đã tải văn bản thành công.", localFilePath, report.BaseHash);
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi khi mở báo cáo: {ex.Message}", null, null);
            }
        }

        /// <summary>
        /// Người lập thu hồi báo cáo đã gửi
        /// </summary>
        public async Task<(bool Success, string Message)> RecallReportAsync(string reportId, string senderCccd)
        {
            try
            {
                string reportUrl = BuildUrl($"reports/{reportId}");
                var getRes = await _http.GetAsync(reportUrl).ConfigureAwait(false);
                if (!getRes.IsSuccessStatusCode)
                {
                    return (false, "Không tìm thấy báo cáo cần thu hồi.");
                }

                string content = await getRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                var report = JsonConvert.DeserializeObject<ReportModel>(content);
                if (report == null) return (false, "Dữ liệu báo cáo bị lỗi.");

                if (report.SenderCccd != senderCccd)
                {
                    return (false, "Bạn không phải người lập báo cáo này nên không thể thu hồi.");
                }

                if (report.Status == "SIGNED_COMMITTED")
                {
                    return (false, "Không thể thu hồi: Báo cáo đã được ký duyệt hoàn tất!");
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (report.Status == "ACQUIRED_LOCKED")
                {
                    bool isExpired = report.LockedAt > 0 && (now - report.LockedAt > LockTimeoutMs);
                    if (!isExpired)
                    {
                        return (false, "Không thể thu hồi: Người duyệt đang mở xử lý văn bản này!");
                    }
                }

                var updatePayload = new
                {
                    status = "RECALLED",
                    recalledAt = now,
                    updatedAt = now
                };
                var patchContent = new StringContent(JsonConvert.SerializeObject(updatePayload), Encoding.UTF8, "application/json");
                var patchRes = await _http.PatchAsync(reportUrl, patchContent).ConfigureAwait(false);
                if (!patchRes.IsSuccessStatusCode)
                {
                    return (false, "Không thể cập nhật trạng thái thu hồi trên máy chủ.");
                }

                return (true, "Đã thu hồi báo cáo thành công!");
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi hệ thống: {ex.Message}");
            }
        }

        /// <summary>
        /// Người duyệt ký xong và nộp bản v2 hoàn tất (Có chốt chặn Base Hash chống ghi đè)
        /// </summary>
        public async Task<(bool Success, string Message)> CommitSignedReportAsync(
            string reportId,
            string approverCccd,
            string signedPdfPath,
            string expectedBaseHash)
        {
            try
            {
                if (!File.Exists(signedPdfPath))
                {
                    return (false, "Tệp PDF đã ký không tồn tại.");
                }

                string reportUrl = BuildUrl($"reports/{reportId}");
                var getRes = await _http.GetAsync(reportUrl).ConfigureAwait(false);
                if (!getRes.IsSuccessStatusCode)
                {
                    return (false, "Không tìm thấy thông tin báo cáo.");
                }

                string content = await getRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                var report = JsonConvert.DeserializeObject<ReportModel>(content);
                if (report == null) return (false, "Dữ liệu báo cáo bị lỗi.");

                // Chốt chặn 1: Không được nộp nếu đã bị thu hồi
                if (report.Status == "RECALLED")
                {
                    return (false, "Từ chối nộp: Báo cáo này đã bị người lập thu hồi trước đó!");
                }

                // Chốt chặn 2: Cryptographic Base Hash Check (Anti-Split-Brain)
                if (!string.IsNullOrEmpty(report.BaseHash) && report.BaseHash != expectedBaseHash)
                {
                    return (false, "Xung đột phiên bản: Tệp gốc trên hệ thống đã thay đổi hoặc cập nhật phiên bản mới!");
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string signedHash = ComputeSha256(signedPdfPath);

                // Lưu tệp PDF v2 đã có 2 chữ ký vào /files
                byte[] signedBytes = await File.ReadAllBytesAsync(signedPdfPath).ConfigureAwait(false);
                string fileDataBase64 = Convert.ToBase64String(signedBytes);
                string fileIdV2 = $"FILE_{reportId}_V2_SIGNED";

                var filePayload = new
                {
                    fileName = $"{report.Title}_DaKyDuyet.pdf",
                    fileData = fileDataBase64,
                    fileSize = signedBytes.Length,
                    createdAt = now
                };

                string fileUrl = BuildUrl($"files/{fileIdV2}");
                var fileContent = new StringContent(JsonConvert.SerializeObject(filePayload), Encoding.UTF8, "application/json");
                var putRes = await _http.PutAsync(fileUrl, fileContent).ConfigureAwait(false);
                if (!putRes.IsSuccessStatusCode)
                {
                    return (false, "Không thể lưu trữ tệp đã ký lên máy chủ đám mây.");
                }

                // Cập nhật trạng thái SIGNED_COMMITTED
                var commitPayload = new
                {
                    status = "SIGNED_COMMITTED",
                    signedBy = approverCccd,
                    signedHash = signedHash,
                    driveFileId = fileIdV2,
                    signedAt = now,
                    updatedAt = now
                };

                var patchContent = new StringContent(JsonConvert.SerializeObject(commitPayload), Encoding.UTF8, "application/json");
                var patchRes = await _http.PatchAsync(reportUrl, patchContent).ConfigureAwait(false);
                if (!patchRes.IsSuccessStatusCode)
                {
                    return (false, "Không thể hoàn tất trạng thái phê duyệt trên máy chủ.");
                }

                return (true, "Phê duyệt và ký số báo cáo thành công!");
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi hệ thống: {ex.Message}");
            }
        }

        /// <summary>
        /// Tải tệp PDF từ đám mây về đường dẫn cục bộ
        /// </summary>
        public async Task<bool> DownloadFileAsync(string fileId, string destinationPath)
        {
            try
            {
                string fileUrl = BuildUrl($"files/{fileId}");
                var res = await _http.GetAsync(fileUrl).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) return false;

                string json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                var jObj = JObject.Parse(json);
                string? base64 = jObj["fileData"]?.ToString();
                if (string.IsNullOrEmpty(base64)) return false;

                byte[] bytes = Convert.FromBase64String(base64);
                string? dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                await File.WriteAllBytesAsync(destinationPath, bytes).ConfigureAwait(false);
                WordConverterService.SanitizeSyncfusionWatermark(destinationPath);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi DownloadFileAsync: {ex}");
                return false;
            }
        }
    }
}
