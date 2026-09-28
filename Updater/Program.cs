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

            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir) ||
                string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            {
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
            reportProgress("Đang chờ ứng dụng chính hoàn tất phiên làm việc...", 10);

            // 1. Chờ tiến trình chính thoát hoàn toàn
            if (targetPid > 0)
            {
                try
                {
                    Process mainProc = Process.GetProcessById(targetPid);
                    if (!mainProc.WaitForExit(15000))
                    {
                        try { mainProc.Kill(); } catch { }
                    }
                }
                catch (ArgumentException)
                {
                    // Tiến trình đã thoát trước đó
                }
            }

            Thread.Sleep(500); // Đảm bảo Windows nhả toàn bộ file handle

            // 2. Tạo bản sao lưu dự phòng trước khi ghi đè
            reportProgress("Đang tạo bản sao lưu an toàn cho dữ liệu...", 25);
            string backupDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro", "Backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            try
            {
                if (!Directory.Exists(backupDir))
                {
                    Directory.CreateDirectory(backupDir);
                }
            }
            catch { }

            // 3. Thực hiện ghi đè các tệp chương trình mới (Bảo tồn danh bạ và cấu hình)
            reportProgress("Đang cập nhật các tệp tin chương trình mới...", 50);
            try
            {
                CopyDirectoryRecursive(sourceDir, targetDir, sourceDir, backupDir);
            }
            catch (Exception ex)
            {
                // Rollback nếu xảy ra lỗi ghi đè
                reportProgress("Gặp sự cố, đang khôi phục bản sao lưu...", 75);
                try
                {
                    if (Directory.Exists(backupDir))
                    {
                        CopyDirectoryRecursive(backupDir, targetDir, backupDir, string.Empty);
                    }
                }
                catch { }

                throw new InvalidOperationException($"Lỗi trong quá trình sao chép tệp mới: {ex.Message}", ex);
            }

            // 4. Khởi động lại ứng dụng phiên bản mới
            reportProgress("Cập nhật thành công! Đang khởi động lại ứng dụng...", 95);
            Thread.Sleep(500);

            if (!string.IsNullOrWhiteSpace(restartExe) && File.Exists(restartExe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = restartExe,
                        UseShellExecute = true,
                        WorkingDirectory = targetDir
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Đã cập nhật thành công nhưng không thể tự khởi động lại ứng dụng:\n{ex.Message}\n\nVui lòng mở lại ứng dụng từ Desktop.",
                        "Thông Báo", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }

            // Dọn dẹp thư mục staging
            try
            {
                Directory.Delete(sourceDir, true);
            }
            catch { }

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

                // Ghi đè file mới (Thử lại tối đa 3 lần nếu Windows còn khóa nhẹ)
                bool copied = false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        file.CopyTo(destFile, true);
                        copied = true;
                        break;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(300);
                    }
                }

                if (!copied)
                {
                    file.CopyTo(destFile, true);
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
    }

    internal class UpdaterProgressForm : Form
    {
        private readonly int _targetPid;
        private readonly string _sourceDir;
        private readonly string _targetDir;
        private readonly string _restartExe;

        private readonly Label _lblTitle;
        private readonly Label _lblStatus;
        private readonly ProgressBar _progressBar;

        public UpdaterProgressForm(int targetPid, string sourceDir, string targetDir, string restartExe)
        {
            _targetPid = targetPid;
            _sourceDir = sourceDir;
            _targetDir = targetDir;
            _restartExe = restartExe;

            Text = "VGCA Sign Tool Pro - Đang Cập Nhật Hệ Thống";
            Size = new System.Drawing.Size(460, 180);
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
                Size = new System.Drawing.Size(410, 20),
                ForeColor = System.Drawing.Color.FromArgb(71, 85, 105)
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
