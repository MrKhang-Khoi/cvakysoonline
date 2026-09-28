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
        private readonly VgcaAuthService _vgcaAuth = new();

        private List<CertificateModel> _certificates = new();
        private List<SignatureProfile> _profiles = new();
        private CertificateModel? _currentActiveCert;

        private string? _originalWordFilePath;
        private string? _currentWorkingPdfPath;
        private string? _lastSignedPdfPath;
        private bool _isWebViewInitialized;

        // VỊ TRÍ CHỮ KÝ DO NGƯỜI DÙNG KÉO THẢ TƯƠNG TÁC TRÊN BẢN XEM TRƯỚC
        private bool _hasUserPlacedStamp;
        private int _userStampPage = 1;
        // ponytail: clamp stamp coords to page size at sign-time
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
            _vgcaAuth.LoginStateChanged += OnVgcaLoginStateChanged;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateStatus("Khởi tạo hệ thống VGCA Sign Tool Pro...");
                await InitializePreviewEngineAsync();

                InitializeVgcaAuthRoster();
                LoadCertificatesAndVerifyVGCALogin();
                LoadProfiles();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Lỗi khởi tạo: {ex.Message}");
                MessageBox.Show($"Không thể khởi tạo ứng dụng:\n{ex.Message}",
                    "Lỗi Khởi Tạo", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task InitializePreviewEngineAsync()
        {
            try
            {
                // Chuẩn Windows Desktop: Lưu trữ Cache WebView2 trong %LocalAppData% của người dùng
                // Triệt tiêu 100% lỗi UnauthorizedAccessException khi phần mềm được cài đặt vào C:\Program Files
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string webView2CacheDir = Path.Combine(localAppData, "VGCASignToolPro", "WebView2Cache");
                if (!Directory.Exists(webView2CacheDir))
                {
                    Directory.CreateDirectory(webView2CacheDir);
                }

                if (PdfPreviewControl == null)
                {
                    throw new InvalidOperationException("PdfPreviewControl chưa được khởi tạo từ XAML.");
                }

                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, webView2CacheDir);
                await PdfPreviewControl.EnsureCoreWebView2Async(env);

                // Tìm thư mục wwwroot một cách linh hoạt (hỗ trợ cả Development, Publish, và Installed mode trên mọi máy)
                string wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
                if (!Directory.Exists(wwwroot))
                {
                    string? currentDir = AppDomain.CurrentDomain.BaseDirectory;
                    for (int i = 0; i < 4 && currentDir != null; i++)
                    {
                        string candidate = Path.Combine(currentDir, "wwwroot");
                        if (Directory.Exists(candidate))
                        {
                            wwwroot = candidate;
                            break;
                        }
                        currentDir = Path.GetDirectoryName(currentDir);
                    }
                }

                if (!Directory.Exists(wwwroot))
                {
                    throw new DirectoryNotFoundException($"Không tìm thấy thư mục wwwroot. Đã tìm tại: {wwwroot}");
                }

                PdfPreviewControl.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "vgcaviewer.local",
                    wwwroot,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.DenyCors);

                PdfPreviewControl.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                _isWebViewInitialized = true;
                UpdateStatus("Engine hiển thị WebView2 và PDF.js tương tác đã sẵn sàng.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi khởi tạo WebView2: {ex}");
                _isWebViewInitialized = false;
                BtnInsertSignatureStamp.IsEnabled = false;
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
                        if (root.TryGetProperty("coords", out var coords) &&
                            coords.TryGetProperty("pageNumber", out var pn) && pn.TryGetInt32(out int pageNum) &&
                            coords.TryGetProperty("x", out var cx) && cx.TryGetDouble(out double xVal) &&
                            coords.TryGetProperty("y", out var cy) && cy.TryGetDouble(out double yVal) &&
                            coords.TryGetProperty("width", out var cw) && cw.TryGetDouble(out double wVal) &&
                            coords.TryGetProperty("height", out var ch) && ch.TryGetDouble(out double hVal))
                        {
                            _hasUserPlacedStamp = true;
                            _userStampPage = pageNum;
                            _userStampX = (float)xVal;
                            _userStampY = (float)yVal;
                            _userStampWidth = (float)wVal;
                            _userStampHeight = (float)hVal;

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
                        if (root.TryGetProperty("totalPages", out var tp) && tp.TryGetInt32(out int totalPages))
                        {
                            UpdateStatus($"Đã nạp xong tài liệu PDF tương tác ({totalPages} trang).");
                        }
                        BtnInsertSignatureStamp.IsEnabled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi xử lý thông điệp WebView: {ex}");
                UpdateStatus($"Lỗi xử lý thông điệp WebView: {ex.Message}");
            }
        }

        private void UpdateStatus(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtStatusBar.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
            });
        }

        #region TAB 1: THÔNG TIN CHỨNG THƯ & ĐĂNG NHẬP VGCA TÍCH HỢP

        private void InitializeVgcaAuthRoster()
        {
            try
            {
                var teachers = _vgcaAuth.GetTeacherList();
                if (teachers == null || teachers.Count == 0)
                {
                    UpdateStatus("Danh bạ VGCA trống hoặc không khả dụng.");
                    return;
                }

                CmbRosterQuickSelect.ItemsSource = teachers;
                CmbRosterQuickSelect.DisplayMemberPath = "FullName";

                // Chọn phần tử đầu tiên trong danh bạ nếu khả dụng
                CmbRosterQuickSelect.SelectedIndex = 0;

                GenerateAndDisplayVgcaQr();
            }
            catch (Exception ex)
            {
                UpdateStatus($"Lỗi khởi tạo danh bạ VGCA: {ex.Message}");
            }
        }

        private void GenerateAndDisplayVgcaQr()
        {
            try
            {
                var qrBitmap = _vgcaAuth.GenerateQrCode(out _);
                if (qrBitmap == null)
                {
                    UpdateStatus("Không thể sinh mã QR VGCA.");
                    return;
                }
                ImgVgcaQrCode.Source = qrBitmap;
            }
            catch (Exception ex)
            {
                UpdateStatus($"Lỗi sinh mã QR VGCA: {ex.Message}");
            }
        }

        private void OnVgcaLoginStateChanged(bool isLoggedIn, VgcaTeacherModel? teacher)
        {
            Dispatcher.Invoke(() =>
            {
                if (isLoggedIn && teacher != null)
                {
                    GrpVgcaLoginBox.Visibility = Visibility.Collapsed;
                    GrpVgcaActiveCard.Visibility = Visibility.Visible;
                    BorderVgcaAuthError.Visibility = Visibility.Collapsed;

                    BadgeVgcaGlobalStatus.Background = new SolidColorBrush(Color.FromRgb(6, 95, 70));
                    TxtVgcaGlobalStatusIcon.Text = "✅";
                    TxtVgcaGlobalStatusText.Text = $"VGCA: {teacher.FullName}";

                    TxtDetailSignerName.Text = teacher.FullName;
                    TxtDetailEmail.Text = !string.IsNullOrWhiteSpace(teacher.Email) ? teacher.Email : "Chưa đăng ký email trong chứng thư";
                    TxtDetailOrg.Text = !string.IsNullOrWhiteSpace(teacher.Department)
                        ? $"{teacher.Department} - {teacher.Organization ?? "N/A"}"
                        : teacher.Organization ?? "Không rõ tổ chức";
                    TxtSignerDisplayName.Text = teacher.FullName;
                    TxtUploadSigner.Text = teacher.FullName;
                    TxtUploadDepartment.Text = teacher.Department ?? "";
                    TxtQuickCertNotice.Text = $"Chứng thư: {teacher.FullName} (Ban Cơ yếu G2)";

                    // Mở khóa Tab 2
                    TabItemSignWorkspace.Header = "  2. Soạn Thảo, Xem & Ký Số Trực Tiếp  ";
                    OverlayTab2Locked.Visibility = Visibility.Collapsed;

                    // Đồng bộ chứng thư trong kho Windows Store nếu có
                    if (_certificates != null && _certificates.Count > 0)
                    {
                        var matchedCert = _certificates.FirstOrDefault(c =>
                            c.IsVGCACertificate && (
                                c.CommonName.Contains(teacher.FullName, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(teacher.SerialNumber) && string.Equals(c.SerialNumber, teacher.SerialNumber, StringComparison.OrdinalIgnoreCase))
                            ));

                        if (matchedCert != null)
                        {
                            _currentActiveCert = matchedCert;
                            CmbCertificates.SelectedItem = matchedCert;
                            TxtDetailIssuer.Text = matchedCert.Issuer;
                            TxtDetailSerial.Text = matchedCert.SerialNumber;
                            TxtDetailValidity.Text = $"{matchedCert.NotBefore:dd/MM/yyyy} đến {matchedCert.NotAfter:dd/MM/yyyy} ({(matchedCert.IsValidNow ? "Còn hiệu lực" : "Đã hết hạn")})";
                            TxtDetailAlgorithm.Text = matchedCert.KeyAlgorithm;
                            TxtDetailThumbprint.Text = matchedCert.Thumbprint;
                        }
                    }

                    var maskedCccd = teacher.Cccd?.Length > 4 
                        ? $"***{teacher.Cccd[^4..]}" 
                        : "***";
                    UpdateStatus($"Phiên làm việc VGCA Virtual CSP đã kích hoạt: {teacher.FullName} ({maskedCccd})");
                }
                else
                {
                    GrpVgcaLoginBox.Visibility = Visibility.Visible;
                    GrpVgcaActiveCard.Visibility = Visibility.Collapsed;

                    BadgeVgcaGlobalStatus.Background = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                    TxtVgcaGlobalStatusIcon.Text = "🔒";
                    TxtVgcaGlobalStatusText.Text = "VGCA: Chưa Đăng Nhập";
                    TxtQuickCertNotice.Text = "Chưa kết nối chứng thư VGCA";

                    // Khóa Tab 2
                    TabItemSignWorkspace.Header = "  2. Soạn Thảo, Xem & Ký Số Trực Tiếp (🔒 Khóa)  ";
                    OverlayTab2Locked.Visibility = Visibility.Visible;

                    UpdateStatus("Tài khoản VGCA chưa đăng nhập. Tab 2 bị khóa để đảm bảo an toàn.");
                }
            });
        }

        private void CmbRosterQuickSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbRosterQuickSelect.SelectedItem is VgcaTeacherModel teacher)
            {
                TxtVgcaCccd.Text = teacher.Cccd;
                BorderVgcaAuthError.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnVgcaLogin_Click(object sender, RoutedEventArgs e)
        {
            string cccd = TxtVgcaCccd.Text.Trim();
            string password = TxtVgcaPassword.Password;

            if (string.IsNullOrWhiteSpace(cccd) || string.IsNullOrEmpty(password))
            {
                BorderVgcaAuthError.Visibility = Visibility.Visible;
                TxtVgcaAuthError.Text = "Vui lòng nhập đầy đủ số CCCD và mật khẩu.";
                return;
            }

            // ponytail: sync call OK cho local-only auth. Chuyển async + disable button khi tích hợp API thật.
            UpdateStatus("Đang xác thực thông tin đăng nhập VGCA Virtual CSP...");
            var result = _vgcaAuth.AuthenticatePassword(cccd, password);

            if (result.Success && result.Teacher != null)
            {
                BorderVgcaAuthError.Visibility = Visibility.Collapsed;
                MessageBox.Show(
                    $"Đăng nhập thành công tài khoản VGCA Virtual CSP!\n\nCán bộ: {result.Teacher.FullName}\nĐơn vị: {result.Teacher.Department}\nSố CCCD: {result.Teacher.Cccd}\n\nKhu vực Soạn thảo & Ký số (Tab 2) đã được mở khóa sẵn sàng.",
                    "Xác Thực Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                BorderVgcaAuthError.Visibility = Visibility.Visible;
                TxtVgcaAuthError.Text = result.ErrorDescription;
                UpdateStatus($"Đăng nhập thất bại: {result.ErrorDescription}");
            }
        }

        private void BtnRefreshQr_Click(object sender, RoutedEventArgs e)
        {
            GenerateAndDisplayVgcaQr();
            UpdateStatus("Đã tạo mã QR đăng nhập VGCA mới.");
        }

        private void BtnApproveQr_Click(object sender, RoutedEventArgs e)
        {
            string cccd = TxtVgcaCccd.Text.Trim();
            if (string.IsNullOrWhiteSpace(cccd))
            {
                BorderVgcaAuthError.Visibility = Visibility.Visible;
                TxtVgcaAuthError.Text = "Vui lòng nhập số CCCD trước khi phê duyệt QR.";
                return;
            }

            if (_vgcaAuth == null)
            {
                BorderVgcaAuthError.Visibility = Visibility.Visible;
                TxtVgcaAuthError.Text = "Dịch vụ xác thực chưa sẵn sàng. Vui lòng khởi động lại.";
                return;
            }

            // ponytail: sync call OK cho local-only auth. Chuyển async + disable button khi tích hợp API thật.
            var result = _vgcaAuth.ApproveQrLogin(cccd);

            if (result.Success && result.Teacher != null)
            {
                BorderVgcaAuthError.Visibility = Visibility.Collapsed;
                MessageBox.Show(
                    $"Xác nhận phê duyệt mã QR thành công từ ứng dụng VGCA Mobile!\n\nCán bộ: {result.Teacher.FullName ?? "(Không rõ)"}\nĐơn vị: {result.Teacher.Department ?? "(Không rõ)"}\nSố CCCD: {result.Teacher.Cccd ?? "(Không rõ)"}\n\nKhu vực Soạn thảo & Ký số (Tab 2) đã được mở khóa.",
                    "Xác Thực QR Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                BorderVgcaAuthError.Visibility = Visibility.Visible;
                string err = result.ErrorDescription ?? "Xác thực QR thất bại. Vui lòng thử lại.";
                TxtVgcaAuthError.Text = err;
                UpdateStatus($"Lỗi xác thực QR: {err}");
            }
        }

        private void BtnVgcaLogout_Click(object sender, RoutedEventArgs e)
        {
            var choice = MessageBox.Show(
                "Bạn có chắc chắn muốn đăng xuất tài khoản chữ ký số VGCA không?\n(Sau khi đăng xuất, Tab 2 sẽ bị khóa lại cho đến khi đăng nhập lại)",
                "Xác Nhận Đăng Xuất", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (choice == MessageBoxResult.Yes)
            {
                if (_vgcaAuth == null)
                {
                    UpdateStatus("Dịch vụ xác thực chưa sẵn sàng.");
                    return;
                }
                _vgcaAuth.Logout();
                GenerateAndDisplayVgcaQr();
                UpdateStatus("Đã đăng xuất tài khoản VGCA Virtual CSP.");
            }
        }

        private void BtnGoToLoginTab_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
        }

        private void LoadCertificatesAndVerifyVGCALogin()
        {
            try
            {
                _certificates = _certService.LoadCertificates(onlyWithPrivateKey: true);
                CmbCertificates.ItemsSource = null;
                CmbCertificates.ItemsSource = _certificates;

                if (_certificates == null || _certificates.Count == 0)
                {
                    UpdateStatus("Không tìm thấy chứng thư số nào có khóa riêng.");
                    OnVgcaLoginStateChanged(false, null);
                    return;
                }

                // Tìm chứng thư VGCA chuyên dùng của Ban Cơ yếu Chính phủ
                var vgcaCert = _certService.GetActiveVGCACertificate();

                if (vgcaCert != null)
                {
                    _currentActiveCert = vgcaCert;
                    CmbCertificates.SelectedItem = vgcaCert;

                    TxtDetailIssuer.Text = vgcaCert.Issuer ?? "";
                    TxtDetailSerial.Text = vgcaCert.SerialNumber ?? "";
                    TxtDetailValidity.Text = $"{vgcaCert.NotBefore:dd/MM/yyyy} đến {vgcaCert.NotAfter:dd/MM/yyyy} ({(vgcaCert.IsValidNow ? "Còn hiệu lực" : "Đã hết hạn")})";
                    TxtDetailAlgorithm.Text = vgcaCert.KeyAlgorithm ?? "";
                    TxtDetailThumbprint.Text = vgcaCert.Thumbprint ?? "";

                    if (_vgcaAuth.IsLoggedIn && _vgcaAuth.CurrentTeacher != null)
                    {
                        OnVgcaLoginStateChanged(true, _vgcaAuth.CurrentTeacher);
                    }
                    else
                    {
                        OnVgcaLoginStateChanged(false, null);
                    }

                    UpdateStatus($"Đã phát hiện chứng thư Ban Cơ yếu: {vgcaCert.CommonName ?? "VGCA"} (Serial: {vgcaCert.SerialNumber ?? "N/A"}). Vui lòng đăng nhập Tab 1.");
                }
                else
                {
                    _currentActiveCert = null;
                    if (_vgcaAuth.IsLoggedIn && _vgcaAuth.CurrentTeacher != null)
                    {
                        OnVgcaLoginStateChanged(true, _vgcaAuth.CurrentTeacher);
                    }
                    else
                    {
                        OnVgcaLoginStateChanged(false, null);
                    }

                    UpdateStatus("Cảnh báo: Chưa phát hiện tài khoản chữ ký số Ban Cơ yếu Chính phủ.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi kiểm tra chứng thư số: {ex}");
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
                MessageBox.Show("Đã mở ứng dụng VGCA Virtual CSP v2.0.\nVui lòng đăng nhập tài khoản chữ ký số Ban Cơ yếu của bạn, sau đó nhấn nút 'Quét Lại Token' trên phần mềm.",
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
                // ponytail: internal-only, thay bằng generic msg khi phân phối
                System.Diagnostics.Trace.TraceError($"Lỗi nạp mẫu chữ ký: {ex}");
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
                    string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    if (!string.IsNullOrEmpty(desktopPath))
                    {
                        string defaultDesktopStamp = Path.Combine(desktopPath, "quyen.png");
                        if (File.Exists(defaultDesktopStamp))
                        {
                            SetAndPersistStampImage(defaultDesktopStamp);
                            UpdateStatus($"Tự động nạp ảnh con dấu từ: {defaultDesktopStamp}");
                        }
                        else
                        {
                            ClearStampImage();
                        }
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

            var bitmap = new BitmapImage();
            try
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(filePath);
                bitmap.EndInit();
                bitmap.Freeze();
            }
            catch (Exception ex)
            {
                ClearStampImage();
                System.Diagnostics.Trace.TraceError($"Lỗi giải mã ảnh con dấu: {ex}");
                UpdateStatus($"Không thể nạp ảnh con dấu: {Path.GetFileName(filePath)}");
                return;
            }

            try
            {
                TxtStampImagePath.Text = filePath;

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
                ClearStampImage();
                System.Diagnostics.Trace.TraceError($"Lỗi hiển thị ảnh con dấu: {ex}");
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
                try
                {
                    _profileManager.SaveProfiles(_profiles);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError($"Lỗi lưu profile: {ex}");
                    UpdateStatus($"Lỗi lưu profile: {ex.Message}");
                }
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
                var dimText = TxtDimensions?.Text;
                if (!string.IsNullOrWhiteSpace(dimText))
                {
                    var dimParts = dimText.Split('x', 'X');
                    if (dimParts.Length == 2)
                    {
                        if (int.TryParse(dimParts[0].Trim(), out int w) && w > 0) profile.SignatureWidth = w;
                        if (int.TryParse(dimParts[1].Trim(), out int h) && h > 0) profile.SignatureHeight = h;
                    }
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

                try
                {
                    _profileManager.SaveProfiles(_profiles);
                    UpdateStatus($"Đã lưu thành công cấu hình mẫu chữ ký: '{profile.Name}'");
                    MessageBox.Show($"Đã lưu mẫu chữ ký '{profile.Name}' thành công!\nẢnh con dấu: {(profile.ImagePath != null ? Path.GetFileName(profile.ImagePath) : "Chưa chọn")}\nKích thước: {profile.SignatureWidth}x{profile.SignatureHeight}px", 
                        "Lưu Cấu Hình Mẫu Ký", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError($"Lỗi lưu profile: {ex}");
                    UpdateStatus($"Lỗi lưu profile: {ex.Message}");
                }
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
                BorderSignSuccessBanner.Visibility = Visibility.Collapsed;

                TxtCurrentFileName.Text = Path.GetFileName(wordPath);
                PnlEditingToolbar.Visibility = Visibility.Visible;
                BtnConvertToPdfAndReadySign.IsEnabled = false;
                BtnInsertSignatureStamp.IsEnabled = false;
                BtnSignCurrentDocument.IsEnabled = false;

                try
                {
                    long fileSizeKb = File.Exists(wordPath) ? new FileInfo(wordPath).Length / 1024 : 0;
                    TxtCurrentFileStatus.Text = $"Đang tải cấu trúc tài liệu ({fileSizeKb:N0} KB)...";

                    // Chuyển sang HTML có nhúng định dạng A4 và kích hoạt chế độ chỉnh sửa trực tiếp (contentEditable)
                    string htmlPath = await _wordConverter.ConvertWordToEditableHtmlAsync(wordPath);

                    if (_isWebViewInitialized && PdfPreviewControl?.CoreWebView2 != null)
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
                    TxtCurrentFileStatus.Text = "Nạp tài liệu thất bại.";
                    _originalWordFilePath = null;
                    System.Diagnostics.Trace.TraceError($"Lỗi nạp tài liệu Word: {ex}");
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
                if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                {
                    MessageBox.Show("Tệp PDF không hợp lệ hoặc không tồn tại.", "Lỗi Tệp", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _originalWordFilePath = null;
                _currentWorkingPdfPath = pdfPath;
                BorderSignSuccessBanner.Visibility = Visibility.Collapsed;

                try
                {
                    TxtCurrentFileName.Text = Path.GetFileName(pdfPath);
                    long fileSizeKb = new FileInfo(pdfPath).Length / 1024;
                    TxtCurrentFileStatus.Text = $"Tệp PDF sẵn sàng ký số ({fileSizeKb:N0} KB).";
                    PnlEditingToolbar.Visibility = Visibility.Collapsed;
                    BtnConvertToPdfAndReadySign.IsEnabled = false;
                    BtnSignCurrentDocument.IsEnabled = true;

                    await DisplayPdfInInteractiveViewerAsync(pdfPath);

                    UpdateStatus($"Đã nạp tệp PDF trực tiếp: {Path.GetFileName(pdfPath)}");
                }
                catch (Exception ex)
                {
                    TxtCurrentFileStatus.Text = "Nạp tệp PDF thất bại.";
                    _currentWorkingPdfPath = null;
                    System.Diagnostics.Trace.TraceError($"Lỗi nạp PDF: {ex}");
                    MessageBox.Show($"Không thể hiển thị tài liệu PDF:\n{ex.Message}", "Lỗi Nạp PDF", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnExecuteConvertWord_Click(object sender, RoutedEventArgs e)
        {
            if (_wordConverter == null)
            {
                MessageBox.Show("Dịch vụ chuyển đổi Word chưa được khởi tạo.", "Lỗi Hệ Thống", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

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
                System.Diagnostics.Trace.TraceError($"Lỗi chuyển đổi sang PDF: {ex}");
                MessageBox.Show($"Lỗi chuyển đổi sang PDF:\n{ex.Message}", "Lỗi Chuyển Đổi", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtCurrentFileStatus.Text = "Chuyển đổi thất bại.";
                UpdateStatus("Chuyển đổi Word sang PDF thất bại.");
            }
            finally
            {
                PrgConvertLoading.Visibility = Visibility.Collapsed;
                BtnConvertToPdfAndReadySign.IsEnabled = true;
            }
        }

        private async Task DisplayPdfInInteractiveViewerAsync(string pdfPath)
        {
            if (!_isWebViewInitialized || !File.Exists(pdfPath) || PdfPreviewControl?.CoreWebView2 == null)
            {
                TxtCurrentFileStatus.Text = "Trình xem tương tác chưa sẵn sàng.";
                return;
            }

            try
            {
                var fileInfo = new FileInfo(pdfPath);
                // Giới hạn kích thước file an toàn cho WebView2 in-memory Base64 (30MB)
                const long maxFileSize = 30 * 1024 * 1024;
                if (fileInfo.Length > maxFileSize)
                {
                    RollbackViewerUiState("Tệp PDF quá lớn (>30MB) để nạp vào trình xem tương tác.");
                    return;
                }

                // Đọc byte của PDF và chuyển sang Base64, sau đó giải phóng mảng byte ngay lập tức
                byte[]? pdfBytes = await File.ReadAllBytesAsync(pdfPath);
                string base64Pdf = Convert.ToBase64String(pdfBytes);
                pdfBytes = null;

                if (PdfPreviewControl == null || PdfPreviewControl.CoreWebView2 == null)
                {
                    RollbackViewerUiState("Lỗi: Thành phần trình xem đã bị giải phóng.");
                    return;
                }

                string currentSource = "";
                try
                {
                    currentSource = PdfPreviewControl.Source?.ToString() ?? "";
                }
                catch (ObjectDisposedException)
                {
                    RollbackViewerUiState("Lỗi: Trình xem đã đóng hoặc bị hủy.");
                    return;
                }

                bool isViewerAlreadyLoaded = currentSource.Contains("pdf_viewer.html", StringComparison.OrdinalIgnoreCase);

                if (!isViewerAlreadyLoaded)
                {
                    var tcs = new TaskCompletionSource<bool>();
                    EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs>? navHandler = null;
                    navHandler = (s, e) =>
                    {
                        try
                        {
                            PdfPreviewControl.NavigationCompleted -= navHandler;
                        }
                        catch (Exception)
                        {
                            // Bỏ qua lỗi gỡ handler nếu control đã unbind
                        }
                        tcs.TrySetResult(e.IsSuccess);
                    };
                    PdfPreviewControl.NavigationCompleted += navHandler;
                    PdfPreviewControl.CoreWebView2.Navigate("https://vgcaviewer.local/pdf_viewer.html");

                    var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000));
                    if (completedTask != tcs.Task)
                    {
                        try { PdfPreviewControl.NavigationCompleted -= navHandler; } catch { }
                        RollbackViewerUiState("Lỗi: Quá thời gian chờ tải trình xem PDF.");
                        return;
                    }

                    bool navSuccess = await tcs.Task;
                    if (!navSuccess)
                    {
                        RollbackViewerUiState("Lỗi: Không thể điều hướng tới trang xem PDF.");
                        return;
                    }
                }

                if (PdfPreviewControl == null || PdfPreviewControl.CoreWebView2 == null)
                {
                    RollbackViewerUiState("Lỗi: WebView2 runtime không khả dụng.");
                    return;
                }

                // Đảm bảo DOM và script loadPdfFromBase64 đã sẵn sàng
                bool isScriptReady = false;
                for (int i = 0; i < 30; i++)
                {
                    if (PdfPreviewControl?.CoreWebView2 == null) break;
                    string checkScript = await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync("typeof loadPdfFromBase64 === 'function'");
                    if (checkScript == "true")
                    {
                        isScriptReady = true;
                        break;
                    }
                    await Task.Delay(100);
                }

                if (!isScriptReady || PdfPreviewControl?.CoreWebView2 == null)
                {
                    RollbackViewerUiState("Lỗi: Trình xem PDF không tải được engine hiển thị.");
                    return;
                }

                string safeJsonBase64 = System.Text.Json.JsonSerializer.Serialize(base64Pdf);
                await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync($"loadPdfFromBase64({safeJsonBase64});");

                // Chỉ kích hoạt UI thành công sau khi PDF đã được nạp hoàn tất
                PnlPreviewPlaceholder.Visibility = Visibility.Collapsed;
                _hasUserPlacedStamp = false;
                BtnInsertSignatureStamp.IsEnabled = true;
            }
            catch (ObjectDisposedException)
            {
                RollbackViewerUiState("Lỗi: Trình xem đã bị hủy trong quá trình xử lý.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi nạp PDF tương tác: {ex}");
                RollbackViewerUiState($"Lỗi tải tài liệu: {ex.Message}");
            }
        }

        private void RollbackViewerUiState(string errorMessage)
        {
            PnlPreviewPlaceholder.Visibility = Visibility.Visible;
            _hasUserPlacedStamp = false;
            BtnInsertSignatureStamp.IsEnabled = false;
            TxtCurrentFileStatus.Text = errorMessage;
            UpdateStatus(errorMessage);
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

            string dir = Path.GetDirectoryName(_currentWorkingPdfPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            try
            {
                string testFile = Path.Combine(dir, $".vgca_perm_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch
            {
                dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            }

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

                // Hiển thị Banner Thành công và các nút thao tác nhanh trên giao diện
                BorderSignSuccessBanner.Visibility = Visibility.Visible;
                TxtSignedSummaryNotice.Text = $"Tệp đã ký: {Path.GetFileName(signedPath)} • Đã nhúng chữ ký số và dấu thời gian TSA Ban Cơ yếu.";

                // Cập nhật sẵn sang Tab 3 (Upload Web)
                TxtUploadFilePath.Text = signedPath;
                TxtUploadDocTitle.Text = Path.GetFileNameWithoutExtension(signedPath);
                if (_vgcaAuth.CurrentTeacher != null)
                {
                    TxtUploadSigner.Text = _vgcaAuth.CurrentTeacher.FullName;
                    TxtUploadDepartment.Text = _vgcaAuth.CurrentTeacher.Department;
                }

                var choice = MessageBox.Show(
                    $"Ký số văn bản thành công!\n\nTệp tin đã lưu tại:\n{signedPath}\n\nChữ ký số đã được nhúng chính xác tại vị trí bạn chọn với dấu thời gian TSA Ban Cơ yếu.\nNội dung văn bản đã ký được hiển thị trực tiếp trên màn hình.\n\nBạn có muốn chuyển sang Tab 3 để tải tệp lên Cổng Web ngay không?",
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

        private void BtnSaveSignedCopy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastSignedPdfPath) || !File.Exists(_lastSignedPdfPath))
            {
                MessageBox.Show("Chưa có tệp tin văn bản đã ký để lưu.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sfd = new SaveFileDialog
            {
                Title = "Lưu bản sao tài liệu đã ký số",
                FileName = Path.GetFileName(_lastSignedPdfPath),
                Filter = "Tài liệu PDF (*.pdf)|*.pdf|Tất cả tệp (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    File.Copy(_lastSignedPdfPath, sfd.FileName, true);
                    UpdateStatus($"Đã lưu bản sao tệp đã ký tại: {sfd.FileName}");
                    MessageBox.Show($"Đã lưu bản sao thành công tại:\n{sfd.FileName}", "Lưu Bản Sao", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi khi lưu tệp:\n{ex.Message}", "Lỗi Lưu Tệp", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnOpenSignedPdfExternal_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastSignedPdfPath) || !File.Exists(_lastSignedPdfPath))
            {
                MessageBox.Show("Chưa có tệp tin văn bản đã ký để mở.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _lastSignedPdfPath,
                    UseShellExecute = true
                });
                UpdateStatus($"Đã mở tệp ngoài hệ thống: {Path.GetFileName(_lastSignedPdfPath)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không thể mở tệp:\n{ex.Message}", "Lỗi Mở Tệp", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenSignedFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastSignedPdfPath) || !File.Exists(_lastSignedPdfPath))
            {
                MessageBox.Show("Chưa có tệp tin văn bản đã ký.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_lastSignedPdfPath}\"");
                UpdateStatus("Đã mở thư mục chứa tệp tin đã ký.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không thể mở thư mục:\n{ex.Message}", "Lỗi Mở Thư Mục", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnGoToUploadWeb_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 2; // Chuyển sang Tab 3
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