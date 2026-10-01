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
            catch (Exception ex)
            {
                Trace.TraceWarning($"Lỗi đọc đuôi tệp log: {ex.Message}");
            }
            return lines;
        }

        /// <summary>
        /// Phân tích nguyên nhân sâu xa khi gặp lỗi ký số (CryptographicException / Win32 / KSP RSSP).
        /// Đọc trực tiếp nhật ký KSP thời gian thực để phân biệt chính xác:
        /// - Lỗi mạng WinHttp 12002 Timeout (Máy chủ Ban Cơ Yếu không phản hồi -> không phát tín hiệu về điện thoại)
        /// - Lỗi phiên làm việc Bearer Token hết hạn (3006 / 0x8009002D)
        /// - Lỗi người dùng hủy hoặc hết hạn chờ Smart OTP (0x8010006E)
        /// - Lỗi không tìm thấy Private Key (0x80090016)
        /// - Lỗi mã PIN (0x80090020)
        /// </summary>
        public static (string Title, string Message, string DiagnosticDetails) ExplainSigningError(Exception ex, bool isVirtualCsp)
        {
            if (isVirtualCsp)
            {
                try
                {
                    if (Directory.Exists(LogsDirectory))
                    {
                        var kspLogFiles = Directory.GetFiles(LogsDirectory, "*vcsp11*.log")
                            .Select(f => new FileInfo(f))
                            .OrderByDescending(f => f.LastWriteTimeUtc)
                            .Take(2)
                            .ToList();

                        foreach (var logFile in kspLogFiles)
                        {
                            if (logFile.Length == 0) continue;
                            var recentLines = ReadTailLines(logFile.FullName, 50);

                            // 1. Kiểm tra lỗi mạng timeout 12002 WinHttp
                            bool has12002 = recentLines.Any(l => l.Contains("12002") || l.Contains("The operation timed out"));
                            if (has12002)
                            {
                                return (
                                    "Máy Chủ Ban Cơ Yếu Không Phản Hồi (Lỗi Mạng 12002)",
                                    "⚠️ KHÔNG THỂ KẾT NỐI TỚI MÁY CHỦ XÁC THỰC BAN CƠ YẾU CHÍNH PHỦ\n\n" +
                                    "• Mã lỗi hệ thống: WinHttpSendRequest 12002 (The operation timed out - 21 giây).\n" +
                                    "• Hiện tượng: Điện thoại di động của thầy/cô KHÔNG NHẬN ĐƯỢC thông báo xác thực.\n\n" +
                                    "📌 NGUYÊN NHÂN:\n" +
                                    "Đường truyền mạng từ máy tính tới cổng dịch vụ ký số từ xa (RSSP) của Ban Cơ Yếu bị ngắt quãng hoặc máy chủ ủy quyền đang quá tải/bảo trì. Gói tin xin cấp quyền chưa gửi tới máy chủ nên máy chủ không thể phát tín hiệu về ứng dụng VGCA Mobile trên điện thoại.\n\n" +
                                    "💡 HƯỚNG DẪN KHẮC PHỤC:\n" +
                                    "1. Kiểm tra lại kết nối mạng Internet trên máy tính (đảm bảo không chặn cổng bảo mật).\n" +
                                    "2. Thử lại sau ít phút khi đường truyền tới Ban Cơ Yếu ổn định trở lại.\n" +
                                    "3. Mở ứng dụng VGCA Virtual CSP trên máy tính để kiểm tra lại trạng thái kết nối.",
                                    "WinHttp 12002 Timeout on WinHttpSendRequest (RSSP Server unreachable)"
                                );
                            }

                            // 2. Kiểm tra lỗi hết hạn Token (3006 / 0x8009002D)
                            bool hasTokenExpired = recentLines.Any(l => l.Contains("\"error\":3006") || l.Contains("BEARER TOKEN") || l.Contains("0x8009002D"));
                            if (hasTokenExpired)
                            {
                                return (
                                    "Phiên Đăng Nhập Virtual CSP Đã Hết Hạn (Mã Lỗi 3006)",
                                    "⚠️ PHIÊN LÀM VIỆC VIRTUAL CSP ĐÃ HẾT HIỆU LỰC\n\n" +
                                    "• Mã lỗi hệ thống: RSSP 3006 (Bearer Token Expired - 0x8009002D).\n\n" +
                                    "📌 NGUYÊN NHÂN:\n" +
                                    "Phiên đăng nhập trên phần mềm VGCA Virtual CSP đã quá thời hạn cho phép.\n\n" +
                                    "💡 HƯỚNG DẪN KHẮC PHỤC:\n" +
                                    "Vui lòng chuyển sang cửa sổ phần mềm Virtual CSP trên máy tính, đăng nhập lại bằng Mật khẩu hoặc quét mã QR trước khi bấm Ký số.",
                                    "RSSP 3006 / 0x8009002D Bearer Token Expired"
                                );
                            }

                            // 3. Kiểm tra lỗi người dùng hủy hoặc timeout Smart OTP (0x8010006E)
                            bool hasCancelled = recentLines.Any(l => l.Contains("0x8010006E") || l.Contains("Waiting dialog cancelled"));
                            if (hasCancelled)
                            {
                                return (
                                    "Yêu Cầu Ký Số Bị Hủy Bỏ (0x8010006E)",
                                    "⚠️ YÊU CẦU KÝ SỐ ĐÃ BỊ HỦY HOẶC QUÁ THỜI GIAN CHỜ\n\n" +
                                    "• Mã lỗi: SCARD_W_CANCELLED_BY_USER (0x8010006E).\n\n" +
                                    "📌 NGUYÊN NHÂN:\n" +
                                    "Yêu cầu ký số bị đóng hoặc đã quá thời gian chờ xác nhận trên điện thoại.\n\n" +
                                    "💡 HƯỚNG DẪN KHẮC PHỤC:\n" +
                                    "Bấm 'Tiến hành ký số' lại và chú ý mở ứng dụng VGCA Mobile trên điện thoại để bấm 'Đồng ý' ngay khi có thông báo.",
                                    "0x8010006E SCARD_W_CANCELLED_BY_USER"
                                );
                            }
                        }
                    }
                }
                catch (Exception logEx)
                {
                    Trace.TraceWarning($"Lỗi phân tích log KSP khi chẩn đoán: {logEx.Message}");
                }
            }

            int hResult = ex.HResult;
            if (hResult == unchecked((int)0x8010006E))
            {
                return (
                    "Yêu Cầu Ký Số Bị Hủy Bỏ (0x8010006E)",
                    "Yêu cầu ký số bị hủy bỏ hoặc quá thời gian chờ xác thực từ xa.\n\nVui lòng thử lại và chú ý phê duyệt trên ứng dụng VGCA Mobile.",
                    "0x8010006E"
                );
            }
            if (hResult == unchecked((int)0x8009002D))
            {
                return (
                    "Phiên Đăng Nhập Virtual CSP Đã Hết Hạn",
                    "Phiên làm việc trên Virtual CSP đã hết hạn. Vui lòng mở Virtual CSP và đăng nhập lại.",
                    "0x8009002D"
                );
            }
            if (hResult == unchecked((int)0x80090016))
            {
                return (
                    "Không Tìm Thấy Khóa Riêng Tư (0x80090016)",
                    "Không thể truy cập khóa riêng tư (Private Key) của chứng thư này (NTE_BAD_KEYSET).\n\nVui lòng kiểm tra lại thiết bị USB Token hoặc phần mềm Virtual CSP.",
                    "0x80090016"
                );
            }
            if (hResult == unchecked((int)0x80090020))
            {
                return (
                    "Mã PIN Không Đúng Hoặc Thiết Bị Bị Khóa (0x80090020)",
                    "Xác thực khóa riêng tư thất bại (NTE_FAIL).\n\nVui lòng kiểm tra lại mã PIN USB Token hoặc kiểm tra trạng thái khóa trên phần mềm Virtual CSP.",
                    "0x80090020"
                );
            }

            return (
                "Lỗi Ký Số",
                $"Quá trình ký số gặp lỗi:\n\n{ex.Message}\n\nVui lòng kiểm tra lại trạng thái kết nối của ứng dụng Virtual CSP hoặc thiết bị USB Token.",
                ex.ToString()
            );
        }
    }
}
