using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        private CertificateModel? _currentActiveCert;

        private string? _originalWordFilePath;
        private string? _currentWorkingPdfPath;
        private string? _lastSignedPdfPath;
        private bool _isWebViewInitialized;

        // VỊ TRÍ CHỮ KÝ DO NGƯỜI DÙNG KÉO THẢ TƯƠNG TÁC TRÊN BẢN XEM TRƯỚC
        private bool _hasUserPlacedStamp = false;
        private int _userStampPage = 1;
        private float _userStampX = 380f;
        private float _userStampY = 120f;
        private float _userStampWidth = 140f;
        private float _userStampHeight = 140f;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;

            _wordConverter.StatusChanged += UpdateStatus;
            _pdfSigner.StatusChanged += UpdateStatus;
            _webUploader.StatusChanged += UpdateStatus;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateStatus("Khởi tạo hệ thống VGCA Sign Tool Pro...");
            await InitializePreviewEngineAsync();

            LoadCertificatesAndVerifyVGCALogin();
            LoadProfiles();
        }

        private async Task InitializePreviewEngineAsync()
        {
            try
            {
                await PdfPreviewControl.EnsureCoreWebView2Async(null);

                // Ánh xạ thư mục wwwroot vào Virtual Host Domain để thực thi PDF.js và Web Worker ngoại tuyến an toàn
                string wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
                if (!Directory.Exists(wwwroot))
                {
                    wwwroot = @"C:\Users\HPZBook\Desktop\VGCASignToolPro\wwwroot";
                }

                if (Directory.Exists(wwwroot))
                {
                    PdfPreviewControl.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "vgcaviewer.local",
                        wwwroot,
                        Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                }

                PdfPreviewControl.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                _isWebViewInitialized = true;
                UpdateStatus("Engine hiển thị WebView2 và PDF.js tương tác đã sẵn sàng.");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Lỗi khởi tạo WebView2: {ex.Message}");
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string jsonString = e.WebMessageAsJson;
                using var doc = System.Text.Json.JsonDocument.Parse(jsonString);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var typeProp))
                {
                    string? msgType = typeProp.GetString();
                    if (msgType == "stampCoordinatesChanged")
                    {
                        if (root.TryGetProperty("coords", out var coords))
                        {
                            _hasUserPlacedStamp = true;
                            _userStampPage = coords.GetProperty("pageNumber").GetInt32();
                            _userStampX = (float)coords.GetProperty("x").GetDouble();
                            _userStampY = (float)coords.GetProperty("y").GetDouble();
                            _userStampWidth = (float)coords.GetProperty("width").GetDouble();
                            _userStampHeight = (float)coords.GetProperty("height").GetDouble();

                            // Đồng bộ ngược lại các ô nhập liệu ở Tab 1 để người dùng quan sát
                            TxtPageNumber.Text = _userStampPage.ToString();
                            TxtCoordX.Text = Math.Round(_userStampX).ToString();
                            TxtCoordY.Text = Math.Round(_userStampY).ToString();
                            TxtDimensions.Text = $"{Math.Round(_userStampWidth)}x{Math.Round(_userStampHeight)}";

                            UpdateStatus($"Đã định vị chữ ký tại Trang {_userStampPage} (X: {Math.Round(_userStampX)}, Y: {Math.Round(_userStampY)}, {_userStampWidth:F0}x{_userStampHeight:F0} pt).");
                        }
                    }
                    else if (msgType == "stampRemoved")
                    {
                        _hasUserPlacedStamp = false;
                        UpdateStatus("Đã hủy bỏ vị trí chèn chữ ký tương tác.");
                    }
                    else if (msgType == "pdfLoaded")
                    {
                        int totalPages = root.GetProperty("totalPages").GetInt32();
                        UpdateStatus($"Đã nạp xong tài liệu PDF tương tác ({totalPages} trang).");
                        BtnInsertSignatureStamp.IsEnabled = true;
                    }
                }
            }
            catch
            {
                // Bỏ qua lỗi parsing không mong muốn
            }
        }

        private void UpdateStatus(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtStatusBar.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
            });
        }

        #region TAB 1: THÔNG TIN CHỨNG THƯ & ĐĂNG NHẬP VGCA

        private void LoadCertificatesAndVerifyVGCALogin()
        {
            try
            {
                _certificates = _certService.LoadCertificates(onlyWithPrivateKey: true);
                CmbCertificates.ItemsSource = null;
                CmbCertificates.ItemsSource = _certificates;

                // Tìm chứng thư VGCA chuyên dùng của Ban Cơ yếu Chính phủ
                var vgcaCert = _certService.GetActiveVGCACertificate();

                if (vgcaCert != null)
                {
                    _currentActiveCert = vgcaCert;
                    CmbCertificates.SelectedItem = vgcaCert;

                    // Hiển thị trạng thái ĐÃ ĐĂNG NHẬP
                    PnlVgcaLoggedInDetails.Visibility = Visibility.Visible;
                    PnlVgcaNotLoggedInWarning.Visibility = Visibility.Collapsed;

                    BadgeVgcaGlobalStatus.Background = new SolidColorBrush(Color.FromRgb(6, 95, 70));
                    TxtVgcaGlobalStatusIcon.Text = "✅";
                    TxtVgcaGlobalStatusText.Text = $"VGCA Virtual CSP: {vgcaCert.CommonName}";

                    // Điền chi tiết thông tin chứng thư
                    TxtDetailSignerName.Text = vgcaCert.CommonName;
                    TxtDetailEmail.Text = !string.IsNullOrWhiteSpace(vgcaCert.Email) ? vgcaCert.Email : "Chưa đăng ký email trong chứng thư";
                    TxtDetailOrg.Text = !string.IsNullOrWhiteSpace(vgcaCert.OrganizationUnit) 
                        ? $"{vgcaCert.OrganizationUnit} - {vgcaCert.Organization}" 
                        : vgcaCert.Subject;
                    TxtDetailIssuer.Text = vgcaCert.Issuer;
                    TxtDetailSerial.Text = vgcaCert.SerialNumber;
                    TxtDetailValidity.Text = $"{vgcaCert.NotBefore:dd/MM/yyyy} đến {vgcaCert.NotAfter:dd/MM/yyyy} ({(vgcaCert.IsValidNow ? "Còn hiệu lực" : "Đã hết hạn")})";
                    TxtDetailAlgorithm.Text = vgcaCert.KeyAlgorithm;
                    TxtDetailThumbprint.Text = vgcaCert.Thumbprint;

                    TxtSignerDisplayName.Text = vgcaCert.CommonName;
                    TxtUploadSigner.Text = vgcaCert.CommonName;
                    TxtQuickCertNotice.Text = $"Chứng thư: {vgcaCert.CommonName} ({vgcaCert.KeyAlgorithm})";

                    UpdateStatus($"Đã xác thực chứng thư Ban Cơ yếu: {vgcaCert.CommonName} (Serial: {vgcaCert.SerialNumber})");
                }
                else
                {
                    _currentActiveCert = null;

                    // Hiển thị cảnh báo CHƯA ĐĂNG NHẬP
                    PnlVgcaLoggedInDetails.Visibility = Visibility.Collapsed;
                    PnlVgcaNotLoggedInWarning.Visibility = Visibility.Visible;

                    BadgeVgcaGlobalStatus.Background = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                    TxtVgcaGlobalStatusIcon.Text = "⚠️";
                    TxtVgcaGlobalStatusText.Text = "VGCA: Chưa Đăng Nhập";
                    TxtQuickCertNotice.Text = "Chưa kết nối chứng thư VGCA";

                    UpdateStatus("Cảnh báo: Chưa phát hiện tài khoản chữ ký số Ban Cơ yếu Chính phủ.");
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Lỗi kiểm tra chứng thư số: {ex.Message}");
            }
        }

        private void BtnRefreshCertificates_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatus("Đang quét lại danh sách chứng thư số...");
            LoadCertificatesAndVerifyVGCALogin();
        }

        private void BtnLaunchVgcaManager_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatus("Đang khởi động ứng dụng VGCA Virtual CSP v2.0...");
            bool launched = _certService.LaunchVGCALoginApp();
            if (!launched)
            {
                MessageBox.Show("Không tìm thấy tệp tin vgca_vcsp_v2_mgr.exe tại thư mục cài đặt VGCA Virtual CSP.",
                    "Lỗi Khởi Động", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show("Đã mở ứng dụng VGCA Virtual CSP v2.0.\nVui lòng đăng nhập tài khoản chữ ký số Ban Cơ yếu của bạn, sau đó nhấn nút 'Quét Lại (F5)' trên phần mềm.",
                    "Đăng Nhập VGCA", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void CmbCertificates_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbCertificates.SelectedItem is CertificateModel cert)
            {
                _currentActiveCert = cert;
                TxtSignerDisplayName.Text = cert.CommonName;
                TxtUploadSigner.Text = cert.CommonName;
                TxtQuickCertNotice.Text = $"Chứng thư: {cert.CommonName} ({cert.KeyAlgorithm})";

                TxtDetailSignerName.Text = cert.CommonName;
                TxtDetailEmail.Text = !string.IsNullOrWhiteSpace(cert.Email) ? cert.Email : "N/A";
                TxtDetailOrg.Text = !string.IsNullOrWhiteSpace(cert.OrganizationUnit) ? $"{cert.OrganizationUnit} - {cert.Organization}" : cert.Subject;
                TxtDetailIssuer.Text = cert.Issuer;
                TxtDetailSerial.Text = cert.SerialNumber;
                TxtDetailValidity.Text = $"{cert.NotBefore:dd/MM/yyyy} đến {cert.NotAfter:dd/MM/yyyy} ({(cert.IsValidNow ? "Còn hạn" : "Hết hạn")})";
                TxtDetailAlgorithm.Text = cert.KeyAlgorithm;
                TxtDetailThumbprint.Text = cert.Thumbprint;
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
                UpdateStatus($"Lỗi nạp mẫu chữ ký: {ex.Message}");
            }
        }

        private void CmbProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbProfiles.SelectedItem is SignatureProfile profile)
            {
                TxtDimensions.Text = $"{profile.SignatureWidth}x{profile.SignatureHeight}";

                if (CmbDisplayMode != null)
                {
                    CmbDisplayMode.SelectedIndex = profile.Mode switch
                    {
                        1 => 0, // Chỉ ảnh
                        2 => 1, // Ảnh + Chữ
                        0 => 2, // Chỉ chữ
                        _ => 0
                    };
                }

                if (!string.IsNullOrWhiteSpace(profile.ImagePath) && File.Exists(profile.ImagePath))
                {
                    SetAndPersistStampImage(profile.ImagePath);
                }
                else
                {
                    // Tự động kiểm tra file quyen.png trên Desktop người dùng nếu có
                    string defaultDesktopStamp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "quyen.png");
                    if (File.Exists(defaultDesktopStamp))
                    {
                        SetAndPersistStampImage(defaultDesktopStamp);
                    }
                    else
                    {
                        ClearStampImage();
                    }
                }
            }
        }

        private void SetAndPersistStampImage(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                ClearStampImage();
                return;
            }

            try
            {
                TxtStampImagePath.Text = filePath;

                // Nạp ảnh xem trước trong Tab 1
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(filePath);
                bitmap.EndInit();

                ImgStampPreview.Source = bitmap;
                PnlStampPlaceholder.Visibility = Visibility.Collapsed;
                PnlStampLoaded.Visibility = Visibility.Visible;
                BtnClearStampImage.Visibility = Visibility.Visible;

                TxtStampFileName.Text = Path.GetFileName(filePath);
                TxtStampDimensionsInfo.Text = $"Kích thước: {bitmap.PixelWidth}x{bitmap.PixelHeight}px • Dung lượng: {new FileInfo(filePath).Length / 1024:N0} KB";
                TxtStampFullPath.Text = filePath;

                // Đồng bộ sang chip hiển thị trên Tab 2
                ImgTab2StampThumb.Source = bitmap;
                ImgTab2StampThumb.Visibility = Visibility.Visible;
                TxtTab2StampStatus.Text = $"🔴 Con dấu: {Path.GetFileName(filePath)}";
                BtnTab2SelectStamp.Content = "Đổi ảnh";

                // Tự động gán vào Profile đang chọn và lưu ngay xuống ổ đĩa
                if (CmbProfiles.SelectedItem is SignatureProfile profile)
                {
                    profile.ImagePath = filePath;
                    _profileManager.SaveProfiles(_profiles);
                }

                UpdateStatus($"Đã nạp và lưu ảnh con dấu: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Lỗi hiển thị ảnh con dấu: {ex.Message}");
            }
        }

        private void ClearStampImage()
        {
            TxtStampImagePath.Text = string.Empty;
            ImgStampPreview.Source = null;
            PnlStampPlaceholder.Visibility = Visibility.Visible;
            PnlStampLoaded.Visibility = Visibility.Collapsed;
            BtnClearStampImage.Visibility = Visibility.Collapsed;

            ImgTab2StampThumb.Source = null;
            ImgTab2StampThumb.Visibility = Visibility.Collapsed;
            TxtTab2StampStatus.Text = "⚪ Con dấu: Chưa chọn";
            BtnTab2SelectStamp.Content = "+ Thêm ảnh";

            if (CmbProfiles.SelectedItem is SignatureProfile profile)
            {
                profile.ImagePath = null;
                _profileManager.SaveProfiles(_profiles);
            }

            UpdateStatus("Đã gỡ bỏ ảnh con dấu.");
        }

        private void BtnSelectStampImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Chọn hình ảnh con dấu đỏ hoặc chữ ký tay quét",
                Filter = "Hình ảnh (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|Tất cả tệp (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (dialog.ShowDialog() == true)
            {
                SetAndPersistStampImage(dialog.FileName);
            }
        }

        private void BtnTab2ChangeStamp_Click(object sender, RoutedEventArgs e)
        {
            BtnSelectStampImage_Click(sender, e);
        }

        private void BtnClearStampImage_Click(object sender, RoutedEventArgs e)
        {
            ClearStampImage();
        }

        private void BtnSaveProfile_Click(object sender, RoutedEventArgs e)
        {
            if (CmbProfiles.SelectedItem is SignatureProfile profile)
            {
                var dimParts = TxtDimensions.Text.Split('x', 'X');
                if (dimParts.Length == 2)
                {
                    if (int.TryParse(dimParts[0].Trim(), out int w)) profile.SignatureWidth = w;
                    if (int.TryParse(dimParts[1].Trim(), out int h)) profile.SignatureHeight = h;
                }

                if (CmbDisplayMode != null)
                {
                    profile.Mode = CmbDisplayMode.SelectedIndex switch
                    {
                        0 => 1, // Chỉ ảnh
                        1 => 2, // Ảnh + Chữ
                        2 => 0, // Chỉ chữ
                        _ => 1
                    };
                }

                string stampPath = TxtStampImagePath.Text.Trim();
                profile.ImagePath = (!string.IsNullOrWhiteSpace(stampPath) && File.Exists(stampPath)) ? stampPath : null;
                profile.SignerName = TxtSignerDisplayName.Text.Trim();

                _profileManager.SaveProfiles(_profiles);
                UpdateStatus($"Đã lưu thành công cấu hình mẫu chữ ký: '{profile.Name}'");
                MessageBox.Show($"Đã lưu mẫu chữ ký '{profile.Name}' thành công!\nẢnh con dấu: {(profile.ImagePath != null ? Path.GetFileName(profile.ImagePath) : "Chưa chọn")}\nKích thước: {profile.SignatureWidth}x{profile.SignatureHeight}px", 
                    "Lưu Cấu Hình Mẫu Ký", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void CmbDisplayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbProfiles.SelectedItem is SignatureProfile profile && CmbDisplayMode != null)
            {
                profile.Mode = CmbDisplayMode.SelectedIndex switch
                {
                    0 => 1, // Chỉ ảnh
                    1 => 2, // Ảnh + Chữ
                    2 => 0, // Chỉ chữ
                    _ => 1
                };
            }
        }

        private void BtnGoToSignWorkspace_Click(object sender, RoutedEventArgs e)
        {
            // Chuyển sang Tab 2 (Soạn thảo & Ký Văn Bản)
            MainTabControl.SelectedIndex = 1;
        }

        #endregion

        #region TAB 2: SOẠN THẢO, XEM & KÝ TRỰC TIẾP TRONG GIAO DIỆN

        private async void BtnSelectWord_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Tải tệp Word (.docx, .doc) lên để xem & điều chỉnh trực tiếp",
                Filter = "Tài liệu Word (*.docx;*.doc)|*.docx;*.doc|Tất cả tệp (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                string wordPath = dialog.FileName;
                _originalWordFilePath = wordPath;
                _currentWorkingPdfPath = null;

                TxtCurrentFileName.Text = Path.GetFileName(wordPath);
                TxtCurrentFileStatus.Text = $"Đang tải cấu trúc tài liệu ({new FileInfo(wordPath).Length / 1024:N0} KB)...";
                PnlEditingToolbar.Visibility = Visibility.Visible;
                BtnConvertToPdfAndReadySign.IsEnabled = false;
                BtnInsertSignatureStamp.IsEnabled = false;
                BtnSignCurrentDocument.IsEnabled = false;

                try
                {
                    // Chuyển sang HTML có nhúng định dạng A4 và kích hoạt chế độ chỉnh sửa trực tiếp (contentEditable)
                    string htmlPath = await _wordConverter.ConvertWordToEditableHtmlAsync(wordPath);

                    if (_isWebViewInitialized)
                    {
                        PnlPreviewPlaceholder.Visibility = Visibility.Collapsed;
                        PdfPreviewControl.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                    }

                    TxtCurrentFileStatus.Text = "Đang ở chế độ xem & điều chỉnh trực tiếp. Bạn có thể sửa văn bản ngay bên dưới, sau đó bấm 'Chuyển Qua PDF Để Ký'.";
                    BtnConvertToPdfAndReadySign.IsEnabled = true;
                    UpdateStatus($"Đã nạp văn bản Word: {Path.GetFileName(wordPath)}. Sẵn sàng xem và điều chỉnh.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Không thể hiển thị tài liệu Word:\n{ex.Message}", "Lỗi Nạp Tài Liệu", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnSelectPdf_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Tải tệp tin PDF lên để ký số",
                Filter = "Tài liệu PDF (*.pdf)|*.pdf|Tất cả tệp (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                string pdfPath = dialog.FileName;
                _originalWordFilePath = null;
                _currentWorkingPdfPath = pdfPath;

                TxtCurrentFileName.Text = Path.GetFileName(pdfPath);
                TxtCurrentFileStatus.Text = $"Tệp PDF sẵn sàng ký số ({new FileInfo(pdfPath).Length / 1024:N0} KB).";
                PnlEditingToolbar.Visibility = Visibility.Collapsed;
                BtnConvertToPdfAndReadySign.IsEnabled = false;
                BtnSignCurrentDocument.IsEnabled = true;

                await DisplayPdfInInteractiveViewerAsync(pdfPath);

                UpdateStatus($"Đã nạp tệp PDF trực tiếp: {Path.GetFileName(pdfPath)}");
            }
        }

        private async void BtnExecuteConvertWord_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_originalWordFilePath) || !File.Exists(_originalWordFilePath))
            {
                MessageBox.Show("Vui lòng tải tệp Word lên trước khi chuyển đổi.", "Chưa có file", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnConvertToPdfAndReadySign.IsEnabled = false;
            PrgConvertLoading.Visibility = Visibility.Visible;
            TxtCurrentFileStatus.Text = "Đang chuyển đổi tài liệu sang chuẩn PDF Nghị định 30/2020/NĐ-CP...";
            UpdateStatus("Bắt đầu chuyển đổi Word sang PDF...");

            try
            {
                string pdfPath = await _wordConverter.ConvertWordToPdfAsync(_originalWordFilePath);
                _currentWorkingPdfPath = pdfPath;

                // Nạp file PDF chuẩn vào WebView2 bằng Engine tương tác
                await DisplayPdfInInteractiveViewerAsync(pdfPath);

                TxtCurrentFileName.Text = Path.GetFileName(pdfPath);
                TxtCurrentFileStatus.Text = "Tài liệu PDF chuẩn đã được tạo và nạp sẵn sàng ký số.";
                BtnSignCurrentDocument.IsEnabled = true;
                UpdateStatus($"Đã xuất tệp PDF hoàn chỉnh: {Path.GetFileName(pdfPath)}");

                MessageBox.Show(
                    $"Chuyển đổi sang PDF thành công!\n\nTệp tin đã lưu tại:\n{pdfPath}\n\nNội dung văn bản chuẩn đã được hiển thị trên màn hình. Bạn có thể bấm '📍 Chèn Chữ Ký' để kéo thả con dấu đến đúng vị trí cần ký, sau đó bấm '✍ TIẾN HÀNH KÝ SỐ'.",
                    "Chuyển Đổi Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi chuyển đổi sang PDF:\n{ex.Message}", "Lỗi Chuyển Đổi", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnConvertToPdfAndReadySign.IsEnabled = true;
            }
            finally
            {
                PrgConvertLoading.Visibility = Visibility.Collapsed;
            }
        }

        private async Task DisplayPdfInInteractiveViewerAsync(string pdfPath)
        {
            if (!_isWebViewInitialized || !File.Exists(pdfPath)) return;

            PnlPreviewPlaceholder.Visibility = Visibility.Collapsed;
            _hasUserPlacedStamp = false;
            BtnInsertSignatureStamp.IsEnabled = true;

            // Đọc byte của PDF và chuyển sang Base64
            byte[] pdfBytes = await File.ReadAllBytesAsync(pdfPath);
            string base64Pdf = Convert.ToBase64String(pdfBytes);

            string currentSource = PdfPreviewControl.Source?.ToString() ?? "";
            bool isViewerAlreadyLoaded = currentSource.Contains("pdf_viewer.html", StringComparison.OrdinalIgnoreCase);

            if (!isViewerAlreadyLoaded)
            {
                var tcs = new TaskCompletionSource<bool>();
                EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs>? navHandler = null;
                navHandler = (s, e) =>
                {
                    PdfPreviewControl.NavigationCompleted -= navHandler;
                    tcs.TrySetResult(e.IsSuccess);
                };
                PdfPreviewControl.NavigationCompleted += navHandler;
                PdfPreviewControl.CoreWebView2.Navigate("https://vgcaviewer.local/pdf_viewer.html");
                await tcs.Task;
            }

            // Đảm bảo DOM và script loadPdfFromBase64 đã sẵn sàng
            for (int i = 0; i < 30; i++)
            {
                string checkScript = await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync("typeof loadPdfFromBase64 === 'function'");
                if (checkScript == "true") break;
                await Task.Delay(100);
            }

            await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync($"loadPdfFromBase64('{base64Pdf}');");
        }

        private async void BtnInsertSignatureStamp_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentWorkingPdfPath) || !File.Exists(_currentWorkingPdfPath))
            {
                if (!string.IsNullOrWhiteSpace(_originalWordFilePath))
                {
                    MessageBox.Show("Bạn đang xem văn bản ở dạng Word. Vui lòng bấm nút '⚡ Chuyển Qua PDF Để Ký' trước khi thực hiện chèn chữ ký.",
                        "Cần Chuyển Sang PDF", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Vui lòng tải tệp Word hoặc PDF lên trước khi chèn chữ ký.",
                        "Chưa Có Tài Liệu", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }

            // Xác định hình ảnh con dấu / chữ ký
            string? stampPath = TxtStampImagePath.Text.Trim();
            if (string.IsNullOrWhiteSpace(stampPath) || stampPath.StartsWith("Ảnh con dấu") || !File.Exists(stampPath))
            {
                if (CmbProfiles.SelectedItem is SignatureProfile p && !string.IsNullOrWhiteSpace(p.ImagePath) && File.Exists(p.ImagePath))
                {
                    stampPath = p.ImagePath;
                }
                else
                {
                    string defaultDesktopStamp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "quyen.png");
                    if (File.Exists(defaultDesktopStamp))
                    {
                        stampPath = defaultDesktopStamp;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(stampPath) || !File.Exists(stampPath))
            {
                var ofd = new OpenFileDialog
                {
                    Title = "Chọn hình ảnh con dấu hoặc chữ ký tay để chèn",
                    Filter = "Hình ảnh (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|Tất cả tệp (*.*)|*.*",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                };
                if (ofd.ShowDialog() == true)
                {
                    stampPath = ofd.FileName;
                    SetAndPersistStampImage(stampPath);
                }
                else
                {
                    MessageBox.Show("Vui lòng chọn ảnh con dấu hoặc chữ ký để chèn lên tài liệu.", "Chưa Chọn Ảnh", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // Đọc ảnh và chuyển sang Data URL (Base64) để inject trực tiếp vào viewer
            byte[] imgBytes = await File.ReadAllBytesAsync(stampPath);
            string ext = Path.GetExtension(stampPath).TrimStart('.').ToLower();
            string mimeType = ext switch
            {
                "jpg" or "jpeg" => "image/jpeg",
                "png" => "image/png",
                "gif" => "image/gif",
                _ => "image/png"
            };
            string dataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(imgBytes)}";

            // Kích thước mặc định
            int widthPx = 140;
            int heightPx = 140;
            var dimParts = TxtDimensions.Text.Split('x', 'X');
            if (dimParts.Length == 2 && int.TryParse(dimParts[0].Trim(), out int w) && int.TryParse(dimParts[1].Trim(), out int h))
            {
                widthPx = Math.Max(60, w);
                heightPx = Math.Max(40, h);
            }

            // Gọi hàm JavaScript insertStamp
            string script = $"insertStamp('{dataUrl}', {widthPx}, {heightPx});";
            await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync(script);

            _hasUserPlacedStamp = true;
            UpdateStatus("Đã chèn khung chữ ký tương tác trên trang văn bản. Hãy dùng chuột kéo thả hoặc co giãn đến vị trí mong muốn.");
        }

        private async void BtnSignDocument_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentWorkingPdfPath) || !File.Exists(_currentWorkingPdfPath))
            {
                MessageBox.Show("Vui lòng tải file PDF hoặc thực hiện chuyển đổi Word sang PDF trước khi ký.",
                    "Chưa có tệp tin PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_currentActiveCert == null)
            {
                var result = MessageBox.Show(
                    "Chưa phát hiện chứng thư số Ban Cơ yếu Chính phủ đang hoạt động.\nBạn có muốn chuyển sang Tab 1 để kiểm tra và đăng nhập tài khoản VGCA không?",
                    "Chưa Đăng Nhập VGCA", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    MainTabControl.SelectedIndex = 0;
                }
                return;
            }

            // Đọc cấu hình mẫu ký: Ưu tiên tọa độ kéo thả tương tác của người dùng
            int pageNumber = -1;
            float coordX = 380;
            float coordY = 120;
            float width = 120;
            float height = 120;

            if (_hasUserPlacedStamp)
            {
                pageNumber = _userStampPage;
                coordX = _userStampX;
                coordY = _userStampY;
                width = _userStampWidth;
                height = _userStampHeight;
            }
            else
            {
                int.TryParse(TxtPageNumber.Text.Trim(), out pageNumber);
                float.TryParse(TxtCoordX.Text.Trim(), out coordX);
                float.TryParse(TxtCoordY.Text.Trim(), out coordY);
                var dimParts = TxtDimensions.Text.Split('x', 'X');
                if (dimParts.Length == 2)
                {
                    float.TryParse(dimParts[0].Trim(), out width);
                    float.TryParse(dimParts[1].Trim(), out height);
                }
            }

            // Xác định tệp hình ảnh con dấu (từ ô nhập, từ Profile đã lưu, hoặc từ Desktop quyen.png)
            string? stampPath = TxtStampImagePath.Text.Trim();
            if (string.IsNullOrWhiteSpace(stampPath) || stampPath.StartsWith("Ảnh con dấu") || !File.Exists(stampPath))
            {
                if (CmbProfiles.SelectedItem is SignatureProfile p && !string.IsNullOrWhiteSpace(p.ImagePath) && File.Exists(p.ImagePath))
                {
                    stampPath = p.ImagePath;
                }
                else
                {
                    string defaultDesktopStamp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "quyen.png");
                    if (File.Exists(defaultDesktopStamp))
                    {
                        stampPath = defaultDesktopStamp;
                    }
                    else
                    {
                        stampPath = null;
                    }
                }
            }

            var selectedProfile = CmbProfiles.SelectedItem as SignatureProfile;
            if (selectedProfile != null)
            {
                if (!string.IsNullOrWhiteSpace(stampPath))
                {
                    selectedProfile.ImagePath = stampPath;
                }
                if (CmbDisplayMode != null)
                {
                    selectedProfile.Mode = CmbDisplayMode.SelectedIndex switch
                    {
                        0 => 1, // Chỉ ảnh
                        1 => 2, // Ảnh + Chữ
                        2 => 0, // Chỉ chữ
                        _ => 1
                    };
                }
            }

            string dir = Path.GetDirectoryName(_currentWorkingPdfPath) ?? AppDomain.CurrentDomain.BaseDirectory;
            string baseName = Path.GetFileNameWithoutExtension(_currentWorkingPdfPath);
            string outputPath = Path.Combine(dir, $"{baseName}_SIGNED.pdf");

            var request = new SigningRequest
            {
                SourcePdfPath = _currentWorkingPdfPath,
                OutputPdfPath = outputPath,
                Certificate = _currentActiveCert,
                PageNumber = pageNumber,
                X = coordX,
                Y = coordY,
                Width = width,
                Height = height,
                Reason = TxtReason.Text.Trim(),
                SignerDisplayName = TxtSignerDisplayName.Text.Trim(),
                StampImagePath = stampPath,
                Profile = selectedProfile,
                UseTSA = ChkUseTSA.IsChecked == true,
                TSAUrl = "http://ca.gov.vn/tsa"
            };

            BtnSignCurrentDocument.IsEnabled = false;
            UpdateStatus("Đang tiến hành ký số văn bản chuẩn PAdES LTV...");

            try
            {
                string signedPath = await _pdfSigner.SignPdfAsync(request);
                _lastSignedPdfPath = signedPath;

                // Nạp file đã ký vào Interactive Viewer để người dùng thẩm định kết quả trực quan
                await DisplayPdfInInteractiveViewerAsync(signedPath);

                TxtCurrentFileName.Text = Path.GetFileName(signedPath);
                TxtCurrentFileStatus.Text = "✅ VĂN BẢN ĐÃ ĐƯỢC KÝ SỐ THÀNH CÔNG VỚI CHỨNG THƯ BAN CƠ YẾU CHÍNH PHỦ.";

                // Cập nhật sẵn sang Tab 3 (Upload Web)
                TxtUploadFilePath.Text = signedPath;
                TxtUploadDocTitle.Text = Path.GetFileNameWithoutExtension(signedPath);

                var choice = MessageBox.Show(
                    $"Ký số văn bản thành công!\n\nTệp tin đã lưu tại:\n{signedPath}\n\nChữ ký số đã được nhúng chính xác tại vị trí bạn chọn với dấu thời gian TSA Ban Cơ yếu.\nBạn có muốn chuyển sang Tab 3 để tải tệp lên Cổng Web ngay không?",
                    "Ký Số Thành Công", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (choice == MessageBoxResult.Yes)
                {
                    MainTabControl.SelectedIndex = 2; // Chuyển sang Tab 3
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Quá trình ký số gặp lỗi:\n\n{ex.Message}\n\nVui lòng kiểm tra lại quyền truy cập khóa riêng tư của Virtual CSP hoặc mã PIN Token.",
                    "Lỗi Ký Số", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus($"Lỗi ký số: {ex.Message}");
            }
            finally
            {
                BtnSignCurrentDocument.IsEnabled = true;
            }
        }

        #region Các lệnh điều chỉnh văn bản trực tiếp (ExecCommand trong WebView2)

        private void BtnFormatBold_Click(object sender, RoutedEventArgs e)
        {
            PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('bold', false, null);");
        }

        private void BtnFormatItalic_Click(object sender, RoutedEventArgs e)
        {
            PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('italic', false, null);");
        }

        private void BtnFormatUnderline_Click(object sender, RoutedEventArgs e)
        {
            PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('underline', false, null);");
        }

        private void BtnAlignLeft_Click(object sender, RoutedEventArgs e)
        {
            PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('justifyLeft', false, null);");
        }

        private void BtnAlignCenter_Click(object sender, RoutedEventArgs e)
        {
            PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('justifyCenter', false, null);");
        }

        private void BtnAlignRight_Click(object sender, RoutedEventArgs e)
        {
            PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('justifyRight', false, null);");
        }

        private void BtnAlignJustify_Click(object sender, RoutedEventArgs e)
        {
            PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('justifyFull', false, null);");
        }

        #endregion

        #endregion

        #region TAB 3: GỬI LÊN CỔNG WEB

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
            TxtHttpStatus.Foreground = Brushes.DarkOrange;

            try
            {
                var result = await _webUploader.UploadSignedPdfAsync(request);

                TxtHttpStatus.Text = $"HTTP {result.StatusCode} ({(result.IsSuccess ? "THÀNH CÔNG" : "LỖI")})";
                TxtHttpStatus.Foreground = result.IsSuccess ? Brushes.ForestGreen : Brushes.Crimson;
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
                TxtHttpStatus.Foreground = Brushes.Crimson;
                TxtHttpResponseContent.Text = ex.ToString();
            }
            finally
            {
                BtnExecuteUpload.IsEnabled = true;
            }
        }

        #endregion

        #region TAB 4: KIỂM TRA CHỮ KÝ (VERIFY)

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

                UpdateStatus($"Đang phân tích chữ ký trong tệp: {Path.GetFileName(pdfPath)}...");

                var signatures = _pdfVerifier.VerifyPdf(pdfPath);
                ListSignatures.ItemsSource = signatures;

                if (signatures.Count > 0)
                {
                    UpdateStatus($"Phát hiện {signatures.Count} chữ ký số trong tài liệu.");
                    MessageBox.Show(
                        $"Tìm thấy {signatures.Count} chữ ký số trong tài liệu.\nKiểm tra chi tiết trạng thái ở bảng bên dưới.",
                        "Kết Quả Xác Minh", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    UpdateStatus("Tài liệu không có chữ ký số hoặc chữ ký chưa được lưu đúng định dạng.");
                    MessageBox.Show("Tài liệu này không chứa bất kỳ chữ ký số nào.",
                        "Không có chữ ký", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        #endregion
    }
}