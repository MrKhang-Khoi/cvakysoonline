using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VGCASignToolPro.Models;
using VGCASignToolPro.Services;

namespace VGCASignToolPro
{
    public partial class UpdateDialog : Window
    {
        private readonly UpdateCheckResult _checkResult;
        private readonly UpdateService _updateService;
        private CancellationTokenSource? _downloadCts;

        public UpdateDialog(UpdateCheckResult checkResult, UpdateService updateService)
        {
            InitializeComponent();
            _checkResult = checkResult;
            _updateService = updateService;

            PopulateReleaseData();
        }

        private void PopulateReleaseData()
        {
            TxtCurrentVer.Text = $"v{_checkResult.CurrentVersion}";
            TxtNewVer.Text = $"v{_checkResult.LatestVersion}";

            AppReleaseModel? release = _checkResult.Release;
            if (release != null)
            {
                TxtReleaseTitle.Text = !string.IsNullOrWhiteSpace(release.Title)
                    ? release.Title
                    : $"Bản Nâng Cấp Hệ Thống v{release.Version}";

                string sizeNotice = release.PackageSizeMb > 0 ? $" • Dung lượng: ~{release.PackageSizeMb:F1} MB" : "";
                TxtReleaseDate.Text = $"Phát hành: {release.ReleaseDate}{sizeNotice}";

                // Điền danh sách Changelog
                PnlChangelogList.Children.Clear();
                if (release.Changelog != null && release.Changelog.Count > 0)
                {
                    foreach (string item in release.Changelog)
                    {
                        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                        sp.Children.Add(new TextBlock
                        {
                            Text = "✔",
                            Foreground = new SolidColorBrush(Color.FromRgb(5, 150, 105)),
                            FontWeight = FontWeights.Bold,
                            Margin = new Thickness(0, 0, 8, 0)
                        });
                        sp.Children.Add(new TextBlock
                        {
                            Text = item,
                            Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 440
                        });
                        PnlChangelogList.Children.Add(sp);
                    }
                }
                else
                {
                    PnlChangelogList.Children.Add(new TextBlock
                    {
                        Text = "Cải thiện độ ổn định, hiệu năng và nâng cấp tương thích hệ điều hành Windows.",
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                        FontSize = 12
                    });
                }

                // Nếu là bản cập nhật bắt buộc
                if (_checkResult.IsMandatory)
                {
                    BorderMandatoryNotice.Visibility = Visibility.Visible;
                    BtnLater.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void BtnLater_Click(object sender, RoutedEventArgs e)
        {
            if (_checkResult.IsMandatory)
            {
                MessageBox.Show(
                    "Đây là bản cập nhật bắt buộc từ nhà trường để tương thích chữ ký số Ban Cơ Yếu. Vui lòng bấm Cập Nhật Ngay.",
                    "Cập Nhật Bắt Buộc", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _downloadCts?.Cancel();
            Close();
        }

        private async void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
        {
            if (_checkResult.Release == null || string.IsNullOrWhiteSpace(_checkResult.Release.DownloadUrl))
            {
                MessageBox.Show("Đường dẫn tải gói cập nhật trên máy chủ không hợp lệ.", "Lỗi Cập Nhật", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            BtnUpdateNow.IsEnabled = false;
            BtnLater.IsEnabled = false;
            PnlProgressArea.Visibility = Visibility.Visible;

            _downloadCts = new CancellationTokenSource();
            string? downloadedZip = null;

            try
            {
                TxtProgressStatus.Text = "Đang kết nối và tải gói cập nhật từ máy chủ...";

                var progress = new Progress<UpdateDownloadProgress>(p =>
                {
                    PrgDownload.Value = p.ProgressPercentage;
                    TxtProgressPercent.Text = $"{p.ProgressPercentage}%";

                    double downloadedMb = p.BytesReceived / 1048576.0;
                    double totalMb = (p.TotalBytesToReceive ?? 0) / 1048576.0;

                    TxtProgressSpeed.Text = $"{p.SpeedKbs:F0} KB/s • {downloadedMb:F1} MB / {totalMb:F1} MB";
                });

                downloadedZip = await _updateService.DownloadUpdatePackageAsync(
                    _checkResult.Release.DownloadUrl,
                    progress,
                    _downloadCts.Token);

                // KIỂM TRA MÃ BĂM MẬT MÃ HỌC SHA-256 ĐỐI SOÁT VỚI FIREBASE
                TxtProgressStatus.Text = "Đang kiểm tra tính toàn vẹn (SHA-256 Checksum)...";
                await Task.Delay(300);

                bool isShaValid = _updateService.VerifyPackageSha256(downloadedZip, _checkResult.Release.Sha256);
                if (!isShaValid)
                {
                    try { if (File.Exists(downloadedZip)) File.Delete(downloadedZip); } catch { }

                    MessageBox.Show(
                        "Mã băm SHA-256 của tệp tải về không trùng khớp với dữ liệu niêm phong trên máy chủ Firebase!\n\n" +
                        "Để bảo vệ an toàn cho máy tính, phần mềm đã hủy bỏ quá trình cập nhật này. Vui lòng thử lại sau.",
                        "Cảnh Báo Toàn Vẹn Tệp Tin", MessageBoxButton.OK, MessageBoxImage.Error);

                    BtnUpdateNow.IsEnabled = true;
                    BtnLater.IsEnabled = true;
                    PnlProgressArea.Visibility = Visibility.Collapsed;
                    return;
                }

                // KHỞI CHẠY TRÌNH UPDATER ĐỘC LẬP
                TxtProgressStatus.Text = "Xác thực SHA-256 thành công! Đang kích hoạt bộ nâng cấp độc lập...";
                await Task.Delay(500);

                string currentExePath = Process.GetCurrentProcess().MainModule?.FileName ??
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VGCASignToolPro.exe");

                bool launched = _updateService.LaunchExternalUpdater(downloadedZip, currentExePath);
                if (launched)
                {
                    // Tắt ứng dụng chính an toàn để Updater ghi đè
                    Application.Current.Shutdown();
                }
                else
                {
                    MessageBox.Show(
                        "Không thể khởi chạy tiến trình VGCASignToolPro.Updater.exe.\nVui lòng kiểm tra lại quyền truy cập thư mục.",
                        "Lỗi Khởi Chạy", MessageBoxButton.OK, MessageBoxImage.Error);

                    BtnUpdateNow.IsEnabled = true;
                    BtnLater.IsEnabled = true;
                }
            }
            catch (OperationCanceledException)
            {
                TxtProgressStatus.Text = "Đã hủy tải gói cập nhật.";
                BtnUpdateNow.IsEnabled = true;
                BtnLater.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Quá trình tải bản cập nhật gặp lỗi:\n\n{ex.Message}",
                    "Lỗi Tải Cập Nhật", MessageBoxButton.OK, MessageBoxImage.Error);

                BtnUpdateNow.IsEnabled = true;
                BtnLater.IsEnabled = true;
                PnlProgressArea.Visibility = Visibility.Collapsed;
            }
        }
    }
}
