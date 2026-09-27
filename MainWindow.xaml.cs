using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VGCASignToolPro.Models;
using VGCASignToolPro.Services;

namespace VGCASignToolPro
{
    public partial class MainWindow : Window
    {
        private readonly WordConverterService _wordConverter = new();
        private readonly CertificateService _certService = new();
        private readonly PdfSigningService _pdfSigner = new();
        private readonly PdfVerifierService _pdfVerifier = new();
        private readonly ProfileManager _profileManager = new();
        private readonly WebUploadService _webUploader = new();

        private List<CertificateModel> _certificates = new();
        private List<SignatureProfile> _profiles = new();

        private string? _currentWorkingPdfPath;
        private string? _lastSignedPdfPath;
        private string? _originalWordFilePath;
        private bool _isWebViewInitialized;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;

            // Đăng ký nhận log thời gian thực từ các Service
            _wordConverter.StatusChanged += LogActivity;
            _pdfSigner.StatusChanged += LogActivity;
            _webUploader.StatusChanged += LogActivity;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LogActivity("Hệ thống khởi động thành công. Đang tải cấu hình...");

            LoadCertificates();
            LoadProfiles();
            await InitializePreviewEngineAsync();
        }

        private async Task InitializePreviewEngineAsync()
        {
            try
            {
                await PdfPreviewControl.EnsureCoreWebView2Async(null);
                _isWebViewInitialized = true;
                LogActivity("Engine hiển thị WebView2 sẵn sàng.");
            }
            catch (Exception ex)
            {
                LogActivity($"Cảnh báo: Khung xem trước WebView2 chưa kích hoạt ({ex.Message}).");
            }
        }

