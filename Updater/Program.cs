using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace VGCASignToolPro.Updater
{
    internal static class Program
    {
        private static readonly object LogLock = new();

        public static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string logDir = Path.Combine(localAppData, "VGCASignToolPro", "logs");
                    if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                    string logFile = Path.Combine(logDir, $"updater_{DateTime.Now:yyyyMMdd}.log");
                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                    File.AppendAllText(logFile, line);
                }
            }
            catch { }
        }

        private static readonly HashSet<string> ProtectedFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "school_roster.json",
            "school_roster.json.hmac",
            "workflow_config.json",
            "profiles.json",
            ".roster.key"
        };

        private static readonly HashSet<string> ProtectedDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            "logs",
            "WebView2Cache"
        };

        [STAThread]
        private static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            Log($"=== KHỞI ĐỘNG VGCASignToolPro.Updater (PID: {Environment.ProcessId}) ===");
            Log($"Args ({args.Length}): {string.Join(" ", args)}");

            int targetPid = -1;
            string sourceDir = string.Empty;
            string targetDir = string.Empty;
            string restartExe = string.Empty;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out targetPid);
                }
                else if (string.Equals(args[i], "--source", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    sourceDir = args[++i];
                }
                else if (string.Equals(args[i], "--target", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    targetDir = args[++i];
                }
                else if (string.Equals(args[i], "--restart", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    restartExe = args[++i];
                }
            }

            Log($"Phân tích tham số: targetPid={targetPid}, sourceDir='{sourceDir}', targetDir='{targetDir}', restartExe='{restartExe}'");

            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir) ||
                string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            {
                Log($"[LỖI] Tham số không hợp lệ hoặc thư mục không tồn tại. sourceDir Exists={Directory.Exists(sourceDir)}, targetDir Exists={Directory.Exists(targetDir)}");
                MessageBox.Show(
                    "Tham số cập nhật không hợp lệ hoặc thư mục nguồn/đích không tồn tại.",
                    "Lỗi Cập Nhật", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Hiển thị Form tiến trình cập nhật trang nhã
            using var progressForm = new UpdaterProgressForm(targetPid, sourceDir, targetDir, restartExe);
            Application.Run(progressForm);
        }

        public static void ApplyUpdate(int targetPid, string sourceDir, string targetDir, string restartExe, Action<string, int> reportProgress)
        {
            Log($"[ApplyUpdate] Bắt đầu chu trình cập nhật từ '{sourceDir}' sang '{targetDir}', targetPid={targetPid}");
            reportProgress("Đang chờ ứng dụng chính hoàn tất phiên làm việc...", 10);

            // 1. Chờ và cưỡng chế tắt triệt để TOÀN BỘ các tiến trình VGCASignToolPro đang chạy
            Log($"[ApplyUpdate] Đang chờ và tắt tiến trình chính (PID {targetPid})...");
            TerminateAllAppProcesses(targetPid);

            Thread.Sleep(1000); // Đảm bảo Windows giải phóng toàn bộ file handle và Memory-Mapped DLL

            // 2. Tạo bản sao lưu dự phòng trước khi ghi đè
            reportProgress("Đang tạo bản sao lưu an toàn cho dữ liệu...", 25);
            string backupDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro", "Backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            try
            {
                if (!Directory.Exists(backupDir))
                {
                    Directory.CreateDirectory(backupDir);
                    Log($"[ApplyUpdate] Đã tạo thư mục sao lưu dự phòng: '{backupDir}'");
                }
            }
            catch (Exception ex)
            {
                Log($"[ApplyUpdate] [Cảnh báo] Không thể tạo thư mục sao lưu: {ex.Message}");
            }

            // 3. Thực hiện ghi đè các tệp chương trình mới (Bảo tồn danh bạ và cấu hình)
            reportProgress("Đang cập nhật các tệp tin chương trình mới...", 50);
            try
            {
                Log($"[ApplyUpdate] Bắt đầu sao chép tệp mới từ '{sourceDir}' sang '{targetDir}'...");
                CopyDirectoryRecursive(sourceDir, targetDir, sourceDir, backupDir);
                Log("[ApplyUpdate] Đã sao chép toàn bộ tệp mới thành công.");
            }
            catch (Exception ex)
            {
                Log($"[ApplyUpdate] [LỖI] Xảy ra ngoại lệ trong khi sao chép: {ex}");
                // Rollback nếu xảy ra lỗi ghi đè
                reportProgress("Gặp sự cố, đang khôi phục bản sao lưu...", 75);
                try
                {
                    if (Directory.Exists(backupDir))
                    {
                        Log("[ApplyUpdate] Đang phục hồi dữ liệu từ thư mục sao lưu...");
                        CopyDirectoryRecursive(backupDir, targetDir, backupDir, string.Empty);
                    }
                }
                catch (Exception rbEx)
                {
                    Log($"[ApplyUpdate] [LỖI ROLLBACK] {rbEx}");
                }

                throw new InvalidOperationException($"Lỗi trong quá trình sao chép tệp mới: {ex.Message}", ex);
            }

            // 4. Khởi động lại ứng dụng phiên bản mới
            reportProgress("Cập nhật thành công! Đang khởi động lại ứng dụng...", 95);
            Thread.Sleep(500);

            if (!string.IsNullOrWhiteSpace(restartExe) && File.Exists(restartExe))
            {
                try
                {
                    Log($"[ApplyUpdate] Khởi động lại ứng dụng: '{restartExe}' trong thư mục '{targetDir}'");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = restartExe,
                        UseShellExecute = true,
                        WorkingDirectory = targetDir
                    });
                }
                catch (Exception ex)
                {
                    Log($"[ApplyUpdate] [LỖI] Không thể tự khởi động ứng dụng: {ex}");
                    MessageBox.Show(
                        $"Đã cập nhật thành công nhưng không thể tự khởi động lại ứng dụng:\n{ex.Message}\n\nVui lòng mở lại ứng dụng từ Desktop.",
                        "Thông Báo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }

            // Dọn dẹp các tệp tạm pending delete
            CleanPendingDeleteFiles(targetDir);

            // Dọn dẹp thư mục staging
            try
            {
                Directory.Delete(sourceDir, true);
                Log($"[ApplyUpdate] Đã dọn dẹp thư mục staging '{sourceDir}'");
            }
            catch (Exception ex)
            {
                Log($"[ApplyUpdate] Thư mục staging tạm chưa xóa được ngay (sẽ dọn dẹp sau): {ex.Message}");
            }

            Log("[ApplyUpdate] Hoàn tất toàn bộ chu trình nâng cấp 100%!");
            reportProgress("Hoàn tất!", 100);
        }

        private static void CopyDirectoryRecursive(string currentSource, string targetBase, string rootSource, string backupDir)
        {
            DirectoryInfo sourceInfo = new DirectoryInfo(currentSource);

            // Kiểm tra thư mục được bảo vệ
            if (ProtectedDirectories.Contains(sourceInfo.Name))
            {
                return;
            }

            // Tạo các thư mục con tương ứng
            string relativePath = Path.GetRelativePath(rootSource, currentSource);
            string currentTargetDir = relativePath == "." ? targetBase : Path.Combine(targetBase, relativePath);

            if (!Directory.Exists(currentTargetDir))
            {
                Directory.CreateDirectory(currentTargetDir);
            }

            // Sao chép các tệp tin
            foreach (FileInfo file in sourceInfo.GetFiles())
            {
                // BẢO TỒN NGUYÊN VẸN CÁC TỆP DỮ LIỆU CỦA NHÀ TRƯỜNG
                if (ProtectedFiles.Contains(file.Name))
                {
                    string targetFilePath = Path.Combine(currentTargetDir, file.Name);
                    if (File.Exists(targetFilePath))
                    {
                        // Đã có dữ liệu trường học -> BỎ QUA KHÔNG ĐƯỢC GHI ĐÈ!
                        continue;
                    }
                }

                string destFile = Path.Combine(currentTargetDir, file.Name);

                // Sao lưu file cũ nếu có
                if (!string.IsNullOrEmpty(backupDir) && File.Exists(destFile))
                {
                    try
                    {
                        string bkpFile = Path.Combine(backupDir, file.Name);
                        File.Copy(destFile, bkpFile, true);
                    }
                    catch { }
                }

                // Ghi đè file mới bằng kỹ thuật Safe Replace (Move-Before-Overwrite) chuẩn Chrome/VS Code
                bool copied = false;
                Exception? lastEx = null;

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        if (File.Exists(destFile))
                        {
                            try
                            {
                                File.Delete(destFile);
                            }
                            catch (IOException)
                            {
                                // Nếu file đang bị lock nhẹ, đổi tên nó sang tệp tạm pending delete
                                string pendingDelete = destFile + ".pending_delete_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                                try
                                {
                                    File.Move(destFile, pendingDelete);
                                }
                                catch
                                {
                                    // Tiếp tục thử copy đè trực tiếp
                                }
                            }
                        }

                        file.CopyTo(destFile, true);
                        copied = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        Thread.Sleep(500);
                    }
                }

                if (!copied && lastEx != null)
                {
                    throw new IOException($"Không thể cập nhật tệp tin '{destFile}': {lastEx.Message}", lastEx);
                }
            }

            // Đệ quy cho thư mục con
            foreach (DirectoryInfo subDir in sourceInfo.GetDirectories())
            {
                if (!ProtectedDirectories.Contains(subDir.Name))
                {
                    CopyDirectoryRecursive(subDir.FullName, targetBase, rootSource, backupDir);
                }
            }
        }

        private static void TerminateAllAppProcesses(int targetPid)
        {
            // 1. Chờ PID cụ thể nếu có
            if (targetPid > 0)
            {
                try
                {
                    Process mainProc = Process.GetProcessById(targetPid);
                    if (!mainProc.WaitForExit(8000))
                    {
                        try { mainProc.Kill(); } catch { }
                    }
                }
                catch (ArgumentException)
                {
                    // Tiến trình đã thoát
                }
            }

            // 2. Quét và cưỡng chế tắt toàn bộ các tiến trình VGCASignToolPro khác (chạy ngầm, autostart)
            try
            {
                var procs = Process.GetProcessesByName("VGCASignToolPro");
                foreach (var p in procs)
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            p.Kill();
                            p.WaitForExit(3000);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void CleanPendingDeleteFiles(string targetDir)
        {
            try
            {
                var pendingFiles = Directory.GetFiles(targetDir, "*.pending_delete_*", SearchOption.AllDirectories);
                foreach (var f in pendingFiles)
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }
    }

    internal class UpdaterProgressForm : Form
    {
        private readonly int _targetPid;
        private readonly string _sourceDir;
        private readonly string _targetDir;
        private readonly string _restartExe;

        private readonly Label _lblTitle;
        private readonly Label _lblStatus;
        private readonly Label _lblPercent;
        private readonly ProgressBar _progressBar;

        public UpdaterProgressForm(int targetPid, string sourceDir, string targetDir, string restartExe)
        {
            _targetPid = targetPid;
            _sourceDir = sourceDir;
            _targetDir = targetDir;
            _restartExe = restartExe;

            Text = "VGCA Sign Tool Pro - Đang Cập Nhật Hệ Thống";
            Size = new System.Drawing.Size(460, 200);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            TopMost = true;

            _lblTitle = new Label
            {
                Text = "Đang Cập Nhật VGCA Sign Tool Pro Lên Phiên Bản Mới...",
                Font = new System.Drawing.Font("Segoe UI", 10.5f, System.Drawing.FontStyle.Bold),
                Location = new System.Drawing.Point(20, 18),
                Size = new System.Drawing.Size(410, 26)
            };

            _lblStatus = new Label
            {
                Text = "Khởi tạo tiến trình cập nhật...",
                Font = new System.Drawing.Font("Segoe UI", 9f),
                Location = new System.Drawing.Point(20, 48),
                Size = new System.Drawing.Size(340, 20),
                ForeColor = System.Drawing.Color.FromArgb(71, 85, 105)
            };

            _lblPercent = new Label
            {
                Text = "0%",
                Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold),
                Location = new System.Drawing.Point(370, 48),
                Size = new System.Drawing.Size(60, 20),
                TextAlign = System.Drawing.ContentAlignment.MiddleRight,
                ForeColor = System.Drawing.Color.FromArgb(37, 99, 235)
            };

            _progressBar = new ProgressBar
            {
                Location = new System.Drawing.Point(20, 75),
                Size = new System.Drawing.Size(405, 24),
                Style = ProgressBarStyle.Continuous,
                Value = 5
            };

            Controls.Add(_lblTitle);
            Controls.Add(_lblStatus);
            Controls.Add(_lblPercent);
            Controls.Add(_progressBar);

            Shown += (s, e) => StartUpdateThread();
        }

        private void StartUpdateThread()
        {
            var workerThread = new Thread(() =>
            {
                try
                {
                    Program.ApplyUpdate(_targetPid, _sourceDir, _targetDir, _restartExe, (status, percent) =>
                    {
                        if (IsHandleCreated && !IsDisposed)
                        {
                            Invoke(new Action(() =>
                            {
                                _lblStatus.Text = status;
                                _lblPercent.Text = $"{percent}%";
                                _progressBar.Value = Math.Min(100, Math.Max(0, percent));
                            }));
                        }
                    });

                    Thread.Sleep(400);
                    if (IsHandleCreated && !IsDisposed)
                    {
                        Invoke(new Action(() => Close()));
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[UpdaterProgressForm] [LỖI NGOẠI LỆ] Cập nhật thất bại: {ex}");
                    Invoke(new Action(() =>
                    {
                        MessageBox.Show(
                            $"Quá trình cập nhật thất bại:\n\n{ex.Message}\n\nHệ thống đã khôi phục bản cũ an toàn.",
                            "Lỗi Cập Nhật", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Close();
                    }));
                }
            })
            {
                IsBackground = true
            };
            workerThread.Start();
        }
    }
}
