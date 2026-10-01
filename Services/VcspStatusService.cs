using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace VGCASignToolPro.Services
{
    public enum VcspState
    {
        NotInstalled,       // Chưa cài đặt Virtual CSP
        NotRunning,         // Ứng dụng vgca_vcsp_v2_mgr.exe chưa được mở
        LoggedOut,          // Ứng dụng đang mở nhưng chưa đăng nhập hoặc token đã hết hạn (lỗi 3006)
        LoggedIn            // Đang mở và đã đăng nhập thành công (token hợp lệ)
    }

    public class VcspStatusInfo
    {
        public VcspState State { get; set; } = VcspState.NotInstalled;
        public bool IsInstalled => State != VcspState.NotInstalled;
        public bool IsRunning => State == VcspState.NotRunning || State == VcspState.LoggedOut || State == VcspState.LoggedIn;
        public bool IsReadyToSign => State == VcspState.LoggedIn;
        public string StatusTitle { get; set; } = string.Empty;
        public string StatusDescription { get; set; } = string.Empty;
        public string? ActionText { get; set; }
    }

    public static class VcspStatusService
    {
        private static readonly string VGCAManagerExePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "VGCA", "Virtual CSP v2.0", "vgca_vcsp_v2_mgr.exe");

        private static readonly string AlternateManagerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "VGCA", "Virtual CSP v2.0", "vgca_vcsp_v2_mgr.exe");

        private static readonly string LogsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Crypto", "RemoteSigningKSP", "VGCA", "Virtual CSP v2.0", "Logs");

        public static bool IsInstalled()
        {
            return File.Exists(VGCAManagerExePath) || File.Exists(AlternateManagerPath);
        }

        public static bool IsProcessRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName("vgca_vcsp_v2_mgr");
                return processes != null && processes.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool LaunchVirtualCsp()
        {
            string? targetPath = File.Exists(VGCAManagerExePath) ? VGCAManagerExePath :
                                 (File.Exists(AlternateManagerPath) ? AlternateManagerPath : null);

            if (string.IsNullOrEmpty(targetPath)) return false;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Lỗi khởi chạy Virtual CSP: {ex.Message}");
                return false;
            }
        }

        public static VcspStatusInfo CheckStatus()
        {
            if (!IsInstalled())
            {
                return new VcspStatusInfo
                {
                    State = VcspState.NotInstalled,
                    StatusTitle = "Chưa Cài Đặt VGCA Virtual CSP",
                    StatusDescription = "Không tìm thấy phần mềm VGCA Virtual CSP v2.0 của Ban Cơ Yếu Chính Phủ trên máy tính này.",
                    ActionText = "Cài đặt Virtual CSP"
                };
            }

            if (!IsProcessRunning())
            {
                return new VcspStatusInfo
                {
                    State = VcspState.NotRunning,
                    StatusTitle = "Virtual CSP Chưa Được Khởi Động",
                    StatusDescription = "Ứng dụng VGCA Virtual CSP v2.0 chưa được mở trên máy tính. Bạn cần mở ứng dụng và đăng nhập tài khoản trước khi thực hiện ký số.",
                    ActionText = "Mở Virtual CSP"
                };
            }

            // Tiến trình đang chạy -> Kiểm tra phiên đăng nhập thực tế qua Logs
            bool isLoggedIn = CheckIsSessionActiveFromLogs();

            if (!isLoggedIn)
            {
                return new VcspStatusInfo
                {
                    State = VcspState.LoggedOut,
                    StatusTitle = "Virtual CSP Đang Mở Nhưng Chưa Đăng Nhập",
                    StatusDescription = "Ứng dụng Virtual CSP đang hoạt động nhưng chưa đăng nhập (hoặc phiên làm việc Bearer Token đã hết hạn). Vui lòng chuyển sang cửa sổ Virtual CSP để đăng nhập.",
                    ActionText = "Xem Virtual CSP"
                };
            }

            return new VcspStatusInfo
            {
                State = VcspState.LoggedIn,
                StatusTitle = "Virtual CSP Đã Sẵn Sàng",
                StatusDescription = "Phiên làm việc ký số từ xa của Ban Cơ Yếu Chính Phủ đang hoạt động hợp lệ. Đã sẵn sàng ký số.",
                ActionText = null
            };
        }

        private static bool CheckIsSessionActiveFromLogs()
        {
            try
            {
                if (!Directory.Exists(LogsDirectory)) return false;

                // Lấy các file log được sửa đổi gần nhất (trong vòng 24 giờ qua)
                var logFiles = Directory.GetFiles(LogsDirectory, "*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(4)
                    .ToList();

                if (logFiles.Count == 0) return false;

                // Quét ngược từ file mới nhất
                foreach (var logFile in logFiles)
                {
                    if (logFile.Length == 0) continue;

                    var lastLines = ReadTailLines(logFile.FullName, 150);
                    // Duyệt từ dưới lên
                    for (int i = lastLines.Count - 1; i >= 0; i--)
                    {
                        string line = lastLines[i];

                        // Kiểm tra lỗi hết hạn Bearer token (lỗi 3006)
                        if (line.Contains("\"error\":3006") || line.Contains("BEARER TOKEN") || line.Contains("SignHash failed 0x8009002D"))
                        {
                            return false;
                        }

                        // Kiểm tra hành động đăng xuất (revoke)
                        if (line.Contains("reqBody {revoke}") || line.Contains("RESTful return {revoke}: 200"))
                        {
                            return false;
                        }

                        // Kiểm tra đăng nhập thành công
                        if (line.Contains("\"error\":0") &&
                            (line.Contains("\"accessToken\"") || line.Contains("\"ownerInfo\"") || line.Contains("\"SAD\"") || line.Contains("THA?NH CA\"NG") || line.Contains("THÀNH CÔNG")))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Lỗi kiểm tra log Virtual CSP: {ex.Message}");
                return false;
            }
        }

        private static List<string> ReadTailLines(string filePath, int maxLines)
        {
            var lines = new List<string>();
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);

                var buffer = new Queue<string>(maxLines);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (buffer.Count >= maxLines)
                    {
                        buffer.Dequeue();
                    }
                    buffer.Enqueue(line);
                }

                lines.AddRange(buffer);
            }
            catch
            {
                // Bỏ qua nếu không đọc được file
            }
            return lines;
        }
    }
}