        private void LogActivity(string message)
        {
            Dispatcher.Invoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string logLine = $"[{timestamp}] {message}";
                TxtActivityLog.AppendText(logLine + Environment.NewLine);
                TxtActivityLog.ScrollToEnd();
                TxtStatusBar.Text = message;
            });
        }

        private void LoadCertificates()
        {
            try
            {
                _certificates = _certService.LoadCertificates(onlyWithPrivateKey: true);
                CmbCertificates.ItemsSource = null;
                CmbCertificates.ItemsSource = _certificates;

                if (_certificates.Count > 0)
                {
                    CmbCertificates.SelectedIndex = 0;
                    LogActivity($"Đã nạp {_certificates.Count} chứng thư số từ Windows Certificate Store.");
                }
                else
                {
                    TxtCertDetails.Text = "Không tìm thấy chứng thư số nào có Private Key trong Windows Store. Vui lòng cắm USB Token hoặc kiểm tra Virtual CSP.";
                    LogActivity("Cảnh báo: Chưa tìm thấy USB Token hoặc chứng thư số hợp lệ.");
                }
            }
            catch (Exception ex)
            {
                LogActivity($"Lỗi đọc chứng thư số: {ex.Message}");
            }
        }

        private void LoadProfiles()
        {
            try
            {
                _profiles = _profileManager.LoadProfiles();
                CmbProfiles.ItemsSource = null;
                CmbProfiles.ItemsSource = _profiles;

                if (_profiles.Count > 0)
                {
                    CmbProfiles.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                LogActivity($"Lỗi nạp mẫu chữ ký: {ex.Message}");
            }
        }

        #region Xử lý Tài Liệu (Word / PDF)

        private async void BtnSelectWord_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Chọn tệp tin Word cần chuyển đổi và ký số",
                Filter = "Tài liệu Word (*.docx;*.doc)|*.docx;*.doc|Tất cả tệp (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                string wordPath = dialog.FileName;
                _originalWordFilePath = wordPath;
                TxtInputFilePath.Text = wordPath;

                LogActivity($"Đã chọn file Word: {Path.GetFileName(wordPath)}");
                BtnSignDocument.IsEnabled = false;

                try
                {
                    // Tự động chuyển đổi Word sang PDF
                    string pdfPath = await _wordConverter.ConvertWordToPdfAsync(wordPath);
                    _currentWorkingPdfPath = pdfPath;
                    TxtWorkingPdfPath.Text = pdfPath;

                    // Hiển thị lên khung xem trước
                    LoadPdfIntoPreview(pdfPath);
                    LogActivity("File PDF đã được tạo sẵn sàng cho tiến trình ký số.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Không thể chuyển đổi file Word sang PDF:\n{ex.Message}",
                        "Lỗi Chuyển Đổi", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    BtnSignDocument.IsEnabled = true;
                }
            }
        }

        private void BtnSelectPdf_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Chọn tệp tin PDF để ký số",
                Filter = "Tài liệu PDF (*.pdf)|*.pdf|Tất cả tệp (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                string pdfPath = dialog.FileName;
                _originalWordFilePath = null;
                _currentWorkingPdfPath = pdfPath;

                TxtInputFilePath.Text = pdfPath;
                TxtWorkingPdfPath.Text = pdfPath;

                LoadPdfIntoPreview(pdfPath);
                LogActivity($"Đã nạp file PDF trực tiếp: {Path.GetFileName(pdfPath)}");
            }
        }

        private void LoadPdfIntoPreview(string pdfPath)
        {
            try
            {
                if (_isWebViewInitialized && File.Exists(pdfPath))
                {
                    PnlPreviewPlaceholder.Visibility = Visibility.Collapsed;
                    PdfPreviewControl.CoreWebView2.Navigate(new Uri(pdfPath).AbsoluteUri);
                }
            }
            catch (Exception ex)
            {
                LogActivity($"Lỗi hiển thị xem trước: {ex.Message}");
            }
        }

        #endregion

        #region Chứng Thư & Mẫu Chữ Ký

        private void BtnRefreshCertificates_Click(object sender, RoutedEventArgs e)
        {
            LogActivity("Đang quét lại danh sách USB Token & Chứng thư số...");
            LoadCertificates();
        }

        private void CmbCertificates_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbCertificates.SelectedItem is CertificateModel cert)
            {
                TxtCertDetails.Text = $"Chủ sở hữu: {cert.Subject}\nCơ quan cấp (CA): {cert.Issuer}\nSố Serial: {cert.SerialNumber}\nHiệu lực: {cert.NotBefore:dd/MM/yyyy} đến {cert.NotAfter:dd/MM/yyyy}\nThumbprint: {cert.Thumbprint}";
                TxtSignerDisplayName.Text = cert.CommonName;
                TxtUploadSigner.Text = cert.CommonName;
            }
        }

        private void CmbProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbProfiles.SelectedItem is SignatureProfile profile)
            {
                TxtDimensions.Text = $"{profile.SignatureWidth}x{profile.SignatureHeight}";
                if (!string.IsNullOrWhiteSpace(profile.ImagePath) && File.Exists(profile.ImagePath))
                {
                    TxtStampImagePath.Text = profile.ImagePath;
                }
            }
        }

        private void BtnSelectStampImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Chọn hình ảnh con dấu hoặc chữ ký tay",
                Filter = "Hình ảnh (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|Tất cả tệp (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtStampImagePath.Text = dialog.FileName;
                LogActivity($"Đã chọn ảnh con dấu: {Path.GetFileName(dialog.FileName)}");
            }
        }

        #endregion

        #region Ký Số Tài Liệu

        private async void BtnSignDocument_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentWorkingPdfPath) || !File.Exists(_currentWorkingPdfPath))
            {
                MessageBox.Show("Vui lòng chọn hoặc chuyển đổi một tài liệu PDF trước khi ký.",
                    "Chưa có tệp tin", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CmbCertificates.SelectedItem is not CertificateModel cert)
            {
                MessageBox.Show("Vui lòng chọn một chứng thư số hợp lệ từ danh sách.",
                    "Chưa chọn chứng thư", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Đọc tọa độ & kích thước
            int pageNumber = -1;
            int.TryParse(TxtPageNumber.Text.Trim(), out pageNumber);

            float coordX = 380;
            float.TryParse(TxtCoordX.Text.Trim(), out coordX);

            float coordY = 120;
            float.TryParse(TxtCoordY.Text.Trim(), out coordY);

            float width = 160;
            float height = 80;
            var dimParts = TxtDimensions.Text.Split('x', 'X');
            if (dimParts.Length == 2)
            {
                float.TryParse(dimParts[0].Trim(), out width);
                float.TryParse(dimParts[1].Trim(), out height);
            }

            string stampPath = TxtStampImagePath.Text.Trim();
            if (stampPath.StartsWith("Ảnh con dấu") || !File.Exists(stampPath))
            {
                stampPath = null!;
            }

            // Tạo đường dẫn file xuất
            string dir = Path.GetDirectoryName(_currentWorkingPdfPath) ?? AppDomain.CurrentDomain.BaseDirectory;
            string baseName = Path.GetFileNameWithoutExtension(_currentWorkingPdfPath);
            string outputPath = Path.Combine(dir, $"{baseName}_SIGNED.pdf");

            var request = new SigningRequest
            {
                SourcePdfPath = _currentWorkingPdfPath,
                OutputPdfPath = outputPath,
                Certificate = cert,
                PageNumber = pageNumber,
                X = coordX,
                Y = coordY,
                Width = width,
                Height = height,
                Reason = TxtReason.Text.Trim(),
                SignerDisplayName = TxtSignerDisplayName.Text.Trim(),
                StampImagePath = stampPath,
                UseTSA = ChkUseTSA.IsChecked == true,
                TSAUrl = "http://ca.gov.vn/tsa"
            };

            BtnSignDocument.IsEnabled = false;
            LogActivity("=== BẮT ĐẦU TIẾN TRÌNH KÝ SỐ CHUYÊN DÙNG ===");

            try
            {
                string signedPath = await _pdfSigner.SignPdfAsync(request);
                _lastSignedPdfPath = signedPath;

                // Nạp file đã ký vào WebView2 để kiểm tra trực quan
                LoadPdfIntoPreview(signedPath);

                // Cập nhật sẵn sang tab Upload Web
                TxtUploadFilePath.Text = signedPath;
                TxtUploadDocTitle.Text = Path.GetFileNameWithoutExtension(signedPath);
                BtnGoToUpload.Visibility = Visibility.Visible;

                MessageBox.Show(
                    $"Ký số văn bản thành công!\n\nTệp tin đã lưu tại:\n{signedPath}\n\nBạn có thể chuyển sang Tab 'Gửi File Lên Web' để tải tài liệu lên máy chủ tiếp nhận.",
                    "Ký Số Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogActivity($"Lỗi trong quá trình ký số: {ex.Message}");
                MessageBox.Show($"Quá trình ký số gặp lỗi:\n\n{ex.Message}\n\nVui lòng kiểm tra lại mã PIN của USB Token hoặc quyền truy cập chứng thư số.",
                    "Lỗi Ký Số", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnSignDocument.IsEnabled = true;
            }
        }

        private void BtnGoToUpload_Click(object sender, RoutedEventArgs e)
        {
            // Chuyển sang Tab 2 (Gửi Lên Web)
            if (FindVisualParent<TabControl>(this) is TabControl tabs)
            {
                tabs.SelectedIndex = 1;
            }
        }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parentObject = System.Windows.Media.VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindVisualParent<T>(parentObject);
        }

        #endregion

        #region Gửi Lên Web (Upload Web)

        private void BtnSelectSignedPdfForUpload_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Chọn tệp PDF đã ký cần gửi lên Cổng Web",
                Filter = "Tài liệu PDF (*.pdf)|*.pdf|Tất cả tệp (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtUploadFilePath.Text = dialog.FileName;
                TxtUploadDocTitle.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }

        private async void BtnExecuteUpload_Click(object sender, RoutedEventArgs e)
        {
            string filePath = TxtUploadFilePath.Text.Trim();
            string uploadUrl = TxtUploadUrl.Text.Trim();

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                MessageBox.Show("Vui lòng chọn một tệp PDF hợp lệ để gửi lên Web.",
                    "Chưa chọn tệp", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(uploadUrl) || (!uploadUrl.StartsWith("http://") && !uploadUrl.StartsWith("https://")))
            {
                MessageBox.Show("Vui lòng nhập địa chỉ URL máy chủ hợp lệ (bắt đầu bằng http:// hoặc https://).",
                    "URL không hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var request = new UploadRequest
            {
                FilePath = filePath,
                UploadUrl = uploadUrl,
                AuthToken = TxtAuthToken.Text.Trim(),
                DocumentTitle = TxtUploadDocTitle.Text.Trim(),
                SignerName = TxtUploadSigner.Text.Trim(),
                Department = TxtUploadDepartment.Text.Trim(),
                OriginalFileName = _originalWordFilePath != null ? Path.GetFileName(_originalWordFilePath) : Path.GetFileName(filePath)
            };

            BtnExecuteUpload.IsEnabled = false;
            TxtHttpStatus.Text = "Đang gửi gói tin...";
            TxtHttpStatus.Foreground = System.Windows.Media.Brushes.DarkOrange;

            try
            {
                var result = await _webUploader.UploadSignedPdfAsync(request);

                TxtHttpStatus.Text = $"HTTP {result.StatusCode} ({(result.IsSuccess ? "THÀNH CÔNG" : "LỖI")})";
                TxtHttpStatus.Foreground = result.IsSuccess 
                    ? System.Windows.Media.Brushes.ForestGreen 
                    : System.Windows.Media.Brushes.Crimson;

                TxtHttpLatency.Text = $"{result.ResponseTimeMs} ms";
                TxtHttpResponseContent.Text = result.ResponseBody;

                if (result.IsSuccess)
                {
                    MessageBox.Show(
                        $"Tải tài liệu lên Cổng Web thành công!\n\nMã phản hồi: HTTP {result.StatusCode}\nĐộ trễ: {result.ResponseTimeMs} ms",
                        "Tải Lên Web Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(
                        $"Máy chủ phản hồi mã lỗi HTTP {result.StatusCode}:\n{result.ResponseBody}",
                        "Máy Chủ Từ Chối", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                TxtHttpStatus.Text = "LỖI KẾT NỐI";
                TxtHttpStatus.Foreground = System.Windows.Media.Brushes.Crimson;
                TxtHttpResponseContent.Text = ex.ToString();
            }
            finally
            {
                BtnExecuteUpload.IsEnabled = true;
            }
        }

        #endregion

        #region Kiểm Tra Chữ Ký (Verify)

        private void BtnVerifyPdf_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Chọn tệp PDF để xác minh chữ ký số",
                Filter = "Tài liệu PDF (*.pdf)|*.pdf|Tất cả tệp (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                string pdfPath = dialog.FileName;
                TxtVerifyPdfPath.Text = pdfPath;

                LogActivity($"Đang phân tích chữ ký trong tệp: {Path.GetFileName(pdfPath)}...");

                var signatures = _pdfVerifier.VerifyPdf(pdfPath);
                ListSignatures.ItemsSource = signatures;

                if (signatures.Count > 0)
                {
                    LogActivity($"Phát hiện {signatures.Count} chữ ký số trong tài liệu.");
                    MessageBox.Show(
                        $"Tìm thấy {signatures.Count} chữ ký số trong tài liệu.\nKiểm tra chi tiết trạng thái ở bảng bên dưới.",
                        "Kết Quả Xác Minh", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    LogActivity("Tài liệu không có chữ ký số hoặc chữ ký chưa được lưu đúng định dạng.");
                    MessageBox.Show("Tài liệu này không chứa bất kỳ chữ ký số nào.",
                        "Không có chữ ký", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        #endregion
    }
}