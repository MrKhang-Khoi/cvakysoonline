using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VGCASignToolPro.Models;

namespace VGCASignToolPro.Services
{
    public class UpdateService
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private readonly string _configFilePath;
        public string CurrentVersionString { get; private set; } = "2.6.5";

        public UpdateService()
        {
            _configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow_config.json");
            try
            {
                Version? asmVer = Assembly.GetExecutingAssembly().GetName().Version;
                if (asmVer != null && asmVer.Major > 0)
                {
                    CurrentVersionString = $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}";
                }
            }
            catch { }
        }

        /// <summary>
        /// Kiểm tra phiên bản mới từ Firebase Realtime Database (Control Plane)
        /// </summary>
        public async Task<UpdateCheckResult> CheckForUpdateAsync()
        {
            try
            {
                string firebaseUrl = string.Empty;
                string dbSecret = string.Empty;

                if (File.Exists(_configFilePath))
                {
                    try
                    {
                        string configJson = await File.ReadAllTextAsync(_configFilePath, Encoding.UTF8);
                        JObject cfg = JObject.Parse(configJson);
                        firebaseUrl = (cfg["firebaseUrl"]?.ToString() ?? string.Empty).Trim().TrimEnd('/');
                        dbSecret = (cfg["databaseSecret"]?.ToString() ?? string.Empty).Trim();
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(firebaseUrl))
                {
                    // URL public fallback — không hardcode secret, /app_release phải public read
                    firebaseUrl = "https://cva-kyso-lienthong-default-rtdb.asia-southeast1.firebasedatabase.app";
                    dbSecret = string.Empty;
                }

                string endpoint = $"{firebaseUrl}/app_release.json" +
                    (!string.IsNullOrEmpty(dbSecret) ? $"?auth={dbSecret}" : "");

                using var response = await _httpClient.GetAsync(endpoint);
                if (!response.IsSuccessStatusCode)
                {
                    return new UpdateCheckResult
                    {
                        Success = false,
                        CurrentVersion = CurrentVersionString,
                        Message = $"Không thể kết nối máy chủ Firebase (HTTP {(int)response.StatusCode})."
                    };
                }

                string jsonContent = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(jsonContent) || jsonContent == "null")
                {
                    return new UpdateCheckResult
                    {
                        Success = true,
                        HasUpdate = false,
                        CurrentVersion = CurrentVersionString,
                        Message = "Bạn đang sử dụng phiên bản chuẩn mới nhất."
                    };
                }

                AppReleaseModel? release = JsonConvert.DeserializeObject<AppReleaseModel>(jsonContent);
                if (release == null || string.IsNullOrWhiteSpace(release.Version))
                {
                    return new UpdateCheckResult
                    {
                        Success = true,
                        HasUpdate = false,
                        CurrentVersion = CurrentVersionString,
                        Message = "Không có thông tin bản phát hành mới trên máy chủ."
                    };
                }

                // Kiểm tra công tắc khẩn cấp Kill-Switch
                if (!string.Equals(release.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    return new UpdateCheckResult
                    {
                        Success = true,
                        HasUpdate = false,
                        CurrentVersion = CurrentVersionString,
                        Message = "Bản phát hành trên máy chủ đang tạm ngừng để bảo trì."
                    };
                }

                // So sánh phiên bản Semantic Versioning
                bool isNewer = CompareVersions(release.Version, CurrentVersionString) > 0;
                bool isMandatory = release.Mandatory;

                if (!string.IsNullOrEmpty(release.MinSupportedVersion))
                {
                    if (CompareVersions(CurrentVersionString, release.MinSupportedVersion) < 0)
                    {
                        isMandatory = true;
                    }
                }

                return new UpdateCheckResult
                {
                    Success = true,
                    HasUpdate = isNewer,
                    IsMandatory = isMandatory,
                    CurrentVersion = CurrentVersionString,
                    LatestVersion = release.Version,
                    Release = release,
                    Message = isNewer
                        ? $"Đã phát hiện bản cập nhật mới: v{release.Version}"
                        : "Bạn đang sử dụng phiên bản mới nhất."
                };
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult
                {
                    Success = false,
                    CurrentVersion = CurrentVersionString,
                    Message = $"Lỗi kiểm tra cập nhật: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Tải gói cập nhật nhị phân từ GitHub Releases / Cloud Storage với báo cáo tiến trình (hỗ trợ chuyển hướng máy chủ dự phòng)
        /// </summary>
        public async Task<string> DownloadUpdatePackageAsync(
            string downloadUrl,
            string? fallbackUrl,
            IProgress<UpdateDownloadProgress>? progress,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl) && string.IsNullOrWhiteSpace(fallbackUrl))
            {
                throw new ArgumentException("Đường dẫn tải gói cập nhật không hợp lệ.", nameof(downloadUrl));
            }

            string primaryUrl = !string.IsNullOrWhiteSpace(downloadUrl) ? downloadUrl : fallbackUrl!;
            string? secondaryUrl = !string.IsNullOrWhiteSpace(downloadUrl) && !string.IsNullOrWhiteSpace(fallbackUrl) && downloadUrl != fallbackUrl
                ? fallbackUrl
                : null;

            try
            {
                return await DownloadInternalAsync(primaryUrl, progress, ct);
            }
            catch (Exception ex) when (secondaryUrl != null && !ct.IsCancellationRequested)
            {
                System.Diagnostics.Trace.TraceWarning($"Tải từ máy chủ chính ({primaryUrl}) thất bại: {ex.Message}. Đang chuyển sang máy chủ dự phòng: {secondaryUrl}");
                return await DownloadInternalAsync(secondaryUrl, progress, ct);
            }
        }

        public Task<string> DownloadUpdatePackageAsync(
            string downloadUrl,
            IProgress<UpdateDownloadProgress>? progress,
            CancellationToken ct = default)
        {
            return DownloadUpdatePackageAsync(downloadUrl, null, progress, ct);
        }

        private async Task<string> DownloadInternalAsync(
            string downloadUrl,
            IProgress<UpdateDownloadProgress>? progress,
            CancellationToken ct)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro", "Updates");
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }

            string randSuffix = Guid.NewGuid().ToString("N").Substring(0, 6);
            string targetZipPath = Path.Combine(tempDir, $"update_package_{DateTime.Now:yyyyMMdd_HHmmss}_{randSuffix}.zip");

            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            // Thiết lập User-Agent tiêu chuẩn để GitHub Release CDN không từ chối hoặc chặn response stream
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) VGCASignToolPro/2.6.0");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;

            // Nếu content-length vắng mặt trên redirect URL, cố gắng lấy từ HEAD hoặc ước lượng
            if ((!totalBytes.HasValue || totalBytes.Value <= 0) && response.Headers.Location != null)
            {
                try
                {
                    using var headReq = new HttpRequestMessage(HttpMethod.Head, response.Headers.Location);
                    using var headRes = await _httpClient.SendAsync(headReq, ct).ConfigureAwait(false);
                    if (headRes.Content.Headers.ContentLength.HasValue && headRes.Content.Headers.ContentLength.Value > 0)
                    {
                        totalBytes = headRes.Content.Headers.ContentLength;
                    }
                }
                catch { }
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(targetZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            var stopwatch = Stopwatch.StartNew();
            long lastReportTime = 0;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                totalRead += bytesRead;

                long now = stopwatch.ElapsedMilliseconds;
                // Báo cáo định kỳ mỗi 50ms hoặc khi đọc xong để cập nhật mượt trên UI
                if (progress != null && (now - lastReportTime >= 50 || bytesRead == 0))
                {
                    lastReportTime = now;
                    int percentage = totalBytes.HasValue && totalBytes.Value > 0
                        ? (int)((double)totalRead / totalBytes.Value * 100)
                        : 0;

                    double speedKbs = totalRead / 1024.0 / Math.Max(0.1, stopwatch.Elapsed.TotalSeconds);

                    progress.Report(new UpdateDownloadProgress
                    {
                        BytesReceived = totalRead,
                        TotalBytesToReceive = totalBytes,
                        ProgressPercentage = Math.Min(100, percentage),
                        SpeedKbs = speedKbs
                    });
                }
            }

            // Đảm bảo báo cáo tiến trình 100% khi hoàn tất tải file
            if (progress != null)
            {
                progress.Report(new UpdateDownloadProgress
                {
                    BytesReceived = totalRead,
                    TotalBytesToReceive = totalBytes ?? totalRead,
                    ProgressPercentage = 100,
                    SpeedKbs = totalRead / 1024.0 / Math.Max(0.1, stopwatch.Elapsed.TotalSeconds)
                });
            }

            return targetZipPath;
        }

        /// <summary>
        /// Xác thực tính toàn vẹn gói cập nhật bằng mã băm mật mã học SHA-256 đối soát từ Firebase
        /// </summary>
        public bool VerifyPackageSha256(string zipFilePath, string expectedSha256)
        {
            // Bug #9 fix: expectedSha256 rỗng = server chưa set hash = FAIL an toàn, không phải pass
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                System.Diagnostics.Trace.TraceWarning("[UpdateService] Không có SHA-256 trên server — từ chối xác nhận gói cập nhật để bảo vệ toàn vẹn.");
                return false;
            }

            if (!File.Exists(zipFilePath)) return false;

            try
            {
                using var sha256 = SHA256.Create();
                using var stream = File.OpenRead(zipFilePath);
                byte[] hashBytes = sha256.ComputeHash(stream);
                string computedHash = Convert.ToHexString(hashBytes);

                return string.Equals(computedHash, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Giải nén gói cập nhật vào thư mục staging và khởi chạy trình Updater độc lập
        /// </summary>
        public bool LaunchExternalUpdater(string zipFilePath, string restartExePath)
        {
            try
            {
                string stagingDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro", "Updates", $"staging_{Guid.NewGuid():N}");
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
                Directory.CreateDirectory(stagingDir);

                AppLogger.Info($"[UpdateService] Bắt đầu giải nén gói cập nhật '{zipFilePath}' vào thư mục tạm '{stagingDir}'...", "UpdateService");

                // 1. Giải nén gói update vào thư mục staging
                ZipFile.ExtractToDirectory(zipFilePath, stagingDir, true);

                // 2. ƯU TIÊN KHỞI CHẠY UPDATER TỪ THƯ MỤC TẠM (STAGING HOẶC TEMP) ĐỂ KHÔNG BAO GIỜ KHÓA DLL TRONG APPDIR
                string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string stagingUpdater = Path.Combine(stagingDir, "VGCASignToolPro.Updater.exe");
                string stagingNewUpdater = Path.Combine(stagingDir, "VGCASignToolPro.Updater.new.exe");
                string updaterToRun;
                string workingDirToRun;

                if (File.Exists(stagingUpdater))
                {
                    updaterToRun = stagingUpdater;
                    workingDirToRun = stagingDir;
                    AppLogger.Info($"[UpdateService] Phát hiện VGCASignToolPro.Updater.exe trong staging: '{updaterToRun}'", "UpdateService");
                }
                else if (File.Exists(stagingNewUpdater))
                {
                    // Hỗ trợ gói patch có tiền tố .new
                    try
                    {
                        File.Copy(stagingNewUpdater, stagingUpdater, true);
                        string stagingNewDll = Path.Combine(stagingDir, "VGCASignToolPro.Updater.new.dll");
                        string stagingDll = Path.Combine(stagingDir, "VGCASignToolPro.Updater.dll");
                        if (File.Exists(stagingNewDll)) File.Copy(stagingNewDll, stagingDll, true);
                    }
                    catch { }

                    updaterToRun = File.Exists(stagingUpdater) ? stagingUpdater : stagingNewUpdater;
                    workingDirToRun = stagingDir;
                    AppLogger.Info($"[UpdateService] Kích hoạt Updater từ tệp patch staging: '{updaterToRun}'", "UpdateService");
                }
                else
                {
                    string fallbackUpdater = Path.Combine(appDir, "VGCASignToolPro.Updater.exe");
                    if (File.Exists(fallbackUpdater))
                    {
                        // Copy updater VÀ TOÀN BỘ CÁC TỆP PHỤ TRỢ (DLL, JSON RUNTIME) sang thư mục tạm để không bị lỗi HostFxr 0x8000809A
                        string tempIsolatedUpdaterDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro", $"UpdaterIsolated_{Guid.NewGuid():N}");
                        Directory.CreateDirectory(tempIsolatedUpdaterDir);

                        foreach (var f in Directory.GetFiles(appDir, "*.*"))
                        {
                            string fname = Path.GetFileName(f);
                            if (fname.StartsWith("VGCASignToolPro.Updater", StringComparison.OrdinalIgnoreCase) ||
                                fname.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                fname.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    File.Copy(f, Path.Combine(tempIsolatedUpdaterDir, fname), true);
                                }
                                catch { }
                            }
                        }

                        string tempUpdaterExe = Path.Combine(tempIsolatedUpdaterDir, "VGCASignToolPro.Updater.exe");
                        updaterToRun = tempUpdaterExe;
                        workingDirToRun = tempIsolatedUpdaterDir;
                        AppLogger.Info($"[UpdateService] Khởi chạy Updater qua vùng cách ly độc lập: '{updaterToRun}'", "UpdateService");
                    }
                    else
                    {
                        AppLogger.Error("[UpdateService] Không tìm thấy tệp VGCASignToolPro.Updater.exe ở cả staging lẫn appDir!", null, "UpdateService");
                        throw new FileNotFoundException("Không tìm thấy tệp VGCASignToolPro.Updater.exe để thực hiện cập nhật.");
                    }
                }

                int currentPid = Environment.ProcessId;

                // 3. Khởi chạy tiến trình Updater độc lập ngoài vùng cài đặt gốc
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = updaterToRun,
                    Arguments = $"--pid {currentPid} --source \"{stagingDir}\" --target \"{appDir}\" --restart \"{restartExePath}\"",
                    UseShellExecute = true,
                    WorkingDirectory = workingDirToRun
                };

                AppLogger.Info($"[UpdateService] Khởi động tiến trình Updater: '{updaterToRun}' với đối số '{psi.Arguments}'", "UpdateService");
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("[UpdateService] Lỗi nghiêm trọng khi kích hoạt Updater độc lập", ex, "UpdateService");
                System.Diagnostics.Trace.TraceError($"Lỗi kích hoạt Updater độc lập: {ex}");
                return false;
            }
        }

        private static int CompareVersions(string v1, string v2)
        {
            if (Version.TryParse(v1, out var ver1) && Version.TryParse(v2, out var ver2))
            {
                return ver1.CompareTo(ver2);
            }

            // Fallback so sánh chuỗi số
            string[] parts1 = v1.Split('.');
            string[] parts2 = v2.Split('.');
            int maxLen = Math.Max(parts1.Length, parts2.Length);

            for (int i = 0; i < maxLen; i++)
            {
                int p1 = i < parts1.Length && int.TryParse(parts1[i], out int n1) ? n1 : 0;
                int p2 = i < parts2.Length && int.TryParse(parts2[i], out int n2) ? n2 : 0;
                if (p1 != p2) return p1.CompareTo(p2);
            }

            return 0;
        }
    }
}
