using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        private readonly ExcelConverterService _excelConverter = new();
        private readonly CertificateService _certService = new();
        private readonly PdfSigningService _pdfSigner = new();
        private readonly PdfVerifierService _pdfVerifier = new();
        private readonly ProfileManager _profileManager = new();
        private readonly WebUploadService _webUploader = new();
        private readonly VgcaAuthService _vgcaAuth = new();
        private readonly ReportWorkflowService _workflowService = new();
        private string? _activeWorkflowReportId;
        private string? _activeWorkflowExpectedBaseHash;
        private bool _isOutboxViewMode;
        private List<ReportModel> _allReports = new();

        private List<CertificateModel> _certificates = new();
        private List<SignatureProfile> _profiles = new();
        private CertificateModel? _currentActiveCert;

        private string? _originalWordFilePath;
        private bool _isWordModified = false;
        private string? _originalExcelFilePath;
        private string _currentExcelOrientation = "landscape";
        private string? _currentWorkingPdfPath;
        private string? _lastSignedPdfPath;
        private bool _isWebViewInitialized;
        private bool _isBusyProcessing;

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
            WindowState = WindowState.Maximized; // Khởi động mặc định phóng to toàn màn hình (Maximum)
            Loaded += MainWindow_Loaded;

            _wordConverter.StatusChanged += UpdateStatus;
            _excelConverter.StatusChanged += UpdateStatus;
            _pdfSigner.StatusChanged += UpdateStatus;
            _webUploader.StatusChanged += UpdateStatus;
            _vgcaAuth.LoginStateChanged += OnVgcaLoginStateChanged;
            Activated += MainWindow_Activated;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                WindowState = WindowState.Maximized; // Đảm bảo luôn toàn màn hình trên mọi độ phân giải máy tính
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
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(jsonString);
                System.Text.Json.JsonElement root = doc.RootElement;
                if (root.TryGetProperty("type", out System.Text.Json.JsonElement typeProp))
                {
                    string? msgType = typeProp.GetString();
                    if (msgType == "stampCoordinatesChanged")
                    {
                        if (root.TryGetProperty("coords", out System.Text.Json.JsonElement coords) &&
                            coords.TryGetProperty("pageNumber", out System.Text.Json.JsonElement pn) && pn.TryGetInt32(out int pageNum) &&
                            coords.TryGetProperty("x", out System.Text.Json.JsonElement cx) && cx.TryGetDouble(out double xVal) &&
                            coords.TryGetProperty("y", out System.Text.Json.JsonElement cy) && cy.TryGetDouble(out double yVal) &&
                            coords.TryGetProperty("width", out System.Text.Json.JsonElement cw) && cw.TryGetDouble(out double wVal) &&
                            coords.TryGetProperty("height", out System.Text.Json.JsonElement ch) && ch.TryGetDouble(out double hVal))
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
                        if (root.TryGetProperty("totalPages", out System.Text.Json.JsonElement tp) && tp.TryGetInt32(out int totalPages))
                        {
                            UpdateStatus($"Đã nạp xong tài liệu PDF tương tác ({totalPages} trang).");
                        }
                        BtnInsertSignatureStamp.IsEnabled = true;
                    }
                    else if (msgType == "excelLoaded")
                    {
                        int sheetCount = 1;
                        if (root.TryGetProperty("sheetCount", out System.Text.Json.JsonElement scProp) && scProp.TryGetInt32(out int sc))
                        {
                            sheetCount = sc;
                        }
                        TxtCurrentFileStatus.Text = $"✅ Bảng tính Excel ({sheetCount} sheet). Click đúp vào ô để sửa dữ liệu, chọn khổ in và bấm 'Chuyển Qua PDF Để Ký'.";
                        UpdateStatus($"Đã nạp thành công bảng tính Excel ({sheetCount} sheet).");
                    }
                    else if (msgType == "excelOrientationChanged")
                    {
                        if (root.TryGetProperty("orientation", out System.Text.Json.JsonElement orientElem))
                        {
                            _currentExcelOrientation = orientElem.GetString() ?? "landscape";
                            UpdateStatus($"Đã đổi định dạng in bảng tính sang khổ {(_currentExcelOrientation == "portrait" ? "Dọc A4" : "Ngang A4")}.");
                        }
                    }
                    else if (msgType == "requestExportExcelPdf")
                    {
                        Dispatcher.Invoke(async () =>
                        {
                            await ConvertExcelToPdfAsync();
                        });
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
                    TxtQuickCertNotice.Text = $"Chứng thư: {teacher.FullName} (Ban Cơ yếu G2)";

                    // Đồng bộ thông tin sang Hộp Thư Trình Ký (Tab 3)
                    TxtWorkflowUserGreeting.Text = $"Giáo viên: {teacher.FullName} ({teacher.Title})";
                    TxtWorkflowUserUnit.Text = !string.IsNullOrWhiteSpace(teacher.Department)
                        ? $"{teacher.Department} - {teacher.Organization ?? "THCS Chu Văn An"}"
                        : teacher.Organization ?? "Trường THCS Chu Văn An";
                    PopulateWorkflowApprovers(teacher.Cccd);
                    _ = RefreshWorkflowDataAsync();

                    // Mở khóa Tab 2
                    TabItemSignWorkspace.Header = "  2. Soạn Thảo, Xem & Ký Số Trực Tiếp  ";
                    TabItemSignWorkspace.IsEnabled = true;
                    OverlayTab2Locked.Visibility = Visibility.Collapsed;
                    if (PdfPreviewControl != null)
                    {
                        PdfPreviewControl.Visibility = Visibility.Visible;
                    }

                    // Đồng bộ chứng thư trong kho Windows Store nếu có
                    if (_certificates != null && _certificates.Count > 0)
                    {
                        CertificateModel? matchedCert = _certificates.FirstOrDefault(c =>
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

                    string maskedCccd = teacher.Cccd?.Length > 4 
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

                    // Xóa dữ liệu Hộp thư Tab 3
                    TxtWorkflowUserGreeting.Text = "Giáo viên: Chưa đăng nhập";
                    TxtWorkflowUserUnit.Text = "Trường THCS Chu Văn An";
                    CboWorkflowApprover.ItemsSource = null;
                    LvInboxReports.ItemsSource = null;
                    LvOutboxReports.ItemsSource = null;
                    LvArchiveReports.ItemsSource = null;
                    TxtCountInbox.Text = "0";
                    TxtCountOutbox.Text = "0";
                    TxtCountCompleted.Text = "0";

                    // Khóa Tab 2
                    TabItemSignWorkspace.Header = "  2. Soạn Thảo, Xem & Ký Số Trực Tiếp ( 🔒 Khóa )  ";
                    TabItemSignWorkspace.IsEnabled = false;
                    OverlayTab2Locked.Visibility = Visibility.Visible;
                    if (PdfPreviewControl != null)
                    {
                        PdfPreviewControl.Visibility = Visibility.Hidden;
                    }
                    if (MainTabControl.SelectedIndex == 1)
                    {
                        MainTabControl.SelectedIndex = 0;
                    }

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
            VgcaLoginResult result = _vgcaAuth.AuthenticatePassword(cccd, password);

            if (result.Success && result.Teacher != null)
            {
                BorderVgcaAuthError.Visibility = Visibility.Collapsed;
                MessageBox.Show(
                    $"Đăng nhập thành công tài khoản VGCA Virtual CSP!\n\nCán bộ: {result.Teacher.FullName}\nĐơn vị: {result.Teacher.Department}\nSố CCCD: {result.Teacher.Cccd}\n\nKhu vực Soạn thảo & Ký số (Tab 2) đã được mở khóa sẵn sàng.",
                    "Xác Thực Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // Kiểm tra xem trên máy tính đã có Chứng thư số Ban Cơ yếu Chính phủ đang hoạt động hay không
                CertificateModel? activeCert = _certService.GetActiveVGCACertificate();
                if (activeCert != null && activeCert.HasPrivateKey)
                {
                    VgcaLoginResult hwResult = _vgcaAuth.LoginWithHardwareCertificate(activeCert);
                    if (hwResult.Success && hwResult.Teacher != null)
                    {
                        if (string.Equals(hwResult.Teacher.Cccd, cccd, StringComparison.OrdinalIgnoreCase))
                        {
                            _vgcaAuth.UnlockAccount(cccd);
                        }

                        BorderVgcaAuthError.Visibility = Visibility.Collapsed;
                        MessageBox.Show(
                            $"Hệ thống đã tự động nhận diện và xác thực thành công qua Chứng thư số Ban Cơ yếu Chính phủ trên máy tính!\n\n" +
                            $"• Cán bộ: {hwResult.Teacher.FullName}\n" +
                            $"• Đơn vị: {hwResult.Teacher.Department}\n" +
                            $"• Số CCCD: {hwResult.Teacher.Cccd}\n\n" +
                            "Tài khoản đã được mở khóa an toàn và Phân hệ Soạn thảo & Ký số (Tab 2) đã sẵn sàng hoạt động.",
                            "Tự Động Mở Khóa & Đăng Nhập", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                }

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

        private async void BtnVgcaLogout_Click(object sender, RoutedEventArgs e)
        {
            var choice = MessageBox.Show(
                "Bạn có chắc chắn muốn đăng xuất tài khoản chữ ký số VGCA không?\n(Sau khi đăng xuất, tài liệu đang mở sẽ được đóng lại an toàn để bảo mật phiên làm việc)",
                "Xác Nhận Đăng Xuất", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (choice == MessageBoxResult.Yes)
            {
                if (_vgcaAuth == null)
                {
                    UpdateStatus("Dịch vụ xác thực chưa sẵn sàng.");
                    return;
                }

                // Dọn dẹp an toàn phiên làm việc của người dùng hiện tại (chống lộ dữ liệu giữa các giáo viên dùng chung máy)
                await ConfirmAndCloseCurrentDocumentAsync(isManualClose: true);
                _activeWorkflowReportId = null;
                _activeWorkflowExpectedBaseHash = null;
                _currentActiveCert = null;
                BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                BtnSubmitWorkflowReport.Visibility = Visibility.Visible;

                _vgcaAuth.Logout();
                GenerateAndDisplayVgcaQr();
                UpdateStatus("Đã đăng xuất tài khoản VGCA Virtual CSP an toàn.");
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
                    UpdateStatus("Không tìm thấy chứng thư số nào có khóa riêng trong hệ thống.");
                    OnVgcaLoginStateChanged(false, null);
                    return;
                }

                // Tìm chứng thư VGCA chuyên dùng của Ban Cơ yếu Chính phủ
                CertificateModel? vgcaCert = _certService.GetActiveVGCACertificate();

                if (vgcaCert != null)
                {
                    _currentActiveCert = vgcaCert;
                    CmbCertificates.SelectedItem = vgcaCert;

                    TxtDetailIssuer.Text = vgcaCert.Issuer ?? "";
                    TxtDetailSerial.Text = vgcaCert.SerialNumber ?? "";
                    TxtDetailValidity.Text = $"{vgcaCert.NotBefore:dd/MM/yyyy} đến {vgcaCert.NotAfter:dd/MM/yyyy} ({(vgcaCert.IsValidNow ? "Còn hiệu lực" : "Đã hết hạn")})";
                    TxtDetailAlgorithm.Text = vgcaCert.KeyAlgorithm ?? "";
                    TxtDetailThumbprint.Text = vgcaCert.Thumbprint ?? "";

                    // TỰ ĐỘNG XÁC THỰC KHI ĐÃ CÓ CHỨNG THƯ BAN CƠ YẾU CHÍNH PHỦ TRÊN MÁY TÍNH
                    VgcaLoginResult autoLoginResult = _vgcaAuth.LoginWithHardwareCertificate(vgcaCert);
                    if (autoLoginResult.Success && autoLoginResult.Teacher != null)
                    {
                        BorderVgcaAuthError.Visibility = Visibility.Collapsed;
                        OnVgcaLoginStateChanged(true, autoLoginResult.Teacher);
                        UpdateStatus($"Đã tự động kết nối chứng thư số Ban Cơ yếu: {vgcaCert.CommonName}. Khu vực Soạn thảo - Ký số đã sẵn sàng!");
                    }
                    else
                    {
                        if (_vgcaAuth.IsLoggedIn && _vgcaAuth.CurrentTeacher != null)
                        {
                            OnVgcaLoginStateChanged(true, _vgcaAuth.CurrentTeacher);
                        }
                        else
                        {
                            OnVgcaLoginStateChanged(false, null);
                        }
                        UpdateStatus($"Đã phát hiện chứng thư Ban Cơ yếu: {vgcaCert.CommonName ?? "VGCA"} (Serial: {vgcaCert.SerialNumber ?? "N/A"}).");
                    }
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

                    UpdateStatus("Cảnh báo: Chưa phát hiện tài khoản chữ ký số Ban Cơ yếu Chính phủ. Vui lòng mở ứng dụng VGCA Virtual CSP để đăng nhập.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi kiểm tra chứng thư số: {ex}");
                UpdateStatus($"Lỗi kiểm tra chứng thư số: {ex.Message}");
            }
        }

        private void MainWindow_Activated(object? sender, EventArgs e)
        {
            try
            {
                // Tự động quét lại và nhận diện nếu hiện tại chưa có phiên đăng nhập
                if (!_vgcaAuth.IsLoggedIn)
                {
                    LoadCertificatesAndVerifyVGCALogin();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Lỗi kiểm tra chứng thư khi kích hoạt cửa sổ: {ex.Message}");
            }
        }

        private void BtnSyncVgcaCertificate_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatus("Đang đồng bộ và nhận diện chữ ký số từ hệ thống VGCA Virtual CSP...");
            LoadCertificatesAndVerifyVGCALogin();

            if (_currentActiveCert != null && _currentActiveCert.IsVGCACertificate)
            {
                BorderVgcaAuthError.Visibility = Visibility.Collapsed;
                MessageBox.Show(
                    $"Đã nhận diện thành công Chứng thư số Ban Cơ yếu Chính phủ!\n\n" +
                    $"• Chủ sở hữu: {_currentActiveCert.CommonName}\n" +
                    $"• Đơn vị: {_currentActiveCert.OrganizationUnit}\n" +
                    $"• Cơ quan cấp: {_currentActiveCert.IssuerCommonName}\n" +
                    $"• Thời hạn: {_currentActiveCert.NotAfter:dd/MM/yyyy} ({(_currentActiveCert.IsValidNow ? "Còn hiệu lực" : "Đã hết hạn")})\n\n" +
                    "Hệ thống đã tự động kích hoạt phiên làm việc và mở khóa toàn bộ Phân hệ Soạn thảo & Ký số (Tab 2).",
                    "Đồng Bộ Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                BorderVgcaAuthError.Visibility = Visibility.Visible;
                TxtVgcaAuthError.Text = "Chưa phát hiện chứng thư Ban Cơ yếu Chính phủ có khóa riêng trong hệ thống. Vui lòng mở ứng dụng VGCA Virtual CSP trên máy để đăng nhập trước, sau đó bấm nút này lần nữa.";
                UpdateStatus("Chưa tìm thấy chứng thư Ban Cơ yếu hợp lệ trong kho Windows.");
            }
        }

        private void BtnUnlockAccount_Click(object sender, RoutedEventArgs e)
        {
            string cccd = TxtVgcaCccd.Text.Trim();
            if (string.IsNullOrWhiteSpace(cccd))
            {
                MessageBox.Show("Vui lòng nhập số CCCD của tài khoản cần mở khóa vào ô 'Số CCCD / Mã Định Danh'.", "Yêu Cầu Nhập CCCD", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtVgcaCccd.Focus();
                return;
            }

            bool unlocked = _vgcaAuth.UnlockAccount(cccd);
            if (unlocked)
            {
                BorderVgcaAuthError.Visibility = Visibility.Collapsed;
                MessageBox.Show(
                    $"Tài khoản CCCD {cccd} đã được mở khóa an toàn thành công!\n\nBạn có thể:\n1. Bấm '🔄 ĐỒNG BỘ & NHẬN DIỆN CHỨNG THƯ' để đăng nhập tự động qua chứng thư Ban Cơ yếu trên máy.\n2. Hoặc nhập mật khẩu để đăng nhập tài khoản.",
                    "Đã Mở Khóa Tài Khoản", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateStatus($"Đã mở khóa an toàn cho tài khoản {cccd}.");
            }
            else
            {
                MessageBox.Show("Không tìm thấy tài khoản cần mở khóa trong danh bạ.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            if (!_vgcaAuth.IsLoggedIn)
            {
                MessageBox.Show("Vui lòng đăng nhập tài khoản chữ ký số VGCA tại Tab 1 trước khi chuyển sang không gian soạn thảo & ký số.", 
                                "Yêu Cầu Đăng Nhập VGCA", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Chuyển sang Tab 2 (Soạn thảo & Ký Văn Bản)
            MainTabControl.SelectedIndex = 1;
        }

        #endregion

        #region TAB 2: SOẠN THẢO, XEM & KÝ TRỰC TIẾP TRONG GIAO DIỆN

        #region QUY TRÌNH QUẢN LÝ TỆP & CHỐNG TREO MÁY (ANTI-FREEZE & WORKFLOW SAFEGUARD)

        /// <summary>
        /// Kiểm tra xem hiện tại ứng dụng có đang mở tệp Word, Excel hoặc PDF nào không.
        /// </summary>
        private bool HasActiveDocument()
        {
            return (!string.IsNullOrWhiteSpace(_originalWordFilePath) && File.Exists(_originalWordFilePath))
                || (!string.IsNullOrWhiteSpace(_originalExcelFilePath) && File.Exists(_originalExcelFilePath))
                || (!string.IsNullOrWhiteSpace(_currentWorkingPdfPath) && File.Exists(_currentWorkingPdfPath));
        }

        /// <summary>
        /// Lấy mô tả chi tiết tên và loại tệp đang mở để hiển thị hộp thoại xác nhận.
        /// </summary>
        private string GetActiveDocumentDescription()
        {
            if (!string.IsNullOrWhiteSpace(_originalWordFilePath))
            {
                return $"văn bản Word: \"{Path.GetFileName(_originalWordFilePath)}\"";
            }
            if (!string.IsNullOrWhiteSpace(_originalExcelFilePath))
            {
                return $"bảng tính Excel: \"{Path.GetFileName(_originalExcelFilePath)}\"";
            }
            if (!string.IsNullOrWhiteSpace(_currentWorkingPdfPath))
            {
                return $"tài liệu PDF: \"{Path.GetFileName(_currentWorkingPdfPath)}\"";
            }
            return "tài liệu hiện tại";
        }

        /// <summary>
        /// Hiển thị hộp thoại xác nhận đóng tệp đang mở trước khi nạp tệp mới (ngăn ngừa mất dữ liệu và chống treo máy).
        /// Trả về true nếu người dùng đồng ý đóng tệp (hoặc không có tệp nào đang mở).
        /// Trả về false nếu người dùng chọn Cancel/No (giữ nguyên tệp hiện tại).
        /// </summary>
        private async Task<bool> ConfirmAndCloseCurrentDocumentAsync(string targetActionDescription = "mở tệp mới", bool isManualClose = false)
        {
            if (!HasActiveDocument())
            {
                return true;
            }

            string activeDocDesc = GetActiveDocumentDescription();
            string message;
            string title;

            if (isManualClose)
            {
                title = "Xác Nhận Đóng Tệp Đang Mở";
                message = $"Bạn có chắc chắn muốn đóng {activeDocDesc} không?\n\n" +
                          "• Mọi nội dung chỉnh sửa chưa chuyển đổi hoặc vị trí con dấu chữ ký chưa ký sẽ bị hủy.\n" +
                          "• Hệ thống sẽ giải phóng tài nguyên bộ nhớ và đưa giao diện về trạng thái ban đầu.";
            }
            else
            {
                title = "Xác Nhận Đóng Tệp Hiện Tại";
                message = $"Hệ thống phát hiện bạn đang mở {activeDocDesc}.\n\n" +
                          $"Bạn có chắc chắn muốn đóng tệp hiện tại để {targetActionDescription} không?\n\n" +
                          "➡ Chọn [Yes / Có]: Đóng tệp đang mở an toàn và tiếp tục tải tệp mới.\n" +
                          "➡ Chọn [No / Không]: Giữ nguyên tệp hiện tại, hủy thao tác mở tệp mới.";
            }

            MessageBoxResult result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                return false;
            }

            await CloseCurrentDocumentAsync();
            return true;
        }

        /// <summary>
        /// Đóng an toàn tài liệu đang mở, reset toàn bộ trạng thái và giải phóng tài nguyên WebView2 / COM (Chống treo máy).
        /// </summary>
        private async Task CloseCurrentDocumentAsync()
        {
            try
            {
                // 1. Reset các biến đường dẫn tài liệu
                _originalWordFilePath = null;
                _isWordModified = false;
                _originalExcelFilePath = null;
                _currentWorkingPdfPath = null;

                // 2. Ẩn và dọn dẹp trình soạn thảo Word nếu đang mở
                if (WordEditorControl != null)
                {
                    WordEditorControl.Visibility = Visibility.Collapsed;
                    WordEditorControl.Document = null;
                }
                if (PdfPreviewControl != null)
                {
                    PdfPreviewControl.Visibility = Visibility.Visible;
                }

                // 3. Reset trạng thái con dấu chữ ký
                _hasUserPlacedStamp = false;
                _userStampPage = 1;

                // 3. Reset giao diện thanh công cụ
                TxtCurrentFileName.Text = "Chưa chọn tệp tin nào.";
                TxtCurrentFileStatus.Text = "Chọn file Word, Excel hoặc PDF để xem trực tiếp tại đây.";
                PnlEditingToolbar.Visibility = Visibility.Collapsed;
                PnlExcelToolbarControls.Visibility = Visibility.Collapsed;
                BorderSignSuccessBanner.Visibility = Visibility.Collapsed;
                BtnConvertToPdfAndReadySign.IsEnabled = false;
                BtnInsertSignatureStamp.IsEnabled = false;
                BtnSignCurrentDocument.IsEnabled = false;
                if (BtnCloseCurrentFile != null)
                {
                    BtnCloseCurrentFile.Visibility = Visibility.Collapsed;
                }

                // 4. Giải phóng tài nguyên hiển thị trong WebView2
                if (_isWebViewInitialized && PdfPreviewControl?.CoreWebView2 != null)
                {
                    try
                    {
                        await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync("if (typeof resetViewer === 'function') resetViewer();");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.TraceWarning($"Lỗi thực thi resetViewer: {ex.Message}");
                    }
                }

                PnlPreviewPlaceholder.Visibility = Visibility.Visible;

                // 5. Thu hồi rác dọn dẹp RAM
                GC.Collect();
                GC.WaitForPendingFinalizers();

                UpdateStatus("Đã đóng tệp an toàn và dọn dẹp tài nguyên hệ thống.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi khi đóng tệp: {ex}");
            }
        }

        /// <summary>
        /// Khóa/mở khóa giao diện và hiển thị con trỏ chờ khi đang xử lý tác vụ nặng (Chống bấm đúp / Chống treo giao diện).
        /// </summary>
        private void SetProcessingBusyState(bool isBusy, string? busyMessage = null)
        {
            _isBusyProcessing = isBusy;
            Mouse.OverrideCursor = isBusy ? Cursors.Wait : null;

            if (BtnSelectWord != null) BtnSelectWord.IsEnabled = !isBusy;
            if (BtnSelectExcel != null) BtnSelectExcel.IsEnabled = !isBusy;
            if (BtnSelectPdf != null) BtnSelectPdf.IsEnabled = !isBusy;
            if (BtnCloseCurrentFile != null) BtnCloseCurrentFile.IsEnabled = !isBusy;

            if (isBusy)
            {
                BtnConvertToPdfAndReadySign.IsEnabled = false;
                BtnInsertSignatureStamp.IsEnabled = false;
                BtnSignCurrentDocument.IsEnabled = false;
                if (!string.IsNullOrWhiteSpace(busyMessage))
                {
                    UpdateStatus(busyMessage);
                }
            }
        }

        private async void BtnCloseCurrentFile_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusyProcessing)
            {
                return;
            }
            await ConfirmAndCloseCurrentDocumentAsync(isManualClose: true);
        }

        #endregion

        private async void BtnSelectWord_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusyProcessing)
            {
                MessageBox.Show("Hệ thống đang xử lý tác vụ khác, vui lòng chờ trong giây lát...",
                    "Hệ Thống Đang Bận", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Kiểm tra và hỏi đóng tệp đang mở (chống xung đột dữ liệu và treo máy)
            bool canProceed = await ConfirmAndCloseCurrentDocumentAsync("mở tệp Word mới");
            if (!canProceed)
            {
                // Người dùng chọn Cancel/No: Giữ nguyên trạng thái hiện tại
                return;
            }

            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Tải tệp Word (.docx, .doc) lên để xem & điều chỉnh trực tiếp",
                Filter = "Tài liệu Word (*.docx;*.doc)|*.docx;*.doc|Tất cả tệp (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                await LoadWordDocumentAsync(dialog.FileName);
            }
        }

        private async Task LoadWordDocumentAsync(string wordPath)
        {
            if (string.IsNullOrWhiteSpace(wordPath) || !File.Exists(wordPath))
            {
                MessageBox.Show("Tệp Word không tồn tại hoặc đường dẫn không hợp lệ.", "Lỗi Tệp", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetProcessingBusyState(true, $"Đang nạp cấu trúc tài liệu Word: {Path.GetFileName(wordPath)}...");

            _originalWordFilePath = wordPath;
            _originalExcelFilePath = null;
            _currentWorkingPdfPath = null;
            _isWordModified = false;
            BorderSignSuccessBanner.Visibility = Visibility.Collapsed;

            TxtCurrentFileName.Text = Path.GetFileName(wordPath);
            PnlEditingToolbar.Visibility = Visibility.Visible;
            PnlExcelToolbarControls.Visibility = Visibility.Collapsed;
            TxtEditorModeBadge.Text = "✏ Chế độ xem & chỉnh sửa Word (A4 Paged View)";
            BtnConvertToPdfAndReadySign.IsEnabled = false;
            BtnInsertSignatureStamp.IsEnabled = false;
            BtnSignCurrentDocument.IsEnabled = false;
            BtnCloseCurrentFile.Visibility = Visibility.Visible;

            try
            {
                long fileSizeKb = new FileInfo(wordPath).Length / 1024;
                TxtCurrentFileStatus.Text = $"Đang tải cấu trúc tài liệu ({fileSizeKb:N0} KB)...";

                // Nạp trực tiếp vào điều khiển Native A4 Paged Word Editor của Syncfusion (hoàn toàn không cần Microsoft Word)
                var (docStream, formatType) = await Task.Run(() => _wordConverter.LoadWordToEditorStream(wordPath));
                using (docStream)
                {
                    WordEditorControl.Load(docStream, formatType);
                }

                WordEditorControl.LayoutType = Syncfusion.Windows.Controls.RichTextBoxAdv.LayoutType.Pages;

                PnlPreviewPlaceholder.Visibility = Visibility.Collapsed;
                PdfPreviewControl.Visibility = Visibility.Collapsed;
                WordEditorControl.Visibility = Visibility.Visible;

                _isWordModified = false;
                WordEditorControl.ContentChanged -= WordEditorControl_ContentChanged;
                WordEditorControl.ContentChanged += WordEditorControl_ContentChanged;

                TxtCurrentFileStatus.Text = "Đang ở chế độ xem & điều chỉnh trực tiếp (khổ A4). Bạn có thể sửa văn bản ngay bên dưới, sau đó bấm 'Chuyển Qua PDF Để Ký'.";
                BtnConvertToPdfAndReadySign.IsEnabled = true;
                UpdateStatus($"Đã nạp văn bản Word: {Path.GetFileName(wordPath)}. Sẵn sàng xem và điều chỉnh.");
            }
            catch (Exception ex)
            {
                TxtCurrentFileStatus.Text = "Nạp tài liệu thất bại.";
                _originalWordFilePath = null;
                _isWordModified = false;
                WordEditorControl.Visibility = Visibility.Collapsed;
                PdfPreviewControl.Visibility = Visibility.Visible;
                BtnCloseCurrentFile.Visibility = Visibility.Collapsed;
                System.Diagnostics.Trace.TraceError($"Lỗi nạp tài liệu Word: {ex}");
                MessageBox.Show($"Không thể hiển thị tài liệu Word:\n{ex.Message}", "Lỗi Nạp Tài Liệu", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetProcessingBusyState(false);
            }
        }

        private void WordEditorControl_ContentChanged(object? sender, EventArgs e)
        {
            _isWordModified = true;
        }

        private async void BtnSelectExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusyProcessing)
            {
                MessageBox.Show("Hệ thống đang xử lý tác vụ khác, vui lòng chờ trong giây lát...",
                    "Hệ Thống Đang Bận", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Kiểm tra và hỏi đóng tệp đang mở (chống xung đột dữ liệu và treo máy)
            bool canProceed = await ConfirmAndCloseCurrentDocumentAsync("mở bảng tính Excel mới");
            if (!canProceed)
            {
                // Người dùng chọn Cancel/No: Giữ nguyên trạng thái hiện tại
                return;
            }

            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Tải bảng tính Excel (.xlsx, .xls) lên để xem & điều chỉnh trực tiếp",
                Filter = "Bảng tính Excel (*.xlsx;*.xls)|*.xlsx;*.xls|Tất cả tệp (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                await LoadExcelDocumentAsync(dialog.FileName);
            }
        }

        private async Task LoadExcelDocumentAsync(string excelPath)
        {
            if (string.IsNullOrWhiteSpace(excelPath) || !File.Exists(excelPath))
            {
                MessageBox.Show("Tệp Excel không tồn tại hoặc đường dẫn không hợp lệ.", "Lỗi Tệp", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetProcessingBusyState(true, $"Đang nạp bảng tính Excel: {Path.GetFileName(excelPath)}...");

            _originalExcelFilePath = excelPath;
            _originalWordFilePath = null;
            _currentWorkingPdfPath = null;
            _isWordModified = false;
            BorderSignSuccessBanner.Visibility = Visibility.Collapsed;

            if (WordEditorControl != null) WordEditorControl.Visibility = Visibility.Collapsed;
            if (PdfPreviewControl != null) PdfPreviewControl.Visibility = Visibility.Visible;

            TxtCurrentFileName.Text = Path.GetFileName(excelPath);
            TxtCurrentFileStatus.Text = "Đang phân tích cấu trúc bảng tính Excel...";
            PnlEditingToolbar.Visibility = Visibility.Visible;
            PnlExcelToolbarControls.Visibility = Visibility.Visible;
            TxtEditorModeBadge.Text = "📊 Chế độ xem & chỉnh sửa Bảng tính Excel";
            BtnConvertToPdfAndReadySign.IsEnabled = false;
            BtnInsertSignatureStamp.IsEnabled = false;
            BtnSignCurrentDocument.IsEnabled = false;
            BtnCloseCurrentFile.Visibility = Visibility.Visible;

            try
            {
                string base64 = await _excelConverter.ReadExcelAsBase64Async(excelPath);

                if (_isWebViewInitialized && PdfPreviewControl?.CoreWebView2 != null)
                {
                    PnlPreviewPlaceholder.Visibility = Visibility.Collapsed;

                    // Đảm bảo pdf_viewer.html đã được nạp
                    string viewerUrl = PdfPreviewControl.Source?.ToString() ?? "";
                    if (!viewerUrl.Contains("pdf_viewer.html", StringComparison.OrdinalIgnoreCase))
                    {
                        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
                        EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs>? onNavCompleted = null;
                        onNavCompleted = (s, args) =>
                        {
                            try { PdfPreviewControl.NavigationCompleted -= onNavCompleted; }
                            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Lỗi gỡ NavigationCompleted handler: {ex.Message}"); }
                            tcs.TrySetResult(args.IsSuccess);
                        };

                        PdfPreviewControl.NavigationCompleted += onNavCompleted;
                        try
                        {
                            PdfPreviewControl.CoreWebView2.Navigate("https://vgcaviewer.local/pdf_viewer.html");
                            Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                            if (completedTask == tcs.Task)
                            {
                                await tcs.Task;
                            }
                        }
                        finally
                        {
                            try { PdfPreviewControl.NavigationCompleted -= onNavCompleted; }
                            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Lỗi gỡ NavigationCompleted handler: {ex.Message}"); }
                        }
                    }

                    // Đảm bảo script loadExcelFromBase64 đã sẵn sàng trong DOM
                    for (int i = 0; i < 30; i++)
                    {
                        if (PdfPreviewControl?.CoreWebView2 == null) break;
                        string checkScript = await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync("typeof loadExcelFromBase64 === 'function'");
                        if (checkScript == "true") break;
                        await Task.Delay(100);
                    }

                    // Mã hóa an toàn tham số bằng JsonSerializer để triệt tiêu hoàn toàn lỗ hổng JS Injection
                    string jsonBase64 = System.Text.Json.JsonSerializer.Serialize(base64);
                    string jsonFileName = System.Text.Json.JsonSerializer.Serialize(Path.GetFileName(excelPath));
                    string script = $"loadExcelFromBase64({jsonBase64}, {jsonFileName});";
                    if (PdfPreviewControl?.CoreWebView2 != null)
                    {
                        await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync(script);
                    }
                }

                TxtCurrentFileStatus.Text = "Đang ở chế độ xem & điều chỉnh bảng tính. Bạn có thể click đúp vào ô để sửa dữ liệu, sau đó bấm 'Chuyển Qua PDF Để Ký'.";
                BtnConvertToPdfAndReadySign.IsEnabled = true;
                UpdateStatus($"Đã nạp bảng tính Excel: {Path.GetFileName(excelPath)}. Sẵn sàng xem và điều chỉnh.");
            }
            catch (Exception ex)
            {
                TxtCurrentFileStatus.Text = "Nạp bảng tính thất bại.";
                _originalExcelFilePath = null;
                BtnCloseCurrentFile.Visibility = Visibility.Collapsed;
                System.Diagnostics.Trace.TraceError($"Lỗi nạp bảng tính Excel: {ex}");
                MessageBox.Show($"Không thể hiển thị bảng tính Excel:\n{ex.Message}", "Lỗi Nạp Bảng Tính", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetProcessingBusyState(false);
            }
        }

        private async void BtnExcelLandscape_Click(object sender, RoutedEventArgs e)
        {
            _currentExcelOrientation = "landscape";
            if (PdfPreviewControl?.CoreWebView2 != null)
            {
                await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync("setExcelOrientation('landscape');");
                UpdateStatus("Đã đổi định dạng trang in bảng tính sang Khổ Ngang A4.");
            }
        }

        private async void BtnExcelPortrait_Click(object sender, RoutedEventArgs e)
        {
            _currentExcelOrientation = "portrait";
            if (PdfPreviewControl?.CoreWebView2 != null)
            {
                await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync("setExcelOrientation('portrait');");
                UpdateStatus("Đã đổi định dạng trang in bảng tính sang Khổ Dọc A4.");
            }
        }

        private async void BtnSelectPdf_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusyProcessing)
            {
                MessageBox.Show("Hệ thống đang xử lý tác vụ khác, vui lòng chờ trong giây lát...",
                    "Hệ Thống Đang Bận", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Kiểm tra và hỏi đóng tệp đang mở (chống xung đột dữ liệu và treo máy)
            bool canProceed = await ConfirmAndCloseCurrentDocumentAsync("mở tệp PDF mới");
            if (!canProceed)
            {
                // Người dùng chọn Cancel/No: Giữ nguyên trạng thái hiện tại
                return;
            }

            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Tải tệp tin PDF lên để ký số",
                Filter = "Tài liệu PDF (*.pdf)|*.pdf|Tất cả tệp (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                await LoadPdfDocumentAsync(dialog.FileName);
            }
        }

        private async Task LoadPdfDocumentAsync(string pdfPath)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                MessageBox.Show("Tệp PDF không hợp lệ hoặc không tồn tại.", "Lỗi Tệp", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Tự động làm sạch hoàn toàn watermark bản quyền dùng thử nếu tệp chứa vết Syncfusion
            WordConverterService.SanitizeSyncfusionWatermark(pdfPath);

            SetProcessingBusyState(true, $"Đang nạp tệp PDF: {Path.GetFileName(pdfPath)}...");

            _originalWordFilePath = null;
            _originalExcelFilePath = null;
            _currentWorkingPdfPath = pdfPath;
            _isWordModified = false;
            BorderSignSuccessBanner.Visibility = Visibility.Collapsed;

            if (WordEditorControl != null) WordEditorControl.Visibility = Visibility.Collapsed;
            if (PdfPreviewControl != null) PdfPreviewControl.Visibility = Visibility.Visible;

            try
            {
                TxtCurrentFileName.Text = Path.GetFileName(pdfPath);
                long fileSizeKb = new FileInfo(pdfPath).Length / 1024;
                TxtCurrentFileStatus.Text = $"Tệp PDF sẵn sàng ký số ({fileSizeKb:N0} KB).";
                PnlEditingToolbar.Visibility = Visibility.Collapsed;
                PnlExcelToolbarControls.Visibility = Visibility.Collapsed;
                BtnConvertToPdfAndReadySign.IsEnabled = false;
                BtnSignCurrentDocument.IsEnabled = _vgcaAuth.IsLoggedIn;
                BtnInsertSignatureStamp.IsEnabled = _vgcaAuth.IsLoggedIn;
                BtnCloseCurrentFile.Visibility = Visibility.Visible;

                await DisplayPdfInInteractiveViewerAsync(pdfPath);

                UpdateStatus($"Đã nạp tệp PDF trực tiếp: {Path.GetFileName(pdfPath)}");
            }
            catch (Exception ex)
            {
                TxtCurrentFileStatus.Text = "Nạp tệp PDF thất bại.";
                _currentWorkingPdfPath = null;
                BtnCloseCurrentFile.Visibility = Visibility.Collapsed;
                System.Diagnostics.Trace.TraceError($"Lỗi nạp PDF: {ex}");
                MessageBox.Show($"Không thể hiển thị tài liệu PDF:\n{ex.Message}", "Lỗi Nạp PDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetProcessingBusyState(false);
            }
        }

        private async void BtnExecuteConvertDocument_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusyProcessing)
            {
                MessageBox.Show("Hệ thống đang xử lý tác vụ khác, vui lòng chờ trong giây lát...",
                    "Hệ Thống Đang Bận", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_originalExcelFilePath) && File.Exists(_originalExcelFilePath))
            {
                await ConvertExcelToPdfAsync();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_originalWordFilePath) && File.Exists(_originalWordFilePath))
            {
                await ConvertWordToPdfAsync();
                return;
            }

            MessageBox.Show("Vui lòng tải tệp Word hoặc Excel lên trước khi thực hiện chuyển đổi.", "Chưa Có Tệp Tin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async Task ConvertExcelToPdfAsync()
        {
            if (_isBusyProcessing)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_originalExcelFilePath) || !File.Exists(_originalExcelFilePath))
            {
                MessageBox.Show("Vui lòng tải tệp Excel lên trước khi chuyển đổi.", "Chưa có file", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (PdfPreviewControl?.CoreWebView2 == null)
            {
                MessageBox.Show("Trình xem văn bản chưa sẵn sàng.", "Lỗi Hệ Thống", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetProcessingBusyState(true, "Bắt đầu chuyển đổi bảng tính Excel sang PDF...");
            PrgConvertLoading.Visibility = Visibility.Visible;
            TxtCurrentFileStatus.Text = "Đang xuất bảng tính Excel sang PDF chuẩn vector in ấn A4...";

            try
            {
                string outputPdfPath = _excelConverter.GenerateOutputPdfPath(_originalExcelFilePath);

                Microsoft.Web.WebView2.Core.CoreWebView2PrintSettings printSettings = PdfPreviewControl.CoreWebView2.Environment.CreatePrintSettings();
                printSettings.Orientation = string.Equals(_currentExcelOrientation, "portrait", StringComparison.OrdinalIgnoreCase)
                    ? Microsoft.Web.WebView2.Core.CoreWebView2PrintOrientation.Portrait
                    : Microsoft.Web.WebView2.Core.CoreWebView2PrintOrientation.Landscape;
                printSettings.ShouldPrintBackgrounds = true;
                printSettings.ShouldPrintHeaderAndFooter = false;
                printSettings.MarginTop = 0.394;
                printSettings.MarginBottom = 0.394;
                printSettings.MarginLeft = 0.394;
                printSettings.MarginRight = 0.394;

                bool printSuccess = await PdfPreviewControl.CoreWebView2.PrintToPdfAsync(outputPdfPath, printSettings);
                if (!printSuccess || !File.Exists(outputPdfPath))
                {
                    throw new InvalidOperationException("Không thể hoàn tất quá trình xuất PDF từ bảng tính.");
                }

                _currentWorkingPdfPath = outputPdfPath;
                _originalExcelFilePath = null;
                PnlEditingToolbar.Visibility = Visibility.Collapsed;
                PnlExcelToolbarControls.Visibility = Visibility.Collapsed;

                await DisplayPdfInInteractiveViewerAsync(outputPdfPath);

                TxtCurrentFileName.Text = Path.GetFileName(outputPdfPath);
                TxtCurrentFileStatus.Text = "Tài liệu PDF bảng tính chuẩn in ấn đã sẵn sàng để ký số.";
                BtnConvertToPdfAndReadySign.IsEnabled = false;
                BtnSignCurrentDocument.IsEnabled = _vgcaAuth.IsLoggedIn;
                BtnInsertSignatureStamp.IsEnabled = _vgcaAuth.IsLoggedIn;
                BtnCloseCurrentFile.Visibility = Visibility.Visible;
                UpdateStatus($"Đã xuất tệp PDF bảng tính hoàn chỉnh: {Path.GetFileName(outputPdfPath)}");

                MessageBox.Show(
                    $"Chuyển đổi bảng tính sang PDF thành công!\n\nTệp tin đã lưu tại:\n{outputPdfPath}\n\nNội dung bảng tính chuẩn đã được hiển thị trên màn hình. Bạn có thể bấm '📍 Chèn Chữ Ký' để kéo thả con dấu đến đúng vị trí cần ký, sau đó bấm '✍ TIẾN HÀNH KÝ SỐ'.",
                    "Chuyển Đổi Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                BtnConvertToPdfAndReadySign.IsEnabled = true;
                BtnCloseCurrentFile.Visibility = Visibility.Visible;
                System.Diagnostics.Trace.TraceError($"Lỗi chuyển đổi Excel sang PDF: {ex}");
                MessageBox.Show($"Lỗi chuyển đổi bảng tính sang PDF:\n{ex.Message}", "Lỗi Chuyển Đổi", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtCurrentFileStatus.Text = "Chuyển đổi bảng tính thất bại.";
                UpdateStatus("Chuyển đổi Excel sang PDF thất bại.");
            }
            finally
            {
                PrgConvertLoading.Visibility = Visibility.Collapsed;
                SetProcessingBusyState(false);
            }
        }

        private async Task ConvertWordToPdfAsync()
        {
            if (_isBusyProcessing)
            {
                return;
            }

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

            // Kiểm tra xem người dùng có thực hiện chỉnh sửa nội dung văn bản trên WordEditorControl hay không
            bool isModified = _isWordModified;

            SetProcessingBusyState(true, "Bắt đầu chuyển đổi Word sang PDF...");
            PrgConvertLoading.Visibility = Visibility.Visible;
            TxtCurrentFileStatus.Text = "Đang trích xuất nội dung văn bản và xuất sang PDF chuẩn Nghị định 30/2020/NĐ-CP...";

            try
            {
                string pdfPath;

                // NGUYÊN TẮC BẢO VỆ TÀI LIỆU GỐC (ZERO DATA LOSS / READ-ONLY INVARIANT):
                // Tuyệt đối không bao giờ ghi đè lên tệp Word gốc trên máy người dùng.
                // Mọi nội dung chỉnh sửa trên trình soạn thảo sẽ được chuyển trực tiếp từ luồng RAM (docStream) sang PDF để phục vụ ký số.
                if (isModified)
                {
                    using MemoryStream docStream = new MemoryStream();
                    WordEditorControl.Save(docStream, Syncfusion.Windows.Controls.RichTextBoxAdv.FormatType.Docx);
                    docStream.Position = 0;
                    pdfPath = await _wordConverter.ConvertWordStreamToPdfAsync(docStream, _originalWordFilePath);
                }
                else
                {
                    // Văn bản chưa bị chỉnh sửa: Chuyển đổi trực tiếp từ file Word gốc bằng Syncfusion DocIO độc lập
                    pdfPath = await _wordConverter.ConvertWordToPdfAsync(_originalWordFilePath);
                }

                string originalWordFileName = Path.GetFileName(_originalWordFilePath);
                _currentWorkingPdfPath = pdfPath;
                _originalWordFilePath = null;
                _isWordModified = false;
                PnlEditingToolbar.Visibility = Visibility.Collapsed;

                // Chuyển chế độ hiển thị từ WordEditorControl sang WebView2 PDF Viewer
                WordEditorControl.Visibility = Visibility.Collapsed;
                PdfPreviewControl.Visibility = Visibility.Visible;

                // Nạp file PDF chuẩn vào WebView2 bằng Engine tương tác
                await DisplayPdfInInteractiveViewerAsync(pdfPath);

                TxtCurrentFileName.Text = Path.GetFileName(pdfPath);
                TxtCurrentFileStatus.Text = "Tài liệu PDF chuẩn đã được tạo và nạp sẵn sàng ký số.";
                BtnConvertToPdfAndReadySign.IsEnabled = false;
                BtnSignCurrentDocument.IsEnabled = _vgcaAuth.IsLoggedIn;
                BtnInsertSignatureStamp.IsEnabled = _vgcaAuth.IsLoggedIn;
                BtnCloseCurrentFile.Visibility = Visibility.Visible;
                UpdateStatus($"Đã xuất tệp PDF hoàn chỉnh: {Path.GetFileName(pdfPath)}");

                string saveNotice = isModified
                    ? $"• Tệp Word gốc ({originalWordFileName}) được bảo toàn nguyên vẹn 100% trên máy tính.\n• Toàn bộ nội dung đã điều chỉnh được chuyển trực tiếp vào tệp PDF phục vụ ký số.\n"
                    : $"• Tệp PDF được tạo trực tiếp từ bản Word gốc chuẩn xác 100% không xê dịch nét kẻ.\n";

                MessageBox.Show(
                    $"Chuyển đổi sang PDF thành công!\n\n" +
                    saveNotice +
                    $"• Tệp PDF chuẩn đã lưu tại:\n{pdfPath}\n\n" +
                    "Nội dung văn bản chuẩn (bao gồm toàn bộ nội dung đã chỉnh sửa) đã được hiển thị trên màn hình. Bạn có thể bấm '📍 Chèn Chữ Ký' để kéo thả con dấu đến đúng vị trí cần ký, sau đó bấm '✍ TIẾN HÀNH KÝ SỐ'.",
                    "Chuyển Đổi Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                BtnConvertToPdfAndReadySign.IsEnabled = true;
                BtnCloseCurrentFile.Visibility = Visibility.Visible;
                System.Diagnostics.Trace.TraceError($"Lỗi chuyển đổi sang PDF: {ex}");
                MessageBox.Show($"Lỗi chuyển đổi sang PDF:\n{ex.Message}", "Lỗi Chuyển Đổi", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtCurrentFileStatus.Text = "Chuyển đổi thất bại.";
                UpdateStatus("Chuyển đổi Word sang PDF thất bại.");
            }
            finally
            {
                PrgConvertLoading.Visibility = Visibility.Collapsed;
                SetProcessingBusyState(false);
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
                FileInfo fileInfo = new FileInfo(pdfPath);
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
                        try { PdfPreviewControl.NavigationCompleted -= navHandler; }
                        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Lỗi gỡ NavigationCompleted handler: {ex.Message}"); }
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
            if (_isBusyProcessing)
            {
                MessageBox.Show("Hệ thống đang xử lý tác vụ khác, vui lòng chờ trong giây lát...",
                    "Hệ Thống Đang Bận", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(_currentWorkingPdfPath) || !File.Exists(_currentWorkingPdfPath))
            {
                if (!string.IsNullOrWhiteSpace(_originalWordFilePath) || !string.IsNullOrWhiteSpace(_originalExcelFilePath))
                {
                    MessageBox.Show("Bạn đang xem văn bản / bảng tính ở chế độ soạn thảo & chỉnh sửa. Vui lòng bấm nút '⚡ Chuyển Qua PDF Để Ký' trước khi thực hiện chèn chữ ký.",
                        "Cần Chuyển Sang PDF", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Vui lòng tải tệp Word, Excel hoặc PDF lên trước khi chèn chữ ký.",
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
                OpenFileDialog ofd = new OpenFileDialog
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
            string[] dimParts = TxtDimensions.Text.Split('x', 'X');
            if (dimParts.Length == 2 && int.TryParse(dimParts[0].Trim(), out int w) && int.TryParse(dimParts[1].Trim(), out int h))
            {
                widthPx = Math.Max(60, w);
                heightPx = Math.Max(40, h);
            }

            if (PdfPreviewControl.CoreWebView2 == null)
            {
                MessageBox.Show("Trình xem văn bản PDF chưa hoàn tất tải hoặc chưa sẵn sàng. Vui lòng chờ tài liệu hiển thị đầy đủ.", "Trình Xem Chưa Sẵn Sàng", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Gọi hàm JavaScript insertStamp
            string script = $"insertStamp('{dataUrl}', {widthPx}, {heightPx});";
            await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync(script);

            _hasUserPlacedStamp = true;
            UpdateStatus("Đã chèn khung chữ ký tương tác trên trang văn bản. Hãy dùng chuột kéo thả hoặc co giãn đến vị trí mong muốn.");
        }

        private async void BtnSignDocument_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusyProcessing)
            {
                MessageBox.Show("Hệ thống đang xử lý tác vụ khác, vui lòng chờ trong giây lát...",
                    "Hệ Thống Đang Bận", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(_currentWorkingPdfPath) || !File.Exists(_currentWorkingPdfPath))
            {
                MessageBox.Show("Vui lòng tải file PDF hoặc thực hiện chuyển đổi Word/Excel sang PDF trước khi ký.",
                    "Chưa có tệp tin PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_currentActiveCert == null)
            {
                MessageBoxResult result = MessageBox.Show(
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
                string[] dimParts = TxtDimensions.Text.Split('x', 'X');
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

            SignatureProfile? selectedProfile = CmbProfiles.SelectedItem as SignatureProfile;
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

            SigningRequest request = new SigningRequest
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

            SetProcessingBusyState(true, "Đang tiến hành ký số văn bản chuẩn PAdES LTV...");

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
                BtnCloseCurrentFile.Visibility = Visibility.Visible;
                TxtSignedSummaryNotice.Text = $"Tệp đã ký: {Path.GetFileName(signedPath)} • Đã nhúng chữ ký số và dấu thời gian TSA Ban Cơ yếu.";

                // Cập nhật trạng thái các nút workflow trên banner
                if (!string.IsNullOrEmpty(_activeWorkflowReportId))
                {
                    BtnCommitWorkflowApproval.Visibility = Visibility.Visible;
                    BtnSubmitWorkflowReport.Visibility = Visibility.Collapsed;

                    MessageBoxResult choice = MessageBox.Show(
                        $"Ký số phê duyệt văn bản thành công!\n\nTệp tin đã lưu tại:\n{signedPath}\n\nBạn có muốn nộp báo cáo đã phê duyệt lên hệ thống liên thông ngay không?",
                        "Ký Duyệt Thành Công", MessageBoxButton.YesNo, MessageBoxImage.Information);

                    if (choice == MessageBoxResult.Yes)
                    {
                        BtnCommitWorkflowApproval_Click(this, new RoutedEventArgs());
                    }
                }
                else
                {
                    BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                    BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;

                    MessageBoxResult choice = MessageBox.Show(
                        $"Ký số văn bản thành công!\n\nTệp tin đã lưu tại:\n{signedPath}\n\nChữ ký số đã được nhúng chính xác với dấu thời gian TSA Ban Cơ yếu.\n\nBạn có muốn mở hộp thoại trình ký báo cáo này lên Ban Giám hiệu / Tổ trưởng ngay không?",
                        "Ký Số Thành Công", MessageBoxButton.YesNo, MessageBoxImage.Information);

                    if (choice == MessageBoxResult.Yes)
                    {
                        BtnSubmitWorkflowReport_Click(this, new RoutedEventArgs());
                    }
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
                SetProcessingBusyState(false);
                BtnSignCurrentDocument.IsEnabled = _vgcaAuth.IsLoggedIn;
                BtnInsertSignatureStamp.IsEnabled = _vgcaAuth.IsLoggedIn;
            }
        }

        private void BtnSaveSignedCopy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastSignedPdfPath) || !File.Exists(_lastSignedPdfPath))
            {
                MessageBox.Show("Chưa có tệp tin văn bản đã ký để lưu.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveFileDialog sfd = new SaveFileDialog
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

        #region Các lệnh điều chỉnh văn bản trực tiếp (SfRichTextBoxAdv hoặc ExecCommand trong WebView2)

        private void BtnFormatBold_Click(object sender, RoutedEventArgs e)
        {
            if (WordEditorControl != null && WordEditorControl.Visibility == Visibility.Visible)
            {
                if (Syncfusion.Windows.Controls.RichTextBoxAdv.SfRichTextBoxAdv.BoldCommand.CanExecute(null, WordEditorControl))
                {
                    Syncfusion.Windows.Controls.RichTextBoxAdv.SfRichTextBoxAdv.BoldCommand.Execute(null, WordEditorControl);
                }
            }
            else
            {
                PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('bold', false, null);");
            }
        }

        private void BtnFormatItalic_Click(object sender, RoutedEventArgs e)
        {
            if (WordEditorControl != null && WordEditorControl.Visibility == Visibility.Visible)
            {
                if (Syncfusion.Windows.Controls.RichTextBoxAdv.SfRichTextBoxAdv.ItalicCommand.CanExecute(null, WordEditorControl))
                {
                    Syncfusion.Windows.Controls.RichTextBoxAdv.SfRichTextBoxAdv.ItalicCommand.Execute(null, WordEditorControl);
                }
            }
            else
            {
                PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('italic', false, null);");
            }
        }

        private void BtnFormatUnderline_Click(object sender, RoutedEventArgs e)
        {
            if (WordEditorControl != null && WordEditorControl.Visibility == Visibility.Visible)
            {
                if (Syncfusion.Windows.Controls.RichTextBoxAdv.SfRichTextBoxAdv.ToggleUnderlineCommand.CanExecute(null, WordEditorControl))
                {
                    Syncfusion.Windows.Controls.RichTextBoxAdv.SfRichTextBoxAdv.ToggleUnderlineCommand.Execute(null, WordEditorControl);
                }
            }
            else
            {
                PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('underline', false, null);");
            }
        }

        private void BtnAlignLeft_Click(object sender, RoutedEventArgs e)
        {
            if (WordEditorControl != null && WordEditorControl.Visibility == Visibility.Visible)
            {
                WordEditorControl.Selection.ParagraphFormat.TextAlignment = System.Windows.TextAlignment.Left;
            }
            else
            {
                PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('justifyLeft', false, null);");
            }
        }

        private void BtnAlignCenter_Click(object sender, RoutedEventArgs e)
        {
            if (WordEditorControl != null && WordEditorControl.Visibility == Visibility.Visible)
            {
                WordEditorControl.Selection.ParagraphFormat.TextAlignment = System.Windows.TextAlignment.Center;
            }
            else
            {
                PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('justifyCenter', false, null);");
            }
        }

        private void BtnAlignRight_Click(object sender, RoutedEventArgs e)
        {
            if (WordEditorControl != null && WordEditorControl.Visibility == Visibility.Visible)
            {
                WordEditorControl.Selection.ParagraphFormat.TextAlignment = System.Windows.TextAlignment.Right;
            }
            else
            {
                PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('justifyRight', false, null);");
            }
        }

        private void BtnAlignJustify_Click(object sender, RoutedEventArgs e)
        {
            if (WordEditorControl != null && WordEditorControl.Visibility == Visibility.Visible)
            {
                WordEditorControl.Selection.ParagraphFormat.TextAlignment = System.Windows.TextAlignment.Justify;
            }
            else
            {
                PdfPreviewControl.CoreWebView2?.ExecuteScriptAsync("document.execCommand('justifyFull', false, null);");
            }
        }

        private void BtnSaveWordCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (WordEditorControl == null || WordEditorControl.Visibility != Visibility.Visible)
                {
                    MessageBox.Show("Chức năng chỉ khả dụng khi đang ở chế độ xem và chỉnh sửa văn bản Word.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string baseName = !string.IsNullOrWhiteSpace(_originalWordFilePath)
                    ? Path.GetFileNameWithoutExtension(_originalWordFilePath)
                    : "Van_ban_soan_thao";

                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Lưu bản sao Word đã chỉnh sửa thành tệp mới",
                    Filter = "Tài liệu Word (*.docx)|*.docx",
                    FileName = $"{baseName}_da_sua.docx"
                };

                if (sfd.ShowDialog() == true)
                {
                    using MemoryStream ms = new MemoryStream();
                    WordEditorControl.Save(ms, Syncfusion.Windows.Controls.RichTextBoxAdv.FormatType.Docx);
                    _wordConverter.SaveWordDocument(ms, sfd.FileName);
                    MessageBox.Show($"Đã lưu bản sao Word thành công tại:\n{sfd.FileName}\n\n(Tệp Word gốc hoàn toàn được bảo vệ nguyên vẹn)", "Lưu Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi lưu bản sao Word: {ex.Message}", "Lỗi Hệ Thống", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #endregion

        #region TAB 3: HỘP THƯ TRÌNH KÝ BÁO CÁO LIÊN THÔNG (WORKFLOW MANAGEMENT)

        public class WorkflowApproverItem
        {
            public string Cccd { get; set; } = string.Empty;
            public string FullName { get; set; } = string.Empty;
            public string DisplayText { get; set; } = string.Empty;

            public override string ToString() => DisplayText;
        }

        private void PopulateWorkflowApprovers(string currentCccd)
        {
            try
            {
                var teachers = _vgcaAuth.GetTeacherList();
                var items = new List<WorkflowApproverItem>();

                foreach (var t in teachers)
                {
                    if (string.Equals(t.Cccd, currentCccd, StringComparison.OrdinalIgnoreCase)) continue;
                    bool isLeadership = !string.IsNullOrEmpty(t.Title) && 
                        (t.Title.Contains("Hiệu trưởng", StringComparison.OrdinalIgnoreCase) || 
                         t.Title.Contains("Hiệu phó", StringComparison.OrdinalIgnoreCase) ||
                         t.Title.Contains("Phó hiệu trưởng", StringComparison.OrdinalIgnoreCase));
                    bool isDepartmentHead = !string.IsNullOrEmpty(t.Title) &&
                        t.Title.Contains("Tổ trưởng", StringComparison.OrdinalIgnoreCase);
                    string prefix = isLeadership ? "⭐ " : (isDepartmentHead ? "📋 " : "");
                    items.Add(new WorkflowApproverItem
                    {
                        Cccd = t.Cccd,
                        FullName = t.FullName,
                        DisplayText = $"{prefix}{t.FullName} — {t.Title ?? "Giáo viên"} ({t.Department ?? ""})"
                    });
                }

                var sorted = items.OrderByDescending(i => i.DisplayText.StartsWith("⭐"))
                                  .ThenByDescending(i => i.DisplayText.StartsWith("📋"))
                                  .ThenBy(i => i.DisplayText)
                                  .ToList();

                CboWorkflowApprover.ItemsSource = sorted;
                if (sorted.Count > 0)
                {
                    CboWorkflowApprover.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi nạp danh sách người duyệt: {ex}");
            }
        }

        public async Task RefreshWorkflowDataAsync()
        {
            try
            {
                string myCccd = _vgcaAuth.CurrentTeacher?.Cccd ?? string.Empty;

                // Tự động đồng bộ danh sách giáo viên mới nhất từ đám mây (nếu có cập nhật từ web quản trị)
                try
                {
                    await _vgcaAuth.SyncRosterFromCloudAsync();
                    PopulateWorkflowApprovers(myCccd);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning($"Bỏ qua lỗi tự động đồng bộ roster: {ex.Message}");
                }

                if (string.IsNullOrEmpty(myCccd))
                {
                    LvInboxReports.ItemsSource = null;
                    LvOutboxReports.ItemsSource = null;
                    LvArchiveReports.ItemsSource = null;
                    TxtCountInbox.Text = "0";
                    TxtCountOutbox.Text = "0";
                    TxtCountCompleted.Text = "0";
                    return;
                }

                var all = await _workflowService.GetAllReportsAsync();
                _allReports = all;

                // 1. Inbox: Chờ tôi duyệt (người duyệt là tôi và trạng thái còn hiệu lực)
                var inbox = all.Where(r => 
                    string.Equals(r.ApproverCccd, myCccd, StringComparison.OrdinalIgnoreCase) &&
                    (r.Status == "WAITING_OPEN" || r.Status == "ACQUIRED_LOCKED" || r.Status == "LOCK_TIMEOUT")
                ).ToList();

                // 2. Outbox: Tôi đã gửi
                var outbox = all.Where(r =>
                    string.Equals(r.SenderCccd, myCccd, StringComparison.OrdinalIgnoreCase)
                ).ToList();

                // 3. Archive: Toàn trường đã hoàn tất
                var archive = all.Where(r => r.Status == "SIGNED_COMMITTED").ToList();

                LvInboxReports.ItemsSource = inbox;
                LvOutboxReports.ItemsSource = outbox;
                LvArchiveReports.ItemsSource = archive;

                TxtCountInbox.Text = inbox.Count.ToString();
                TxtCountOutbox.Text = outbox.Count.ToString();
                TxtCountCompleted.Text = archive.Count.ToString();

                if (inbox.Count > 0)
                {
                    try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi làm mới dữ liệu workflow: {ex}");
            }
        }

        private void BtnSubmitWorkflowReport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastSignedPdfPath) || !File.Exists(_lastSignedPdfPath))
            {
                MessageBox.Show("Vui lòng thực hiện ký số văn bản trước khi gửi trình ký liên thông.",
                    "Chưa Có Văn Bản Đã Ký", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_vgcaAuth.IsLoggedIn || _vgcaAuth.CurrentTeacher == null)
            {
                MessageBox.Show("Bạn cần đăng nhập tài khoản chữ ký số VGCA trước khi trình ký báo cáo.",
                    "Chưa Đăng Nhập", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string title = !string.IsNullOrWhiteSpace(_originalWordFilePath) 
                ? Path.GetFileNameWithoutExtension(_originalWordFilePath) 
                : Path.GetFileNameWithoutExtension(_lastSignedPdfPath);

            TxtWorkflowSubmitTitle.Text = title;
            TxtWorkflowSubmitSender.Text = $"{_vgcaAuth.CurrentTeacher.FullName} (CCCD: {_vgcaAuth.CurrentTeacher.Cccd})";
            TxtWorkflowSubmitNotes.Text = string.Empty;

            if (PdfPreviewControl != null)
            {
                PdfPreviewControl.Visibility = Visibility.Hidden;
            }
            OverlaySubmitWorkflow.Visibility = Visibility.Visible;
        }

        private void BtnCancelSubmitWorkflow_Click(object sender, RoutedEventArgs e)
        {
            OverlaySubmitWorkflow.Visibility = Visibility.Collapsed;
            if (PdfPreviewControl != null)
            {
                PdfPreviewControl.Visibility = Visibility.Visible;
            }
        }

        private async void BtnConfirmSubmitWorkflow_Click(object sender, RoutedEventArgs e)
        {
            string title = TxtWorkflowSubmitTitle.Text.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                MessageBox.Show("Vui lòng nhập tiêu đề báo cáo.", "Thiếu Tiêu Đề", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CboWorkflowApprover.SelectedItem is not WorkflowApproverItem approver)
            {
                MessageBox.Show("Vui lòng chọn người nhận phê duyệt báo cáo.", "Chưa Chọn Người Duyệt", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_lastSignedPdfPath) || !File.Exists(_lastSignedPdfPath))
            {
                MessageBox.Show("Tệp văn bản đã ký không tồn tại.", "Lỗi Tệp", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            BtnConfirmSubmitWorkflow.IsEnabled = false;
            UpdateStatus("Đang gửi trình ký báo cáo lên hệ thống đám mây...");

            try
            {
                var result = await _workflowService.SubmitReportAsync(
                    title: title,
                    senderName: _vgcaAuth.CurrentTeacher?.FullName ?? "Giáo viên",
                    senderCccd: _vgcaAuth.CurrentTeacher?.Cccd ?? "",
                    approverName: approver.FullName,
                    approverCccd: approver.Cccd,
                    localPdfPath: _lastSignedPdfPath,
                    notes: TxtWorkflowSubmitNotes.Text.Trim()
                );

                if (result.Success)
                {
                    OverlaySubmitWorkflow.Visibility = Visibility.Collapsed;
                    if (PdfPreviewControl != null)
                    {
                        PdfPreviewControl.Visibility = Visibility.Visible;
                    }

                    MessageBox.Show(
                        $"Đã gửi trình ký báo cáo thành công!\n\n• Mã báo cáo: {result.ReportId}\n• Người nhận duyệt: {approver.FullName}\n• Trạng thái: Chờ mở duyệt (WAITING_OPEN)",
                        "Trình Ký Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);

                    UpdateStatus($"Đã gửi trình ký báo cáo '{title}' tới {approver.FullName} (Mã: {result.ReportId})");

                    MainTabControl.SelectedIndex = 2;
                    TabControlWorkflowSub.SelectedIndex = 1;
                    await RefreshWorkflowDataAsync();
                }
                else
                {
                    MessageBox.Show($"Không thể gửi trình ký báo cáo:\n{result.Message}", "Lỗi Gửi Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Error);
                    UpdateStatus($"Lỗi gửi báo cáo: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi hệ thống: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnConfirmSubmitWorkflow.IsEnabled = true;
                if (OverlaySubmitWorkflow.Visibility != Visibility.Visible && PdfPreviewControl != null)
                {
                    PdfPreviewControl.Visibility = Visibility.Visible;
                }
            }
        }

        private async void BtnOpenForReview_Click(object sender, RoutedEventArgs e)
        {
            await OpenSelectedInboxReportForReviewAsync();
        }

        private async void LvInboxReports_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            await OpenSelectedInboxReportForReviewAsync();
        }

        private async Task OpenSelectedInboxReportForReviewAsync()
        {
            if (LvInboxReports.SelectedItem is not ReportModel report)
            {
                MessageBox.Show("Vui lòng chọn một báo cáo trong danh sách để mở ký duyệt.",
                    "Chưa Chọn Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_vgcaAuth.IsLoggedIn || _vgcaAuth.CurrentTeacher == null)
            {
                MessageBox.Show("Bạn cần đăng nhập tài khoản chữ ký số VGCA để mở và ký duyệt văn bản.",
                    "Chưa Đăng Nhập", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"Bạn có muốn mở văn bản:\n\"{report.Title}\"\ndo thầy/cô {report.SenderName} lập để ký số phê duyệt không?",
                "Xác Nhận Ký Duyệt Báo Cáo", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            UpdateStatus($"Đang tải tệp báo cáo '{report.ReportId}' và chiếm quyền xử lý...");
            BtnOpenForReview.IsEnabled = false;

            try
            {
                var result = await _workflowService.AcquireReportForReviewAsync(report.ReportId, _vgcaAuth.CurrentTeacher.Cccd);
                if (result.Success && !string.IsNullOrEmpty(result.DownloadedFilePath))
                {
                    _activeWorkflowReportId = report.ReportId;
                    _activeWorkflowExpectedBaseHash = result.BaseHash;

                    BorderWorkflowReviewNotice.Visibility = Visibility.Visible;
                    TxtWorkflowReviewNoticeHeader.Text = $"CHẾ ĐỘ DUYỆT BÁO CÁO LIÊN THÔNG: {report.Title}";
                    TxtWorkflowReviewNoticeDetail.Text = $"Người lập: {report.SenderName} • Ngày gửi: {report.CreatedTimeFormatted} • Đã khóa phiên xử lý.";
                    BtnCommitWorkflowApproval.Visibility = Visibility.Visible;
                    BtnSubmitWorkflowReport.Visibility = Visibility.Collapsed;

                    await LoadPdfDocumentAsync(result.DownloadedFilePath);

                    MainTabControl.SelectedIndex = 1;
                    UpdateStatus($"Đã nạp báo cáo '{report.Title}' sang không gian ký duyệt. Vui lòng ký số và bấm 'Nộp Báo Cáo Đã Duyệt'.");
                }
                else
                {
                    MessageBox.Show($"Không thể mở văn bản này:\n{result.Message}", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                    await RefreshWorkflowDataAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi nạp văn bản: {ex.Message}", "Lỗi Hệ Thống", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnOpenForReview.IsEnabled = true;
            }
        }

        private void BtnExitWorkflowReviewMode_Click(object sender, RoutedEventArgs e)
        {
            if (_isOutboxViewMode)
            {
                _isOutboxViewMode = false;
                BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                BtnSignCurrentDocument.IsEnabled = _lastSignedPdfPath != null;
                MainTabControl.SelectedIndex = 2;
                TabControlWorkflowSub.SelectedIndex = 1;
                UpdateStatus("Đã đóng chế độ xem báo cáo đã gửi.");
                return;
            }

            var confirm = MessageBox.Show("Bạn có chắc chắn muốn thoát khỏi chế độ phê duyệt báo cáo liên thông không?",
                "Thoát Chế Độ Phê Duyệt", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm == MessageBoxResult.Yes)
            {
                _activeWorkflowReportId = null;
                _activeWorkflowExpectedBaseHash = null;
                BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                UpdateStatus("Đã thoát chế độ phê duyệt liên thông.");
            }
        }

        private async void BtnCommitWorkflowApproval_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_activeWorkflowReportId))
            {
                MessageBox.Show("Không có phiên phê duyệt báo cáo nào đang mở.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_lastSignedPdfPath) || !File.Exists(_lastSignedPdfPath))
            {
                MessageBox.Show("Vui lòng thực hiện ký số văn bản (đóng con dấu phê duyệt) trước khi nộp lên hệ thống.",
                    "Chưa Ký Số", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnCommitWorkflowApproval.IsEnabled = false;
            UpdateStatus("Đang kiểm tra tính toàn vẹn (Base Hash) và nộp bản phê duyệt...");

            try
            {
                var result = await _workflowService.CommitSignedReportAsync(
                    reportId: _activeWorkflowReportId,
                    approverCccd: _vgcaAuth.CurrentTeacher?.Cccd ?? "",
                    signedPdfPath: _lastSignedPdfPath,
                    expectedBaseHash: _activeWorkflowExpectedBaseHash ?? ""
                );

                if (result.Success)
                {
                    MessageBox.Show(
                        "Đã phê duyệt và nộp báo cáo hoàn tất thành công!\n\nVăn bản đã được lưu trữ vĩnh viễn với đầy đủ 2 chữ ký số PAdES LTV.",
                        "Phê Duyệt Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);

                    _activeWorkflowReportId = null;
                    _activeWorkflowExpectedBaseHash = null;
                    BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                    BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                    BtnSubmitWorkflowReport.Visibility = Visibility.Visible;

                    MainTabControl.SelectedIndex = 2;
                    TabControlWorkflowSub.SelectedIndex = 2;
                    await RefreshWorkflowDataAsync();
                }
                else
                {
                    MessageBox.Show($"Không thể nộp bản phê duyệt:\n{result.Message}", "Lỗi Nộp Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Error);
                    UpdateStatus($"Lỗi nộp báo cáo: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi hệ thống: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnCommitWorkflowApproval.IsEnabled = true;
            }
        }

        private async void BtnRecallReport_Click(object sender, RoutedEventArgs e)
        {
            if (LvOutboxReports.SelectedItem is not ReportModel report)
            {
                MessageBox.Show("Vui lòng chọn một báo cáo bạn đã gửi để thu hồi.", "Chưa Chọn Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (report.Status == "SIGNED_COMMITTED")
            {
                MessageBox.Show("Báo cáo này đã được phê duyệt và hoàn tất ký 2 cấp nên không thể thu hồi.",
                    "Không Thể Thu Hồi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"Bạn có chắc chắn muốn thu hồi báo cáo:\n\"{report.Title}\"\nđã gửi cho {report.ApproverName} không?",
                "Xác Nhận Thu Hồi", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            BtnRecallReport.IsEnabled = false;
            UpdateStatus($"Đang thu hồi báo cáo '{report.ReportId}'...");

            try
            {
                var result = await _workflowService.RecallReportAsync(report.ReportId, _vgcaAuth.CurrentTeacher?.Cccd ?? "");
                if (result.Success)
                {
                    MessageBox.Show("Thu hồi báo cáo thành công!", "Thu Hồi Hoàn Tất", MessageBoxButton.OK, MessageBoxImage.Information);
                    UpdateStatus($"Đã thu hồi báo cáo '{report.Title}' thành công.");
                    await RefreshWorkflowDataAsync();
                }
                else
                {
                    MessageBox.Show($"Không thể thu hồi:\n{result.Message}", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi thu hồi: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnRecallReport.IsEnabled = true;
            }
        }

        private async void BtnViewOutboxReport_Click(object sender, RoutedEventArgs e)
        {
            await OpenSelectedOutboxReportForViewingAsync();
        }

        private async void LvOutboxReports_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            await OpenSelectedOutboxReportForViewingAsync();
        }

        private async Task OpenSelectedOutboxReportForViewingAsync()
        {
            if (LvOutboxReports.SelectedItem is not ReportModel report)
            {
                MessageBox.Show("Vui lòng chọn một báo cáo trong danh sách đã gửi để xem.",
                    "Chưa Chọn Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateStatus($"Đang tải bản sao báo cáo '{report.ReportId}' để hiển thị...");
            BtnViewOutboxReport.IsEnabled = false;

            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro_Reports");
                Directory.CreateDirectory(tempDir);
                // Tạo tên file Snapshot ngẫu nhiên GUID để triệt tiêu hoàn toàn xung đột handle file trên ổ đĩa
                string destPath = Path.Combine(tempDir, $"{report.ReportId}_OutboxView_{Guid.NewGuid():N}.pdf");

                bool downloaded = await _workflowService.DownloadFileAsync(report.DriveFileId, destPath);
                if (downloaded && File.Exists(destPath))
                {
                    _isOutboxViewMode = true;
                    _activeWorkflowReportId = null; // Cấm không gán ID phiên duyệt để tránh commit nhầm

                    BorderWorkflowReviewNotice.Visibility = Visibility.Visible;
                    TxtWorkflowReviewNoticeHeader.Text = $"CHẾ ĐỘ XEM BÁO CÁO ĐÃ GỬI (CHỈ ĐỌC): {report.Title}";
                    TxtWorkflowReviewNoticeDetail.Text = $"Người nhận: {report.ApproverName} • Ngày gửi: {report.CreatedTimeFormatted} • Trạng thái: {report.StatusText} (Chế độ xem an toàn, không ảnh hưởng phiên ký của người duyệt).";
                    BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                    BtnSubmitWorkflowReport.Visibility = Visibility.Collapsed;
                    BtnSignCurrentDocument.IsEnabled = false;
                    BtnInsertSignatureStamp.IsEnabled = false;

                    await LoadPdfDocumentAsync(destPath);
                    MainTabControl.SelectedIndex = 1;
                    UpdateStatus($"Đang hiển thị bản gửi: '{report.Title}' (Chế độ xem chỉ đọc).");
                }
                else
                {
                    MessageBox.Show("Không thể tải tệp báo cáo từ máy chủ đám mây.", "Lỗi Tải Tệp", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi hiển thị tệp: {ex.Message}", "Lỗi Hệ Thống", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnViewOutboxReport.IsEnabled = true;
            }
        }

        private async void BtnViewArchiveReport_Click(object sender, RoutedEventArgs e)
        {
            if (LvArchiveReports.SelectedItem is not ReportModel report)
            {
                MessageBox.Show("Vui lòng chọn một báo cáo hoàn tất trong danh sách để xem.", "Chưa Chọn Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateStatus($"Đang tải tệp hoàn tất '{report.ReportId}' để hiển thị...");
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro_Reports");
                Directory.CreateDirectory(tempDir);
                string destPath = Path.Combine(tempDir, $"{report.ReportId}_Completed.pdf");

                bool downloaded = await _workflowService.DownloadFileAsync(report.DriveFileId, destPath);
                if (downloaded && File.Exists(destPath))
                {
                    await LoadPdfDocumentAsync(destPath);
                    MainTabControl.SelectedIndex = 1;
                    UpdateStatus($"Đang hiển thị báo cáo hoàn tất: {report.Title}");
                }
                else
                {
                    MessageBox.Show("Không thể tải tệp báo cáo hoàn tất từ máy chủ.", "Lỗi Tải Tệp", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi hiển thị tệp: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnDownloadArchiveReport_Click(object sender, RoutedEventArgs e)
        {
            if (LvArchiveReports.SelectedItem is not ReportModel report)
            {
                MessageBox.Show("Vui lòng chọn một báo cáo trong danh sách để tải về máy.", "Chưa Chọn Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveFileDialog sfd = new SaveFileDialog
            {
                Title = "Lưu báo cáo hoàn tất về máy tính",
                FileName = $"{report.Title}_DaKyHoanTat.pdf",
                Filter = "Tài liệu PDF (*.pdf)|*.pdf|Tất cả tệp (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (sfd.ShowDialog() == true)
            {
                UpdateStatus("Đang tải tệp về máy tính...");
                try
                {
                    bool success = await _workflowService.DownloadFileAsync(report.DriveFileId, sfd.FileName);
                    if (success)
                    {
                        MessageBox.Show($"Đã tải và lưu báo cáo hoàn tất thành công tại:\n{sfd.FileName}",
                            "Tải Về Hoàn Tất", MessageBoxButton.OK, MessageBoxImage.Information);
                        UpdateStatus($"Đã lưu tệp tại: {sfd.FileName}");
                    }
                    else
                    {
                        MessageBox.Show("Không thể tải tệp từ máy chủ.", "Lỗi Tải Về", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi khi tải tệp: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnRefreshWorkflow_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatus("Đang đồng bộ dữ liệu Hộp thư trình ký từ đám mây...");
            await RefreshWorkflowDataAsync();
            UpdateStatus("Đã làm mới dữ liệu trình ký liên thông.");
        }

        private void BtnNewReportFromTab3_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 1;
        }

        #endregion

        #region TAB 4: KIỂM TRA CHỮ KÝ (VERIFY)

        private void BtnVerifyPdf_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = "Chọn tệp PDF để xác minh chữ ký số",
                Filter = "Tài liệu PDF (*.pdf)|*.pdf|Tất cả tệp (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                string pdfPath = dialog.FileName;
                TxtVerifyPdfPath.Text = pdfPath;

                UpdateStatus($"Đang phân tích chữ ký trong tệp: {Path.GetFileName(pdfPath)}...");

                List<VerifiedSignatureInfo> signatures = _pdfVerifier.VerifyPdf(pdfPath);
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