using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace VGCASignToolPro.Services
{
    /// <summary>
    /// Hệ thống ghi log chẩn đoán và theo dõi sự kiện thời gian thực chuẩn doanh nghiệp.
    /// Tự động ghi ra tệp tin hàng ngày trong %LocalAppData%\VGCASignToolPro\logs\app_yyyyMMdd.log.
    /// Đảm bảo thread-safe, auto-flush, triệt tiêu nuốt lỗi và phục vụ công tác kiểm tra, chẩn đoán.
    /// </summary>
    public static class AppLogger
    {
        private static readonly object _syncLock = new();
        private static string? _logFilePath;
        private static bool _isInitialized;

        public static string LogDirectory { get; private set; } = string.Empty;

        public static void Initialize()
        {
            if (_isInitialized) return;

            lock (_syncLock)
            {
                if (_isInitialized) return;

                try
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    LogDirectory = Path.Combine(localAppData, "VGCASignToolPro", "logs");

                    if (!Directory.Exists(LogDirectory))
                    {
                        Directory.CreateDirectory(LogDirectory);
                    }

                    string logFileName = $"app_{DateTime.UtcNow:yyyyMMdd}.log";
                    _logFilePath = Path.Combine(LogDirectory, logFileName);

                    // Tích hợp TextWriterTraceListener để tự động gom toàn bộ Trace.TraceError/Warning vào file log
                    var streamWriter = new StreamWriter(new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096), Encoding.UTF8)
                    {
                        AutoFlush = true
                    };

                    Trace.Listeners.Add(new TextWriterTraceListener(streamWriter, "AppTraceListener"));
                    Trace.AutoFlush = true;

                    _isInitialized = true;

                    LogInternal("INFO", "=== KHỞI ĐỘNG HỆ THỐNG VGCA SIGN TOOL PRO ===");
                    LogInternal("INFO", $"Log file: {_logFilePath}");
                    LogInternal("INFO", $"OS Version: {Environment.OSVersion}, .NET: {Environment.Version}, 64-bit: {Environment.Is64BitProcess}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Không thể khởi tạo AppLogger: {ex.Message}");
                }
            }
        }

        public static void Info(string message, string memberName = "")
        {
            LogInternal("INFO", message, memberName);
        }

        public static void Warn(string message, string memberName = "")
        {
            LogInternal("WARN", message, memberName);
        }

        public static void Error(string message, Exception? ex = null, string memberName = "")
        {
            string fullMessage = ex != null ? $"{message} | Ngoại lệ: {ex.GetType().Name}: {ex.Message}\nStackTrace:\n{ex.StackTrace}" : message;
            LogInternal("ERROR", fullMessage, memberName);
        }

        private static void LogInternal(string level, string message, string memberName = "")
        {
            try
            {
                if (!_isInitialized)
                {
                    Initialize();
                }

                if (string.IsNullOrEmpty(_logFilePath)) return;

                string timeStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                int threadId = Thread.CurrentThread.ManagedThreadId;
                string caller = string.IsNullOrWhiteSpace(memberName) ? "" : $"[{memberName}] ";
                string logLine = $"[{timeStamp}] [{level,-5}] [Thread-{threadId:D2}] {caller}{message}{Environment.NewLine}";

                lock (_syncLock)
                {
                    File.AppendAllText(_logFilePath, logLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Lỗi ghi log: {ex.Message}");
            }
        }
    }
}
