using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text;
using Newtonsoft.Json;
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
        private readonly UpdateService _updateService = new();
        private readonly WorkflowRealtimeWatcher _realtimeWatcher;
        private string? _activeWorkflowReportId;
        private string? _activeWorkflowExpectedBaseHash;
        private bool _isOutboxViewMode;
        private bool _isWorkflowSealingMode;
        private List<ReportModel> _allReports = new();

        private static bool IsLeadership(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            return title.Contains("Hiệu trưởng", StringComparison.OrdinalIgnoreCase) ||
                   title.Contains("Hiệu phó", StringComparison.OrdinalIgnoreCase) ||
                   title.Contains("Phó hiệu trưởng", StringComparison.OrdinalIgnoreCase);
        }

        private List<CertificateModel> _certificates = new();
        private List<SignatureProfile> _profiles = new();
        private CertificateModel? _currentActiveCert;
        private CertPreferenceModel? _certPreference;

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

        private TaskCompletionSource<bool>? _pdfRenderCompletionSource;
        private TaskCompletionSource<bool>? _excelRenderCompletionSource;
        private DateTime _loadingStartTime = DateTime.MinValue;
        private TextBlock? _popupLoadingTitle;
        private TextBlock? _popupLoadingMessage;
        private TextBlock? _popupLoadingIcon;
        private Button? _btnCancelSigningOverlay;
        private System.Threading.CancellationTokenSource? _signingCts;
        private DateTime _lastActivatedCheckTime = DateTime.MinValue;
        private DateTime _lastRosterSyncTime = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();
            WindowState = App.IsAutoStartMode ? WindowState.Minimized : WindowState.Maximized;
            Loaded += MainWindow_Loaded;
            LocationChanged += (s, e) => RepositionLoadingPopup();
            SizeChanged += (s, e) => RepositionLoadingPopup();

            _wordConverter.StatusChanged += UpdateStatus;
            _excelConverter.StatusChanged += UpdateStatus;
            _pdfSigner.StatusChanged += UpdateStatus;
            _webUploader.StatusChanged += UpdateStatus;
            _vgcaAuth.LoginStateChanged += OnVgcaLoginStateChanged;
            Activated += MainWindow_Activated;

            _realtimeWatcher = new WorkflowRealtimeWatcher(_workflowService);
            _realtimeWatcher.OnDataChanged += () =>
            {
                Dispatcher.InvokeAsync(async () =>
                {
                    await SilentRefreshWorkflowDataAsync();
                }, System.Windows.Threading.DispatcherPriority.Background);
            };

            _realtimeWatcher.OnReportReturned += (repId, title, reason) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    UpdateStatus($"🔔 Báo cáo '{title}' bị trả về: {reason}");
                }, System.Windows.Threading.DispatcherPriority.Background);
            };

            _realtimeWatcher.OnNewReportToApprove += (repId, title, senderName) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    UpdateStatus($"🔔 Báo cáo mới cần duyệt từ {senderName}: '{title}'");
                }, System.Windows.Threading.DispatcherPriority.Background);
            };

            Closed += (s, e) => _realtimeWatcher.Dispose();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.IsAutoStartMode)
                {
                    WindowState = WindowState.Minimized;
                    UpdateStatus("Hệ thống tự động khởi động cùng Windows (Chế độ sẵn sàng nền).");
                }
                else
                {
                    WindowState = WindowState.Maximized;
                    UpdateStatus("Khởi tạo hệ thống VGCA Sign Tool Pro...");
                }

                if (OverlayLoadingPopup != null && MainRootGrid != null)
                {
                    OverlayLoadingPopup.PlacementTarget = MainRootGrid;
                }

                try
                {
                    var asmVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                    if (asmVer != null && TxtAppVersion != null)
                    {
                        TxtAppVersion.Text = $"Phiên bản v{asmVer.Major}.{asmVer.Minor}.{asmVer.Build} • .NET 8 x64";
                    }
                }
                catch { }

                await InitializePreviewEngineAsync();

                InitializeVgcaAuthRoster();
                LoadCertificatesAndVerifyVGCALogin();
                MainTabControl.SelectedIndex = 1;
                LoadProfiles();

                // Kiểm tra cập nhật ngầm sau khi app khởi động 2.5 giây (không block UI)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        await CheckUpdateSilentAsync();
                    });
                });

                // Tự động đồng bộ danh bạ và phân quyền từ đám mây (Firebase) khi khởi động
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1000);
                        var syncRes = await _vgcaAuth.SyncRosterFromCloudAsync();
                        if (syncRes.Success)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (_vgcaAuth.IsLoggedIn && _vgcaAuth.CurrentTeacher != null)
                                {
                                    OnVgcaLoginStateChanged(true, _vgcaAuth.CurrentTeacher);
                                }
                                UpdateStatus($"Đã đồng bộ phân quyền từ máy chủ đám mây ({syncRes.Count} nhân sự).");
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.TraceWarning($"Bỏ qua lỗi tự động đồng bộ roster khởi động: {ex.Message}");
                    }
                });
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
                PdfPreviewControl.DefaultBackgroundColor = System.Drawing.Color.FromArgb(248, 250, 252);

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

                string previewCacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VGCASignToolPro", "PreviewCache");
                if (!Directory.Exists(previewCacheDir))
                {
                    Directory.CreateDirectory(previewCacheDir);
                }

                PdfPreviewControl.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "vgcaviewer.local",
                    wwwroot,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

                PdfPreviewControl.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "vgcacontent.local",
                    previewCacheDir,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

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

                // Cảnh báo thông minh nếu máy tính Windows cũ bị thiếu Microsoft Edge WebView2 Evergreen Runtime
                bool isWebView2Missing = ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException ||
                                         ex is System.IO.FileNotFoundException ||
                                         ex.GetType().Name.Contains("WebView2", StringComparison.OrdinalIgnoreCase) ||
                                         ex.Message.Contains("WebView2", StringComparison.OrdinalIgnoreCase);

                if (isWebView2Missing)
                {
                    var msgResult = MessageBox.Show(
                        "Máy tính của bạn chưa có thư viện 'Microsoft Edge WebView2 Runtime' (thường gặp trên Windows 10 bản cũ).\n\n" +
                        "Phần mềm cần thư viện này để hiển thị văn bản PDF và căn chỉnh vị trí chữ ký số trực quan.\n\n" +
                        "Bạn có muốn mở trang tải chính thức của Microsoft để cài đặt không?",
                        "Cần Cài Đặt Microsoft Edge WebView2 Runtime",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (msgResult == MessageBoxResult.Yes)
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }
                }
            }
        }

        private void CoreWebView2_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string jsonString = e.WebMessageAsJson;
                var root = Newtonsoft.Json.Linq.JObject.Parse(jsonString);
                string? msgType = (string?)root["type"];
                if (msgType == "stampCoordinatesChanged")
                {
                    var coords = root["coords"];
                    if (coords != null &&
                        coords["pageNumber"] != null &&
                        coords["x"] != null &&
                        coords["y"] != null &&
                        coords["width"] != null &&
                        coords["height"] != null)
                    {
                        int pageNum = (int)coords["pageNumber"]!;
                        double xVal = (double)coords["x"]!;
                        double yVal = (double)coords["y"]!;
                        double wVal = (double)coords["width"]!;
                        double hVal = (double)coords["height"]!;

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
                    if (root["totalPages"] != null)
                    {
                        int totalPages = (int)root["totalPages"]!;
                        UpdateStatus($"Đã nạp xong tài liệu PDF tương tác ({totalPages} trang).");
                    }
                    BtnInsertSignatureStamp.IsEnabled = true;
                    _pdfRenderCompletionSource?.TrySetResult(true);
                }
                else if (msgType == "pdfLoadError")
                {
                    string errMsg = (string?)root["message"] ?? "Lỗi không xác định";
                    VGCASignToolPro.Services.AppLogger.Error($"WebView2 PDF.js báo lỗi: {errMsg}", null, "WebMessageReceived");
                    UpdateStatus($"Lỗi hiển thị PDF: {errMsg}");
                    _pdfRenderCompletionSource?.TrySetResult(false);
                }
                else if (msgType == "excelLoaded")
                {
                    int sheetCount = (int?)root["sheetCount"] ?? 1;
                    TxtCurrentFileStatus.Text = $"✅ Bảng tính Excel ({sheetCount} sheet). Click đúp vào ô để sửa dữ liệu, chọn khổ in và bấm 'Chuyển Qua PDF Để Ký'.";
                    UpdateStatus($"Đã nạp thành công bảng tính Excel ({sheetCount} sheet).");
                    _excelRenderCompletionSource?.TrySetResult(true);
                }
                else if (msgType == "excelOrientationChanged")
                {
                    _currentExcelOrientation = (string?)root["orientation"] ?? "landscape";
                    UpdateStatus($"Đã đổi định dạng in bảng tính sang khổ {(_currentExcelOrientation == "portrait" ? "Dọc A4" : "Ngang A4")}.");
                }
                    else if (msgType == "requestExportExcelPdf")
                    {
                        Dispatcher.Invoke(async () =>
                        {
                            await ConvertExcelToPdfAsync();
                        });
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
                    UpdateStatus("Danh bạ giáo viên sẵn sàng đồng bộ từ đám mây. Hệ thống tự động nhận diện chứng thư từ Windows.");
                    return;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Lỗi nạp danh bạ: {ex.Message}");
            }
        }

        private void ClearCertificateDetailsUI()
        {
            if (TxtDetailSignerName != null) TxtDetailSignerName.Text = "Chưa phát hiện thiết bị";
            if (TxtDetailRoleAndSeal != null)
            {
                TxtDetailRoleAndSeal.Text = "Vui lòng mở Virtual CSP hoặc cắm USB Token để tự động nhận diện danh tính và phân quyền";
                TxtDetailRoleAndSeal.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            }
            if (TxtDetailUsbStatus != null)
            {
                TxtDetailUsbStatus.Text = "⚠️ Chưa cắm USB Token hoặc chưa khởi động Virtual CSP.";
                TxtDetailUsbStatus.Foreground = new SolidColorBrush(Color.FromRgb(217, 119, 6));
            }

            if (TxtDetailEmail != null) TxtDetailEmail.Text = "---";
            if (TxtDetailOrg != null) TxtDetailOrg.Text = "---";
            if (TxtDetailIssuer != null) TxtDetailIssuer.Text = "---";
            if (TxtDetailSerial != null) TxtDetailSerial.Text = "---";
            if (TxtDetailValidity != null) TxtDetailValidity.Text = "---";
            if (TxtDetailAlgorithm != null) TxtDetailAlgorithm.Text = "---";
            if (TxtDetailThumbprint != null) TxtDetailThumbprint.Text = "---";

            if (TxtSignerDisplayName != null) TxtSignerDisplayName.Text = string.Empty;
            if (TxtQuickCertNotice != null) TxtQuickCertNotice.Text = "Chưa kết nối chứng thư VGCA (Vui lòng cắm USB hoặc mở Virtual CSP)";
            if (CmbCertificates != null) CmbCertificates.ItemsSource = null;
        }

        private void OnVgcaLoginStateChanged(bool isLoggedIn, VgcaTeacherModel? teacher)
        {
            Dispatcher.Invoke(() =>
            {
                if (isLoggedIn && teacher != null)
                {
                    GrpVgcaActiveCard.Visibility = Visibility.Visible;

                    BadgeVgcaGlobalStatus.Background = new SolidColorBrush(Color.FromRgb(6, 95, 70));
                    TxtVgcaGlobalStatusIcon.Text = "\uE73E";
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
                        ? $"{teacher.Department} - {teacher.Organization ?? ""}"
                        : (!string.IsNullOrEmpty(teacher.Organization) ? teacher.Organization : "Đơn vị giáo dục");
                    PopulateWorkflowApprovers(teacher.Cccd);
                    _ = RefreshWorkflowDataAsync();

                    // Tự động khôi phục mẫu chữ ký & ảnh con dấu từ Cloud nếu máy mới chưa có ảnh
                    if (!string.IsNullOrWhiteSpace(teacher.Cccd))
                    {
                        bool hasAnyStamp = _profiles != null && _profiles.Any(p => !string.IsNullOrEmpty(p.Image) || (!string.IsNullOrEmpty(p.ImagePath) && File.Exists(p.ImagePath)));
                        if (!hasAnyStamp)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var restored = await _profileManager.RestoreProfilesFromCloudAsync(teacher.Cccd);
                                    if (restored != null && restored.Count > 0 && restored.Any(p => !string.IsNullOrEmpty(p.Image) || (!string.IsNullOrEmpty(p.ImagePath) && File.Exists(p.ImagePath))))
                                    {
                                        Dispatcher.Invoke(() =>
                                        {
                                            _profiles = restored;
                                            CmbProfiles.ItemsSource = null;
                                            CmbProfiles.ItemsSource = _profiles;
                                            if (_profiles.Count > 0) CmbProfiles.SelectedIndex = 0;
                                            UpdateStatus($"Đã tự động khôi phục {restored.Count} mẫu chữ ký và ảnh con dấu từ đám mây!");
                                        });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Trace.TraceWarning($"Lỗi khôi phục chữ ký từ Cloud: {ex.Message}");
                                }
                            });
                        }
                    }

                    // Mở khóa Tab 2
                    TabItemSignWorkspace.Header = "  Soạn Thảo & Ký Số  ";
                    TabItemSignWorkspace.IsEnabled = true;
                    OverlayTab2Locked.Visibility = Visibility.Collapsed;
                    if (PdfPreviewControl != null)
                    {
                        PdfPreviewControl.Visibility = Visibility.Visible;
                    }

                    // Cập nhật Chức vụ và Quyền hạn đóng dấu
                    if (TxtDetailRoleAndSeal != null)
                    {
                        string cccdPart = !string.IsNullOrWhiteSpace(teacher.Cccd) ? $"CCCD: {teacher.Cccd} • " : "";
                        if (teacher.CanSeal)
                        {
                            TxtDetailRoleAndSeal.Text = $"{teacher.Title} • {cccdPart}🔴 ĐƯỢC PHÂN QUYỀN ĐÓNG DẤU CƠ QUAN (USB TOKEN)";
                            TxtDetailRoleAndSeal.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28)); // Đỏ nổi bật
                        }
                        else
                        {
                            TxtDetailRoleAndSeal.Text = $"{teacher.Title} • {cccdPart}🔵 Ký Số Cá Nhân Giáo Viên";
                            TxtDetailRoleAndSeal.Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235)); // Xanh Royal Blue
                        }
                    }

                    // Đồng bộ chứng thư trong kho Windows Store theo đối soát danh tính
                    CertificateModel? matchedCert = _currentActiveCert ?? _certService.GetActiveVGCACertificate(teacher.FullName, teacher.Cccd, teacher.SigningMethod);
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

                    // Cập nhật dòng trạng thái USB Token & Virtual CSP thực tế
                    if (TxtDetailUsbStatus != null)
                    {
                        if (matchedCert != null)
                        {
                            if (matchedCert.IsVirtualCspCertificate)
                            {
                                TxtDetailBannerIcon.Text = "🛡️";
                                TxtDetailBannerTitle.Text = "ĐÃ NHẬN DIỆN VGCA VIRTUAL CSP";
                                TxtDetailBannerTitle.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                                TxtDetailBannerSubtitle.Text = "Sẵn sàng ký số từ xa qua ứng dụng VGCA Mobile trên điện thoại.";
                                BorderDetailBanner.Background = new SolidColorBrush(Color.FromRgb(240, 253, 244));
                                BorderDetailBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(134, 239, 172));

                                TxtDetailUsbStatus.Text = $"Đã phát hiện chứng thư Ban Cơ yếu ({matchedCert.CommonName}) & Virtual CSP đã sẵn sàng ký số";
                                TxtDetailUsbStatus.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));

                                BadgeVgcaGlobalStatus.Background = new SolidColorBrush(Color.FromRgb(6, 95, 70));
                                TxtVgcaGlobalStatusIcon.Text = "\uE73E";
                                TxtVgcaGlobalStatusText.Text = $"VGCA: {teacher.FullName}";
                            }
                            else
                            {
                                // USB Token vật lý phần cứng (Cá nhân BGH hoặc Con dấu cơ quan)
                                TxtDetailBannerIcon.Text = "\uE730";
                                TxtDetailBannerTitle.Text = "Đã kết nối USB Token phần cứng";
                                TxtDetailBannerTitle.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                                string tokenDesc = matchedCert.IsOrganizationCertificate
                                    ? $"USB Token con dấu nhà trường: {matchedCert.CommonName}"
                                    : $"USB Token cá nhân: {matchedCert.CommonName}";
                                TxtDetailBannerSubtitle.Text = $"Thiết bị {tokenDesc} đã sẵn sàng ký số.";
                                BorderDetailBanner.Background = new SolidColorBrush(Color.FromRgb(240, 253, 244));
                                BorderDetailBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(134, 239, 172));

                                TxtDetailUsbStatus.Text = $"{tokenDesc}. Sẵn sàng ký số";
                                TxtDetailUsbStatus.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));

                                BadgeVgcaGlobalStatus.Background = new SolidColorBrush(Color.FromRgb(6, 95, 70));
                                TxtVgcaGlobalStatusIcon.Text = "\uE73E";
                                TxtVgcaGlobalStatusText.Text = $"VGCA Token: {teacher.FullName}";
                            }
                        }
                        else
                        {
                            TxtDetailUsbStatus.Text = "ℹ️ Đang quét kết nối chữ ký số Ban Cơ Yếu...";
                            TxtDetailUsbStatus.Foreground = new SolidColorBrush(Color.FromRgb(217, 119, 6)); // Amber
                        }
                    }

                    string maskedId = !string.IsNullOrEmpty(teacher.SerialNumber)
                        ? $"Serial: {teacher.SerialNumber}"
                        : (!string.IsNullOrEmpty(teacher.Email) ? teacher.Email : (teacher.Cccd?.Length > 4 ? $"***{teacher.Cccd[^4..]}" : "***"));
                    UpdateStatus($"Phiên làm việc tài khoản giáo viên đã kích hoạt: {teacher.FullName} ({maskedId})");

                    bool canSeal = teacher.CanSeal;
                    TabItemWaitingSeal.Visibility = canSeal ? Visibility.Visible : Visibility.Collapsed;
                    BorderWaitingSealCount.Visibility = canSeal ? Visibility.Visible : Visibility.Collapsed;

                    UpdateSignAndSealButtonsState(!string.IsNullOrWhiteSpace(_currentWorkingPdfPath) && File.Exists(_currentWorkingPdfPath));
                }
                else
                {
                    GrpVgcaActiveCard.Visibility = Visibility.Visible;

                    BadgeVgcaGlobalStatus.Background = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                    TxtVgcaGlobalStatusIcon.Text = "\uE7BA";
                    TxtVgcaGlobalStatusText.Text = "Chưa kết nối chữ ký số";
                    TxtQuickCertNotice.Text = "Chưa kết nối chứng thư VGCA (Vui lòng cắm USB hoặc mở Virtual CSP)";

                    TxtDetailBannerIcon.Text = "\uE7BA";
                    TxtDetailBannerTitle.Text = "Chưa kết nối thiết bị ký số Ban Cơ yếu";
                    TxtDetailBannerTitle.Foreground = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                    TxtDetailBannerSubtitle.Text = "Vui lòng mở ứng dụng VGCA Virtual CSP hoặc cắm USB Token Ban Cơ yếu vào máy tính.";
                    BorderDetailBanner.Background = new SolidColorBrush(Color.FromRgb(255, 251, 235));
                    BorderDetailBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(253, 230, 138));

                    // Reset 100% chi tiết kỹ thuật về mặc định ("---"), chống hoàn toàn lỗi đọng dữ liệu trên RAM
                    ClearCertificateDetailsUI();

                    // Mở sẵn Tab 2 để giáo viên mở file Word, Excel, PDF xem bình thường
                    TabItemSignWorkspace.Header = "  Soạn Thảo & Ký Số  ";
                    TabItemSignWorkspace.IsEnabled = true;
                    OverlayTab2Locked.Visibility = Visibility.Collapsed;
                    if (PdfPreviewControl != null)
                    {
                        PdfPreviewControl.Visibility = Visibility.Visible;
                    }
                    UpdateSignAndSealButtonsState(false);

                    UpdateStatus("Chưa phát hiện thiết bị chữ ký số Ban Cơ Yếu. Vui lòng cắm USB Token hoặc mở Virtual CSP.");
                }
            });
        }

        private async void BtnVgcaLogout_Click(object sender, RoutedEventArgs e)
        {
            var choice = MessageBox.Show(
                "Bạn có chắc chắn muốn làm mới phiên làm việc chữ ký số VGCA không?\n(Sau khi làm mới, hệ thống sẽ quét lại chứng thư và tải lại phân quyền từ đám mây)",
                "Làm Mới Phiên Làm Việc", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (choice == MessageBoxResult.Yes)
            {
                if (_vgcaAuth == null)
                {
                    UpdateStatus("Dịch vụ xác thực chưa sẵn sàng.");
                    return;
                }

                await ConfirmAndCloseCurrentDocumentAsync(isManualClose: true);
                _activeWorkflowReportId = null;
                _activeWorkflowExpectedBaseHash = null;
                _isWorkflowSealingMode = false;
                _currentActiveCert = null;
                BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                BtnCommitWorkflowSeal.Visibility = Visibility.Collapsed;
                BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                CloseWorkflowApprovalChoicePopup();
                CloseForwardWorkflowPopup();

                _vgcaAuth.Logout();
                ClearCertificateDetailsUI();
                LoadCertificatesAndVerifyVGCALogin();
                UpdateStatus("Đã làm mới phiên làm việc VGCA an toàn.");
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
                    ClearCertificateDetailsUI();
                    _currentActiveCert = null;
                    _vgcaAuth.SetCurrentActiveIdentity(null);
                    return;
                }

                // Load preference đã lưu (nếu có) và khớp với cert thực tế trong kho
                _certPreference ??= CertPreferenceService.Load();
                CertificateModel? preferredCert = null;
                if (_certPreference != null && !string.IsNullOrEmpty(_certPreference.Thumbprint))
                {
                    preferredCert = _certificates.FirstOrDefault(c =>
                        string.Equals(c.Thumbprint, _certPreference.Thumbprint, StringComparison.OrdinalIgnoreCase));
                }

                // Nếu cert ưu tiên không còn trong kho (rút token), fallback về auto-detect
                CertificateModel? vgcaCert = preferredCert ?? _certService.GetActiveVGCACertificate();

                if (vgcaCert != null)
                {
                    _currentActiveCert = vgcaCert;
                    CmbCertificates.SelectedItem = vgcaCert;

                    TxtDetailIssuer.Text = vgcaCert.Issuer ?? "";
                    TxtDetailSerial.Text = vgcaCert.SerialNumber ?? "";
                    TxtDetailValidity.Text = $"{vgcaCert.NotBefore:dd/MM/yyyy} đến {vgcaCert.NotAfter:dd/MM/yyyy} ({(vgcaCert.IsValidNow ? "Còn hiệu lực" : "Đã hết hạn")})";
                    TxtDetailAlgorithm.Text = vgcaCert.KeyAlgorithm ?? "";
                    TxtDetailThumbprint.Text = vgcaCert.Thumbprint ?? "";

                    _vgcaAuth.SetCurrentActiveIdentity(vgcaCert);
                }
                else
                {
                    ClearCertificateDetailsUI();
                    _currentActiveCert = null;
                    _vgcaAuth.SetCurrentActiveIdentity(null);
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
            // Bỏ qua nếu vừa kiểm tra trong vòng 6 giây (chống nghẽn lag luồng UI khi người dùng chuyển qua lại các cửa sổ)
            if ((DateTime.UtcNow - _lastActivatedCheckTime).TotalSeconds < 6)
            {
                return;
            }
            _lastActivatedCheckTime = DateTime.UtcNow;

            _ = Task.Run(async () =>
            {
                try
                {
                    CertificateModel? activeVgCa = _certService.GetActiveVGCACertificate();
                    string activeThumbprint = activeVgCa?.Thumbprint ?? string.Empty;
                    string currentThumbprint = _currentActiveCert?.Thumbprint ?? string.Empty;

                    bool needReload = !string.Equals(activeThumbprint, currentThumbprint, StringComparison.OrdinalIgnoreCase);

                    if (needReload)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            LoadCertificatesAndVerifyVGCALogin();
                        });
                    }

                    // Tự động kiểm tra đồng bộ phân quyền từ đám mây trong nền (nếu có thay đổi)
                    var syncRes = await _vgcaAuth.SyncRosterFromCloudAsync();
                    if (syncRes.Success)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_vgcaAuth.IsLoggedIn && _vgcaAuth.CurrentTeacher != null)
                            {
                                OnVgcaLoginStateChanged(true, _vgcaAuth.CurrentTeacher);
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning($"Bỏ qua lỗi kiểm tra chứng thư khi kích hoạt cửa sổ: {ex.Message}");
                }
            });
        }

        private void BtnSyncVgcaCertificate_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatus("Đang đồng bộ và nhận diện chữ ký số từ hệ thống VGCA Virtual CSP...");
            LoadCertificatesAndVerifyVGCALogin();

            if (_currentActiveCert != null && _currentActiveCert.IsVGCACertificate)
            {
                if (_currentActiveCert.IsVirtualCspCertificate)
                {
                    var vcspInfo = VcspStatusService.CheckStatus();
                    if (vcspInfo.State == VcspState.NotRunning)
                    {
                        var launchChoice = MessageBox.Show(
                            $"Đã tìm thấy chứng thư Ban Cơ yếu: {_currentActiveCert.CommonName}\n\n" +
                            "⚠️ Ứng dụng VGCA Virtual CSP v2.0 chưa được mở trên máy tính.\n\n" +
                            "Bạn có muốn hệ thống tự động khởi động ứng dụng Virtual CSP ngay bây giờ không?",
                            "Khởi Động Virtual CSP", MessageBoxButton.YesNo, MessageBoxImage.Question);

                        if (launchChoice == MessageBoxResult.Yes)
                        {
                            VcspStatusService.LaunchVirtualCsp();
                            UpdateStatus("Đã khởi động Virtual CSP. Vui lòng đăng nhập trên cửa sổ Virtual CSP.");
                        }
                        return;
                    }
                }

                string teacherName = _vgcaAuth.CurrentTeacher?.FullName ?? _currentActiveCert.CommonName;
                string roleText = _vgcaAuth.CurrentTeacher?.CanSeal == true ? "🔴 Có quyền đóng dấu cơ quan" : "🔵 Ký số cá nhân giáo viên";
                MessageBox.Show(
                    $"Đã nhận diện thành công Chứng thư số Ban Cơ yếu Chính phủ!\n\n" +
                    $"• Chủ sở hữu: {teacherName}\n" +
                    $"• Chức vụ & Phân quyền: {roleText}\n" +
                    $"• Đơn vị: {_currentActiveCert.OrganizationUnit}\n" +
                    $"• Cơ quan cấp: {_currentActiveCert.IssuerCommonName}\n" +
                    $"• Thời hạn: {_currentActiveCert.NotAfter:dd/MM/yyyy} ({(_currentActiveCert.IsValidNow ? "Còn hiệu lực" : "Đã hết hạn")})\n\n" +
                    "Hệ thống đã tự động kích hoạt phiên làm việc và sẵn sàng ký số tài liệu.",
                    "Đồng Bộ Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    "Chưa phát hiện chứng thư Ban Cơ yếu Chính phủ có khóa riêng trong hệ thống Windows.\n\n" +
                    "• Nếu dùng Ký Từ Xa qua điện thoại: Vui lòng mở ứng dụng VGCA Virtual CSP trên máy tính.\n" +
                    "• Nếu dùng USB Token: Vui lòng cắm USB Token Ban Cơ Yếu vào máy tính.",
                    "Chưa Phát Hiện Chữ Ký Số", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateStatus("Chưa tìm thấy chứng thư Ban Cơ yếu hợp lệ trong kho Windows.");
            }
        }

        private void BtnRefreshCertificates_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatus("Đang quét lại danh sách chứng thư số...");
            LoadCertificatesAndVerifyVGCALogin();
        }

        private void BtnSaveCertPreference_Click(object sender, RoutedEventArgs e)
        {
            if (_currentActiveCert == null)
            {
                MessageBox.Show(
                    "Chưa chọn chứng thư số nào. Vui lòng chọn chứng thư từ danh sách trước khi lưu.",
                    "Chưa chọn chứng thư", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string mode = CertPreferenceService.DetectMode(_currentActiveCert);
            string modeLabel = mode switch
            {
                "USB_SEAL" => "USB Token con dấu nhà trường (đóng dấu pháp nhân)",
                "USB_PERSONAL" => "USB Token cá nhân BGH / giáo viên (ký số cá nhân)",
                _ => "VGCA Cloud / Virtual CSP (giáo viên ký qua điện thoại)"
            };

            var pref = new CertPreferenceModel
            {
                Thumbprint = _currentActiveCert.Thumbprint,
                Mode = mode,
                DisplayName = _currentActiveCert.CommonName,
                SavedAt = DateTime.UtcNow
            };
            CertPreferenceService.Save(pref);
            _certPreference = pref;

            UpdateStatus($"Đã lưu lựa chọn: {_currentActiveCert.CommonName} [{modeLabel}]");
            MessageBox.Show(
                $"Đã ghi nhớ chứng thư số:\n\n" +
                $"• Tên: {_currentActiveCert.CommonName}\n" +
                $"• Chế độ: {modeLabel}\n\n" +
                $"Lần sau khi quét hoặc khởi động lại ứng dụng, hệ thống sẽ tự động ưu tiên chứng thư này.",
                "Đã lưu lựa chọn chứng thư", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnLaunchVgcaManager_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatus("Đang kiểm tra và khởi động ứng dụng VGCA Virtual CSP của Ban Cơ Yếu...");

            // 1. ƯU TIÊN SỐ 1: Mở ứng dụng Virtual CSP Manager (vgca_vcsp_v2_mgr.exe) để giáo viên đăng nhập tài khoản/mật khẩu
            bool launchedVcsp = _certService.LaunchVcspManagerApp();
            if (launchedVcsp)
            {
                UpdateStatus("Đã mở VGCA Virtual CSP Manager của Ban Cơ Yếu Chính Phủ.");
                MessageBox.Show(
                    "Đã mở phần mềm VGCA Virtual CSP Manager của Ban Cơ Yếu Chính Phủ.\n\n" +
                    "Thầy/Cô vui lòng nhập Tên đăng nhập và Mật khẩu trên cửa sổ Virtual CSP để kích hoạt phiên ký số, sau đó quay lại phần mềm bấm nút 'Quét Token Cắm Sẵn'.",
                    "Đăng Nhập VGCA Virtual CSP", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 2. Nếu chưa cài Virtual CSP: Kiểm tra bộ cài đặt tích hợp sẵn VGCA_VCSPInstaller.exe
            string? bundledVcspInstaller = _certService.GetBundledVcspInstallerPath();
            if (!string.IsNullOrEmpty(bundledVcspInstaller) && File.Exists(bundledVcspInstaller))
            {
                var result = MessageBox.Show(
                    "Máy tính này chưa phát hiện phần mềm ký số từ xa VGCA Virtual CSP của Ban Cơ Yếu Chính Phủ.\n\n" +
                    "Bộ cài đặt chính thức 'VGCA Virtual CSP v2.0' (VGCA_VCSPInstaller.exe) đã được tích hợp sẵn trong ứng dụng.\n" +
                    "Thầy/Cô có muốn tiến hành cài đặt ngay bây giờ để đăng nhập tài khoản và mật khẩu chứng thư số không?",
                    "Cài Đặt VGCA Virtual CSP v2.0",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        UpdateStatus("Đang khởi chạy bộ cài đặt VGCA Virtual CSP v2.0...");
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = bundledVcspInstaller,
                            UseShellExecute = true
                        });
                        return;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Không thể khởi chạy bộ cài đặt VGCA Virtual CSP: {ex.Message}", "Lỗi Cài Đặt", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }

            // 3. Fallback: Thử mở VGCA Sign Tool (USB Token) nếu có
            bool launchedSignTool = _certService.LaunchVGCALoginApp();
            if (launchedSignTool)
            {
                UpdateStatus("Đã khởi động công cụ ký số VGCA Sign Tool của Ban Cơ Yếu Chính Phủ.");
                MessageBox.Show("Đã mở công cụ ký số VGCA Sign Tool của Ban Cơ Yếu Chính Phủ.\nSau khi cắm USB Token, vui lòng nhấn nút 'Quét Token Cắm Sẵn' trên phần mềm.",
                    "Công Cụ Ký Số Ban Cơ Yếu", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 4. Nếu chưa cài cả hai: Gợi ý cài đặt bộ công cụ VGCA Sign Tool (.msi)
            string? bundledMsi = _certService.GetBundledVgcaInstallerPath();
            if (!string.IsNullOrEmpty(bundledMsi) && File.Exists(bundledMsi))
            {
                var resultMsi = MessageBox.Show(
                    "Máy tính này chưa cài đặt bất kỳ phần mềm ký số nào của Ban Cơ Yếu Chính Phủ.\n\n" +
                    "Bạn có muốn cài đặt gói VGCA Sign Tool v3.0.6 (.msi) của Ban Cơ Yếu Chính Phủ không?",
                    "Cài Đặt Công Cụ VGCA",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (resultMsi == MessageBoxResult.Yes)
                {
                    try
                    {
                        UpdateStatus("Đang khởi chạy trình cài đặt VGCA Sign Tool v3.0.6...");
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "msiexec.exe",
                            Arguments = $"/i \"{bundledMsi}\"",
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Không thể khởi chạy bộ cài đặt VGCA: {ex.Message}", "Lỗi Cài Đặt", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            else
            {
                MessageBox.Show(
                    "Không tìm thấy công cụ ký số VGCA (Virtual CSP hoặc VGCA Sign Tool) trên máy tính này.\n\n" +
                    "Vui lòng cài đặt phần mềm ký số chính thức của Ban Cơ Yếu Chính Phủ để sử dụng tính năng ký số.",
                    "Thông Báo VGCA", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void MenuOpenVcspManager_Click(object sender, RoutedEventArgs e)
        {
            if (!_certService.LaunchVcspManagerApp())
            {
                MessageBox.Show(
                    "Chưa phát hiện phần mềm VGCA Virtual CSP Manager trên máy tính này.\n\n" +
                    "Vui lòng chọn tùy chọn 'Cài đặt VGCA Virtual CSP v2.0' từ menu chuột phải để cài đặt.",
                    "Virtual CSP Chưa Cài Đặt", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void MenuInstallVcsp_Click(object sender, RoutedEventArgs e)
        {
            string? installer = _certService.GetBundledVcspInstallerPath();
            if (!string.IsNullOrEmpty(installer) && File.Exists(installer))
            {
                try
                {
                    UpdateStatus("Đang khởi chạy trình cài đặt VGCA Virtual CSP v2.0...");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = installer,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi khởi chạy bộ cài đặt: {ex.Message}", "Lỗi Cài Đặt", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("Không tìm thấy tệp bộ cài đặt VGCA_VCSPInstaller.exe.", "Thiếu Tệp Cài Đặt", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MenuInstallVgcaSignTool_Click(object sender, RoutedEventArgs e)
        {
            string? msi = _certService.GetBundledVgcaInstallerPath();
            if (!string.IsNullOrEmpty(msi) && File.Exists(msi))
            {
                try
                {
                    UpdateStatus("Đang khởi chạy trình cài đặt VGCA Sign Tool v3.0.6 (.msi)...");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "msiexec.exe",
                        Arguments = $"/i \"{msi}\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi khởi chạy bộ cài đặt MSI: {ex.Message}", "Lỗi Cài Đặt", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("Không tìm thấy tệp bộ cài đặt VGCASignToolSetup.msi.", "Thiếu Tệp Cài Đặt", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MenuOpenVgcaSignTool_Click(object sender, RoutedEventArgs e)
        {
            if (!_certService.LaunchVGCALoginApp())
            {
                MessageBox.Show(
                    "Không tìm thấy phần mềm VGCA Sign Tool v3.0.6 trên máy tính này.",
                    "VGCA Sign Tool Chưa Cài Đặt", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnChangePasswordLocal_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Hệ thống tự động nhận diện danh tính và phân quyền qua chữ ký số VGCA. Không cần sử dụng mật khẩu ứng dụng riêng.",
                "Tự Động Nhận Diện VGCA", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void BtnSyncRosterCloud_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateStatus("Đang đồng bộ danh bạ và phân quyền từ máy chủ đám mây Firebase...");
                var res = await _vgcaAuth.SyncRosterFromCloudAsync();
                if (res.Success)
                {
                    if (_vgcaAuth.IsLoggedIn && _vgcaAuth.CurrentTeacher != null)
                    {
                        OnVgcaLoginStateChanged(true, _vgcaAuth.CurrentTeacher);
                        MessageBox.Show(
                            $"Đã đồng bộ thành công dữ liệu từ máy chủ đám mây!\n\n" +
                            $"• Thầy/Cô: {_vgcaAuth.CurrentTeacher.FullName}\n" +
                            $"• Chức vụ: {_vgcaAuth.CurrentTeacher.Title}\n" +
                            $"• Phân quyền đóng dấu: {(_vgcaAuth.CurrentTeacher.CanSeal ? "🔴 ĐƯỢC PHÂN QUYỀN ĐÓNG DẤU" : "🔵 Ký số cá nhân")}\n\n" +
                            $"Toàn bộ giao diện phân quyền Soạn thảo & Ký số (Tab 2) đã được cập nhật.",
                            "Đồng Bộ Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
                        UpdateStatus($"Đã đồng bộ thành công phân quyền cho {_vgcaAuth.CurrentTeacher.FullName}.");
                    }
                    else
                    {
                        MessageBox.Show($"Đã đồng bộ {res.Count} bản ghi giáo viên từ máy chủ đám mây.", "Đồng Bộ Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
                        UpdateStatus($"Đã đồng bộ {res.Count} bản ghi giáo viên từ đám mây.");
                    }
                }
                else
                {
                    MessageBox.Show($"Không thể đồng bộ từ máy chủ:\n{res.Message}", "Lỗi Đồng Bộ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    UpdateStatus($"Đồng bộ thất bại: {res.Message}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi trong quá trình đồng bộ:\n{ex.Message}", "Lỗi Hệ Thống", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus($"Lỗi đồng bộ: {ex.Message}");
            }
        }

        #region AUTO-UPDATE VÀ ĐỒNG BỘ PHIÊN BẢN (FIREBASE + GITHUB)

        private async Task CheckUpdateSilentAsync()
        {
            try
            {
                var result = await _updateService.CheckForUpdateAsync();
                if (result.Success && result.HasUpdate && result.Release != null)
                {
                    TxtUpdateStatusHeader.Text = $"Bản Mới v{result.LatestVersion} ✨";
                    BtnCheckUpdate.Background = new SolidColorBrush(Color.FromRgb(180, 83, 9)); // Amber highlight
                    UpdateStatus($"Phát hiện bản nâng cấp mới: v{result.LatestVersion} ({result.Release.Title}).");

                    if (result.IsMandatory)
                    {
                        var dialog = new UpdateDialog(result, _updateService) { Owner = this };
                        dialog.ShowDialog();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Lỗi kiểm tra cập nhật ngầm: {ex.Message}");
            }
        }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TxtUpdateStatusHeader.Text = "Đang kiểm tra...";
                BtnCheckUpdate.IsEnabled = false;
                UpdateStatus("Đang kết nối Firebase và kiểm tra bản phát hành mới...");

                var result = await _updateService.CheckForUpdateAsync();
                BtnCheckUpdate.IsEnabled = true;

                if (result.Success)
                {
                    if (result.HasUpdate && result.Release != null)
                    {
                        TxtUpdateStatusHeader.Text = $"Bản Mới v{result.LatestVersion} ✨";
                        BtnCheckUpdate.Background = new SolidColorBrush(Color.FromRgb(180, 83, 9));

                        var dialog = new UpdateDialog(result, _updateService) { Owner = this };
                        dialog.ShowDialog();
                    }
                    else
                    {
                        TxtUpdateStatusHeader.Text = "Mới Nhất ✔";
                        BtnCheckUpdate.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                        UpdateStatus($"Bạn đang sử dụng phiên bản mới nhất: v{_updateService.CurrentVersionString}.");
                        MessageBox.Show(
                            $"Bạn đang sử dụng phiên bản chuẩn mới nhất (v{_updateService.CurrentVersionString}).\n\nKhông có bản cập nhật nào mới hơn trên hệ thống nhà trường.",
                            "Hệ Thống Đã Cập Nhật", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {
                    TxtUpdateStatusHeader.Text = "Kiểm Tra Lại";
                    UpdateStatus($"Lỗi kiểm tra cập nhật: {result.Message}");
                    MessageBox.Show(
                        $"Không thể kiểm tra bản cập nhật từ máy chủ:\n\n{result.Message}\n\nVui lòng kiểm tra lại kết nối mạng Internet của nhà trường.",
                        "Lỗi Kết Nối", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                BtnCheckUpdate.IsEnabled = true;
                TxtUpdateStatusHeader.Text = "Kiểm Tra Lại";
                MessageBox.Show($"Lỗi kiểm tra cập nhật: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

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
            else
            {
                _currentActiveCert = null;
                ClearCertificateDetailsUI();
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

                // Nếu mẫu ở chế độ chỉ chữ (Mode == 0) hoặc profile dạng chèn số/ngày tháng, xóa ảnh con dấu
                if (profile.Mode == 0 || profile.ProfileType == 1 || profile.ProfileType == 2)
                {
                    ClearStampImage();
                }
                else if (!string.IsNullOrWhiteSpace(profile.ImagePath) && File.Exists(profile.ImagePath))
                {
                    SetAndPersistStampImage(profile.ImagePath, persistToVault: false);
                }
                else if (!string.IsNullOrWhiteSpace(profile.Image))
                {
                    _profileManager.EnsureProfileImagesRestored(new List<SignatureProfile> { profile });
                    if (!string.IsNullOrWhiteSpace(profile.ImagePath) && File.Exists(profile.ImagePath))
                    {
                        SetAndPersistStampImage(profile.ImagePath, persistToVault: false);
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

        private void SetAndPersistStampImage(string filePath, bool persistToVault = true)
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
                TxtTab2StampStatus.Text = $"Con dấu: {Path.GetFileName(filePath)}";
                BtnTab2SelectStamp.Content = "Đổi ảnh";

                // Tự động lưu trữ an toàn vào App Vault và Base64 trong Profile
                if (CmbProfiles.SelectedItem is SignatureProfile profile)
                {
                    if (persistToVault)
                    {
                        string vaultedPath = _profileManager.PersistStampImageToVault(profile, filePath);
                        profile.ImagePath = vaultedPath;
                        TxtStampImagePath.Text = vaultedPath;
                        TxtStampFullPath.Text = vaultedPath;
                    }
                    else
                    {
                        profile.ImagePath = filePath;
                    }
                    _profileManager.SaveProfiles(_profiles);

                    string? curCccd = _vgcaAuth.CurrentTeacher?.Cccd;
                    if (!string.IsNullOrWhiteSpace(curCccd))
                    {
                        _ = _profileManager.BackupProfilesToCloudAsync(curCccd, _profiles);
                    }
                }

                UpdateStatus($"Đã nạp và lưu trữ an toàn ảnh con dấu: {Path.GetFileName(filePath)}");
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
            TxtTab2StampStatus.Text = "Con dấu: Chưa chọn";
            BtnTab2SelectStamp.Content = "Chọn mẫu...";

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

                    string? curCccd = _vgcaAuth.CurrentTeacher?.Cccd;
                    if (!string.IsNullOrWhiteSpace(curCccd))
                    {
                        _ = _profileManager.BackupProfilesToCloudAsync(curCccd, _profiles);
                    }

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

        private async void BtnSyncCloudProfile_Click(object sender, RoutedEventArgs e)
        {
            string? currentCccd = _vgcaAuth.CurrentTeacher?.Cccd;
            if (string.IsNullOrWhiteSpace(currentCccd))
            {
                MessageBox.Show("Vui lòng cắm USB Token hoặc mở Virtual CSP để nhận diện tài khoản trước khi đồng bộ chữ ký với đám mây.", 
                    "Chưa Nhận Diện Chữ Ký", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                UpdateStatus("Đang kiểm tra và đồng bộ mẫu chữ ký với đám mây...");

                // Thử khôi phục từ Cloud trước nếu cục bộ chưa có ảnh hoặc có bản sao lưu trên Cloud
                var cloudProfiles = await _profileManager.RestoreProfilesFromCloudAsync(currentCccd);
                if (cloudProfiles != null && cloudProfiles.Count > 0 && cloudProfiles.Any(p => !string.IsNullOrEmpty(p.Image) || (!string.IsNullOrEmpty(p.ImagePath) && File.Exists(p.ImagePath))))
                {
                    _profiles = cloudProfiles;
                    CmbProfiles.ItemsSource = null;
                    CmbProfiles.ItemsSource = _profiles;
                    if (_profiles.Count > 0) CmbProfiles.SelectedIndex = 0;

                    UpdateStatus($"Đã khôi phục thành công {cloudProfiles.Count} mẫu chữ ký từ đám mây!");
                    MessageBox.Show($"Đã khôi phục và giải mã thành công {cloudProfiles.Count} mẫu chữ ký kèm ảnh con dấu từ đám mây an toàn!", 
                        "Đồng Bộ Đám Mây", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Nếu trên Cloud chưa có hoặc máy đã có sẵn ảnh -> Tiến hành sao lưu lên Cloud
                bool backedUp = await _profileManager.BackupProfilesToCloudAsync(currentCccd, _profiles);
                if (backedUp)
                {
                    UpdateStatus("Đã sao lưu toàn bộ mẫu chữ ký và ảnh con dấu lên đám mây an toàn (Mã hóa AES-256)!");
                    MessageBox.Show("Đã sao lưu toàn bộ mẫu chữ ký và ảnh con dấu lên đám mây an toàn (Mã hóa AES-256)!\nKhi cài lại máy tính hoặc sang máy mới, dữ liệu sẽ tự động khôi phục.", 
                        "Đồng Bộ Đám Mây", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Không thể sao lưu lên đám mây. Vui lòng kiểm tra kết nối mạng.", 
                        "Lỗi Đồng Bộ", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi trong quá trình đồng bộ đám mây:\n{ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnAddNewProfile_Click(object sender, RoutedEventArgs e)
        {
            var inputWindow = new Window
            {
                Title = "Tạo Mẫu Chữ Ký / Con Dấu Mới",
                Width = 440,
                Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252))
            };

            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock
            {
                Text = "Tên mẫu chữ ký / con dấu:",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var txtName = new TextBox
            {
                Text = $"Mẫu chữ ký {_profiles.Count + 1}",
                Padding = new Thickness(6),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 12)
            };
            stack.Children.Add(txtName);

            stack.Children.Add(new TextBlock
            {
                Text = "Loại mẫu:",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var cmbType = new ComboBox { Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 16) };
            cmbType.Items.Add(new ComboBoxItem { Content = "Ký Cá Nhân / Giáo Viên", Tag = 3, IsSelected = true });
            cmbType.Items.Add(new ComboBoxItem { Content = "Ký Phát Hành Cơ Quan (Con Dấu Đỏ)", Tag = 4 });
            cmbType.Items.Add(new ComboBoxItem { Content = "Ký Nháy / Ký Duyệt", Tag = 3 });
            cmbType.Items.Add(new ComboBoxItem { Content = "Chèn Số Văn Bản", Tag = 1 });
            cmbType.Items.Add(new ComboBoxItem { Content = "Chèn Ngày Tháng", Tag = 2 });
            stack.Children.Add(cmbType);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnCancel = new Button
            {
                Content = "Hủy Bỏ",
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 10, 0),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225))
            };
            btnCancel.Click += (_, _) => inputWindow.Close();

            var btnOk = new Button
            {
                Content = "Tạo Mẫu",
                Padding = new Thickness(16, 6, 16, 6),
                Background = new SolidColorBrush(Color.FromRgb(2, 132, 199)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold
            };
            btnOk.Click += (_, _) =>
            {
                string name = txtName.Text.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    MessageBox.Show("Vui lòng nhập tên cho mẫu chữ ký!", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int profileType = 3;
                if (cmbType.SelectedItem is ComboBoxItem item && item.Tag is int tagVal)
                {
                    profileType = tagVal;
                }

                int w = profileType == 4 ? 120 : (profileType == 3 ? 140 : 100);
                int h = profileType == 4 ? 120 : (profileType == 3 ? 70 : 40);
                int mode = profileType == 1 || profileType == 2 ? 0 : 1;

                var newProfile = new SignatureProfile
                {
                    Name = name,
                    ProfileType = profileType,
                    Mode = mode,
                    SignatureWidth = w,
                    SignatureHeight = h,
                    FontSize = 10,
                    ShowDate = profileType != 1,
                    ShowOrg = profileType != 1 && profileType != 2,
                    ShowLabel = profileType == 3 || profileType == 4
                };

                _profiles.Add(newProfile);
                _profileManager.SaveProfiles(_profiles);

                CmbProfiles.ItemsSource = null;
                CmbProfiles.ItemsSource = _profiles;
                CmbProfiles.SelectedItem = newProfile;

                inputWindow.Close();
                UpdateStatus($"Đã tạo mới mẫu chữ ký: {name}");
            };

            btnPanel.Children.Add(btnCancel);
            btnPanel.Children.Add(btnOk);
            stack.Children.Add(btnPanel);

            inputWindow.Content = stack;
            inputWindow.ShowDialog();
        }

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (CmbProfiles.SelectedItem is not SignatureProfile profile)
            {
                return;
            }

            if (_profiles.Count <= 1)
            {
                MessageBox.Show("Hệ thống yêu cầu phải duy trì ít nhất 01 mẫu chữ ký chuẩn!",
                    "Không Thể Xóa", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"Bạn có chắc chắn muốn xóa mẫu chữ ký '{profile.Name}' không?",
                "Xác Nhận Xóa Mẫu Chữ Ký", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _profiles.Remove(profile);
                _profileManager.SaveProfiles(_profiles);

                CmbProfiles.ItemsSource = null;
                CmbProfiles.ItemsSource = _profiles;
                CmbProfiles.SelectedIndex = 0;

                UpdateStatus($"Đã xóa mẫu chữ ký: {profile.Name}");
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
                UpdateSignAndSealButtonsState(false);
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
                HideLoadingOverlay();
                CloseWorkflowApprovalChoicePopup();
                CloseForwardWorkflowPopup();
                if (BtnForwardWorkflowReport != null)
                {
                    BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                }

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

        private void EnsurePopupElements()
        {
            if (_popupLoadingTitle == null && OverlayLoadingPopup?.Child is FrameworkElement root)
            {
                _popupLoadingTitle = root.FindName("TxtPopupLoadingTitle") as TextBlock;
                _popupLoadingMessage = root.FindName("TxtPopupLoadingMessage") as TextBlock;
                _popupLoadingIcon = root.FindName("TxtPopupLoadingIcon") as TextBlock;
                _btnCancelSigningOverlay = root.FindName("BtnCancelSigningOverlay") as Button;
            }
        }

        private void RepositionLoadingPopup()
        {
            if (OverlayLoadingPopup != null && OverlayLoadingPopup.IsOpen)
            {
                double offset = OverlayLoadingPopup.HorizontalOffset;
                OverlayLoadingPopup.HorizontalOffset = offset + 0.001;
                OverlayLoadingPopup.HorizontalOffset = offset;
            }
            if (OverlaySubmitWorkflowPopup != null && OverlaySubmitWorkflowPopup.IsOpen)
            {
                double offset = OverlaySubmitWorkflowPopup.HorizontalOffset;
                OverlaySubmitWorkflowPopup.HorizontalOffset = offset + 0.001;
                OverlaySubmitWorkflowPopup.HorizontalOffset = offset;
            }
        }

        /// <summary>
        /// Hiển thị hộp thoại trung tâm Loading Dialog với huy hiệu tĩnh chống lag và thanh tiến trình.
        /// Sử dụng WPF Popup tạo HWND độc lập giải quyết triệt để lỗi Airspace che khuất của WebView2.
        /// </summary>
        private void ShowLoadingOverlay(string title, string message, string icon = "📄", bool showCancelButton = false, string cancelButtonText = "Hủy chờ ký")
        {
            _loadingStartTime = DateTime.UtcNow;
            VGCASignToolPro.Services.AppLogger.Info($"Hiện loading overlay: {title} | {message}", "ShowLoadingOverlay");

            EnsurePopupElements();
            if (_popupLoadingTitle != null) _popupLoadingTitle.Text = title;
            if (_popupLoadingMessage != null) _popupLoadingMessage.Text = message;
            if (_popupLoadingIcon != null)
            {
                string glyph = icon switch
                {
                    "📄" or "📑" => "\uE8A5",
                    "📊" => "\uE9F9",
                    "⚡" => "\uE7E8",
                    "✍️" => "\uE8D7",
                    "🔴" => "\uEA3B",
                    "📥" => "\uE896",
                    _ => icon
                };
                _popupLoadingIcon.FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
                _popupLoadingIcon.Text = glyph;
            }

            if (_btnCancelSigningOverlay != null)
            {
                _btnCancelSigningOverlay.Content = cancelButtonText;
                _btnCancelSigningOverlay.Visibility = showCancelButton ? Visibility.Visible : Visibility.Collapsed;
            }

            // Giữ Placeholder màu xám nhạt (#F8FAFC) để che chắn nền của WebView2 trong suốt lúc tải
            if (PnlPreviewPlaceholder != null)
            {
                PnlPreviewPlaceholder.Visibility = Visibility.Visible;
            }

            if (OverlayLoadingModal != null)
            {
                OverlayLoadingModal.Visibility = Visibility.Visible;
            }

            if (OverlayLoadingPopup != null)
            {
                if (MainRootGrid != null)
                {
                    OverlayLoadingPopup.PlacementTarget = MainRootGrid;
                }
                OverlayLoadingPopup.IsOpen = true;
                RepositionLoadingPopup();
            }
        }

        private void BtnCancelSigningOverlay_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_signingCts != null && !_signingCts.IsCancellationRequested)
                {
                    _signingCts.Cancel();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Lỗi hủy ký: {ex.Message}");
            }

            // ĐÓNG NGAY HỘP THOẠI WINDOWS "VIRTUAL CSP" CỦA BAN CƠ YẾU
            VcspStatusService.DismissWaitingDialog();
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 6; i++)
                {
                    await Task.Delay(150);
                    VcspStatusService.DismissWaitingDialog();
                }
            });

            HideLoadingOverlay();
            SetProcessingBusyState(false);
            UpdateStatus("Đã hủy bỏ thao tác ký số theo yêu cầu của người dùng.");
        }

        /// <summary>
        /// Ẩn hộp thoại Loading Dialog sau khi tài liệu đã được nạp/kết xuất hoàn tất.
        /// </summary>
        private void HideLoadingOverlay()
        {
            VGCASignToolPro.Services.AppLogger.Info("Ẩn loading overlay.", "HideLoadingOverlay");
            if (OverlayLoadingPopup != null)
            {
                OverlayLoadingPopup.IsOpen = false;
            }
            if (OverlayLoadingModal != null)
            {
                OverlayLoadingModal.Visibility = Visibility.Collapsed;
            }
            _pdfRenderCompletionSource?.TrySetResult(true);
            _excelRenderCompletionSource?.TrySetResult(true);
            _pdfRenderCompletionSource = null;
            _excelRenderCompletionSource = null;
        }

        /// <summary>
        /// Cập nhật nội dung thông điệp tiến trình trong hộp thoại Loading Dialog theo thời gian thực.
        /// </summary>
        private void UpdateLoadingOverlay(string message, string? title = null, string? icon = null)
        {
            Dispatcher.Invoke(() =>
            {
                EnsurePopupElements();
                if (_popupLoadingMessage != null) _popupLoadingMessage.Text = message;
                if (title != null && _popupLoadingTitle != null) _popupLoadingTitle.Text = title;
                if (icon != null && _popupLoadingIcon != null) _popupLoadingIcon.Text = icon;
                if (TxtStatusBar != null) TxtStatusBar.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
            });
        }

        /// <summary>
        /// Ẩn hộp thoại Loading Dialog với cơ chế chờ tối thiểu (mặc định 600ms) để người dùng kịp nhìn thấy phản hồi trực quan.
        /// </summary>
        private async Task HideLoadingOverlayAsync(int minDisplayMs = 600)
        {
            try
            {
                if (minDisplayMs > 0 && _loadingStartTime != DateTime.MinValue)
                {
                    TimeSpan elapsed = DateTime.UtcNow - _loadingStartTime;
                    int remainingMs = minDisplayMs - (int)elapsed.TotalMilliseconds;
                    if (remainingMs > 0)
                    {
                        await Task.Delay(remainingMs);
                    }
                }
            }
            catch (Exception) { }

            HideLoadingOverlay();
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
                if (BtnStampOfficial != null) BtnStampOfficial.IsEnabled = false;
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

            VGCASignToolPro.Services.AppLogger.Info($"Bắt đầu nạp tệp Word: {wordPath}", "LoadWordDocumentAsync");
            ShowLoadingOverlay("Đang mở file, vui lòng chờ...", $"Đang nạp và tối ưu tài liệu Word: {Path.GetFileName(wordPath)}...", "📄");
            SetProcessingBusyState(true, $"Đang nạp và tối ưu tài liệu Word: {Path.GetFileName(wordPath)}...");

            _originalWordFilePath = wordPath;
            _originalExcelFilePath = null;
            _currentWorkingPdfPath = null;
            _isWordModified = false;
            BorderSignSuccessBanner.Visibility = Visibility.Collapsed;

            TxtCurrentFileName.Text = Path.GetFileName(wordPath);
            PnlEditingToolbar.Visibility = Visibility.Collapsed;
            PnlExcelToolbarControls.Visibility = Visibility.Collapsed;
            BtnCloseCurrentFile.Visibility = Visibility.Visible;

            try
            {
                long fileSizeKb = new FileInfo(wordPath).Length / 1024;
                TxtCurrentFileStatus.Text = $"Đang tối ưu cấu trúc tài liệu ({fileSizeKb:N0} KB) sang bản PDF A4 chuẩn ký số...";

                // Tự động chuyển đổi Word sang bản PDF vector chuẩn Nghị định 30/2020/NĐ-CP (Động cơ kép: Native Word / DocIO)
                string pdfPath = await _wordConverter.ConvertWordToPdfAsync(wordPath);
                if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                {
                    throw new InvalidOperationException("Không thể tạo bản PDF chuẩn từ tệp Word đã chọn.");
                }

                _currentWorkingPdfPath = pdfPath;

                // Đảm bảo ẩn WordEditorControl và kích hoạt WebView2 PDF Viewer sắc nét 100%
                if (WordEditorControl != null) WordEditorControl.Visibility = Visibility.Collapsed;
                if (PdfPreviewControl != null) PdfPreviewControl.Visibility = Visibility.Visible;

                // Nạp file PDF chuẩn vào WebView2 bằng Engine tương tác
                await DisplayPdfInInteractiveViewerAsync(pdfPath);

                TxtCurrentFileName.Text = Path.GetFileName(wordPath);
                TxtCurrentFileStatus.Text = "Tài liệu Word đã được nạp thành công dưới dạng PDF A4 chuẩn in ấn. Bạn có thể kéo thả con dấu và ký ngay.";
                BtnConvertToPdfAndReadySign.IsEnabled = false;
                BtnCloseCurrentFile.Visibility = Visibility.Visible;

                // Mở khóa toàn bộ các nút Ký Cá Nhân, Đóng Dấu, Chèn Vị Trí Ký tức thì
                UpdateSignAndSealButtonsState(true);
                UpdateStatus($"Đã nạp văn bản Word: {Path.GetFileName(wordPath)} (Sẵn sàng ký số).");
                VGCASignToolPro.Services.AppLogger.Info($"Nạp tệp Word thành công: {wordPath}", "LoadWordDocumentAsync");
            }
            catch (Exception ex)
            {
                HideLoadingOverlay();
                TxtCurrentFileStatus.Text = "Nạp tài liệu thất bại.";
                _originalWordFilePath = null;
                _currentWorkingPdfPath = null;
                if (WordEditorControl != null) WordEditorControl.Visibility = Visibility.Collapsed;
                if (PdfPreviewControl != null) PdfPreviewControl.Visibility = Visibility.Visible;
                BtnCloseCurrentFile.Visibility = Visibility.Collapsed;
                VGCASignToolPro.Services.AppLogger.Error($"Lỗi nạp tài liệu Word: {wordPath}", ex, "LoadWordDocumentAsync");
                MessageBox.Show($"Không thể nạp tài liệu Word:\n\n{ex.Message}\n\nVui lòng kiểm tra tệp tin có đang bị khóa bởi ứng dụng khác không.", 
                    "Lỗi Nạp Tài Liệu", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                HideLoadingOverlay();
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

            ShowLoadingOverlay("Đang mở file, vui lòng chờ...", $"Đang phân tích cấu trúc bảng tính Excel: {Path.GetFileName(excelPath)}...", "📊");
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

                    _excelRenderCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    // Mã hóa an toàn tham số bằng JsonConvert để triệt tiêu hoàn toàn lỗ hổng JS Injection
                    string jsonBase64 = Newtonsoft.Json.JsonConvert.SerializeObject(base64);
                    string jsonFileName = Newtonsoft.Json.JsonConvert.SerializeObject(Path.GetFileName(excelPath));
                    string script = $"loadExcelFromBase64({jsonBase64}, {jsonFileName});";
                    if (PdfPreviewControl?.CoreWebView2 != null)
                    {
                        await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync(script);
                    }

                    // Chờ sự kiện excelLoaded từ webview2 hoặc timeout an toàn (3500ms)
                    await Task.WhenAny(_excelRenderCompletionSource.Task, Task.Delay(3500));

                    if (PnlPreviewPlaceholder != null)
                    {
                        PnlPreviewPlaceholder.Visibility = Visibility.Collapsed;
                    }
                }

                TxtCurrentFileStatus.Text = "Đang ở chế độ xem & điều chỉnh bảng tính. Bạn có thể click đúp vào ô để sửa dữ liệu, sau đó bấm 'Chuyển Qua PDF Để Ký'.";
                BtnConvertToPdfAndReadySign.IsEnabled = true;
                UpdateStatus($"Đã nạp bảng tính Excel: {Path.GetFileName(excelPath)}. Sẵn sàng xem và điều chỉnh.");
            }
            catch (Exception ex)
            {
                HideLoadingOverlay();
                TxtCurrentFileStatus.Text = "Nạp bảng tính thất bại.";
                _originalExcelFilePath = null;
                BtnCloseCurrentFile.Visibility = Visibility.Collapsed;
                VGCASignToolPro.Services.AppLogger.Error($"Lỗi nạp bảng tính Excel: {excelPath}", ex, "LoadExcelDocumentAsync");
                MessageBox.Show($"Không thể hiển thị bảng tính Excel:\n{ex.Message}", "Lỗi Nạp Bảng Tính", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                await HideLoadingOverlayAsync(600);
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

            VGCASignToolPro.Services.AppLogger.Info($"Bắt đầu nạp tệp PDF: {pdfPath}", "LoadPdfDocumentAsync");
            ShowLoadingOverlay("Đang mở file, vui lòng chờ...", $"Đang nạp và kết xuất trang tài liệu PDF: {Path.GetFileName(pdfPath)}...", "📑");
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
                UpdateSignAndSealButtonsState(true);
                BtnCloseCurrentFile.Visibility = Visibility.Visible;

                await DisplayPdfInInteractiveViewerAsync(pdfPath);

                UpdateStatus($"Đã nạp tệp PDF trực tiếp: {Path.GetFileName(pdfPath)}");
                VGCASignToolPro.Services.AppLogger.Info($"Nạp tệp PDF thành công: {pdfPath}", "LoadPdfDocumentAsync");
            }
            catch (Exception ex)
            {
                HideLoadingOverlay();
                TxtCurrentFileStatus.Text = "Nạp tệp PDF thất bại.";
                _currentWorkingPdfPath = null;
                BtnCloseCurrentFile.Visibility = Visibility.Collapsed;
                VGCASignToolPro.Services.AppLogger.Error($"Lỗi nạp PDF: {pdfPath}", ex, "LoadPdfDocumentAsync");
                MessageBox.Show($"Không thể hiển thị tài liệu PDF:\n{ex.Message}", "Lỗi Nạp PDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                HideLoadingOverlay();
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

            ShowLoadingOverlay("Đang chuyển Excel thành PDF, vui lòng chờ...", "Đang xuất bảng tính Excel sang PDF chuẩn vector in ấn A4...", "⚡");
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
                UpdateSignAndSealButtonsState(true);
                BtnCloseCurrentFile.Visibility = Visibility.Visible;
                UpdateStatus($"Đã xuất tệp PDF bảng tính hoàn chỉnh: {Path.GetFileName(outputPdfPath)}");

                await HideLoadingOverlayAsync(600);
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
                await HideLoadingOverlayAsync(0);
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

            if (!string.IsNullOrWhiteSpace(_currentWorkingPdfPath) && File.Exists(_currentWorkingPdfPath))
            {
                MessageBox.Show("Tài liệu đã ở định dạng PDF chuẩn sẵn sàng ký số.\n\nBạn có thể bấm '📍 Chèn Vị Trí Ký' để kéo thả con dấu, sau đó bấm '✍️ Ký Cá Nhân' hoặc '🔴 Đóng dấu'.",
                    "Đã Sẵn Sàng Ký Số", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(_originalWordFilePath) || !File.Exists(_originalWordFilePath))
            {
                MessageBox.Show("Vui lòng tải tệp Word lên trước khi chuyển đổi.", "Chưa có file", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Kiểm tra xem người dùng có thực hiện chỉnh sửa nội dung văn bản trên WordEditorControl hay không
            bool isModified = _isWordModified;

            ShowLoadingOverlay("Đang chuyển Word thành PDF, vui lòng chờ...", "Đang trích xuất nội dung văn bản và xuất sang PDF chuẩn Nghị định 30/2020/NĐ-CP...", "⚡");
            SetProcessingBusyState(true, "Bắt đầu chuyển đổi Word sang PDF...");
            PrgConvertLoading.Visibility = Visibility.Visible;
            TxtCurrentFileStatus.Text = "Đang trích xuất nội dung văn bản và xuất sang PDF chuẩn Nghị định 30/2020/NĐ-CP...";

            try
            {
                string pdfPath;

                // NGUYÊN TẮC BẢO VỆ TÀI LIỆU GỐC (ZERO DATA LOSS / READ-ONLY INVARIANT):
                // Tuyệt đối không bao giờ ghi đè lên tệp Word gốc trên máy người dùng.
                if (isModified && WordEditorControl != null && WordEditorControl.Visibility == Visibility.Visible)
                {
                    using MemoryStream docStream = new MemoryStream();
                    WordEditorControl.Save(docStream, Syncfusion.Windows.Controls.RichTextBoxAdv.FormatType.Docx);
                    docStream.Position = 0;
                    pdfPath = await _wordConverter.ConvertWordStreamToPdfAsync(docStream, _originalWordFilePath);
                }
                else
                {
                    // Chuyển đổi trực tiếp từ file Word gốc bằng động cơ kép (Word Native / Syncfusion DocIO)
                    pdfPath = await _wordConverter.ConvertWordToPdfAsync(_originalWordFilePath);
                }

                string originalWordFileName = Path.GetFileName(_originalWordFilePath);
                _currentWorkingPdfPath = pdfPath;
                _isWordModified = false;
                PnlEditingToolbar.Visibility = Visibility.Collapsed;

                // Chuyển chế độ hiển thị từ WordEditorControl sang WebView2 PDF Viewer
                if (WordEditorControl != null) WordEditorControl.Visibility = Visibility.Collapsed;
                if (PdfPreviewControl != null) PdfPreviewControl.Visibility = Visibility.Visible;

                // Nạp file PDF chuẩn vào WebView2 bằng Engine tương tác
                await DisplayPdfInInteractiveViewerAsync(pdfPath);

                TxtCurrentFileName.Text = originalWordFileName;
                TxtCurrentFileStatus.Text = "Tài liệu Word đã được nạp thành công dưới dạng PDF A4 chuẩn in ấn. Sẵn sàng ký số.";
                BtnConvertToPdfAndReadySign.IsEnabled = false;
                UpdateSignAndSealButtonsState(true);
                BtnCloseCurrentFile.Visibility = Visibility.Visible;
                UpdateStatus($"Đã xuất tệp PDF hoàn chỉnh: {Path.GetFileName(pdfPath)}");

                string saveNotice = isModified
                    ? $"• Tệp Word gốc ({originalWordFileName}) được bảo toàn nguyên vẹn 100% trên máy tính.\n• Toàn bộ nội dung đã điều chỉnh được chuyển trực tiếp vào tệp PDF phục vụ ký số.\n"
                    : $"• Tệp PDF được tạo trực tiếp từ bản Word gốc chuẩn xác 100% không xê dịch nét kẻ.\n";

                await HideLoadingOverlayAsync(600);
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
                await HideLoadingOverlayAsync(0);
                PrgConvertLoading.Visibility = Visibility.Collapsed;
                SetProcessingBusyState(false);
            }
        }

        private async Task DisplayPdfInInteractiveViewerAsync(string pdfPath)
        {
            if (!_isWebViewInitialized || !File.Exists(pdfPath) || PdfPreviewControl?.CoreWebView2 == null)
            {
                TxtCurrentFileStatus.Text = "Trình xem tương tác chưa sẵn sàng.";
                VGCASignToolPro.Services.AppLogger.Warn("DisplayPdfInInteractiveViewerAsync: Trình xem tương tác chưa sẵn sàng.", "DisplayPdfInInteractiveViewerAsync");
                return;
            }

            try
            {
                VGCASignToolPro.Services.AppLogger.Info($"Bắt đầu nạp PDF vào trình xem tương tác: {pdfPath}", "DisplayPdfInInteractiveViewerAsync");
                FileInfo fileInfo = new FileInfo(pdfPath);
                // Giới hạn kích thước file an toàn cho preview (150MB)
                const long maxFileSize = 150 * 1024 * 1024;
                if (fileInfo.Length > maxFileSize)
                {
                    RollbackViewerUiState("Tệp PDF quá lớn (>150MB) để nạp vào trình xem tương tác.");
                    return;
                }

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
                    VGCASignToolPro.Services.AppLogger.Info("Điều hướng WebView2 tới https://vgcaviewer.local/pdf_viewer.html", "DisplayPdfInInteractiveViewerAsync");
                    var tcs = new TaskCompletionSource<bool>();
                    EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs>? navHandler = null;
                    navHandler = (s, e) =>
                    {
                        try
                        {
                            if (PdfPreviewControl?.CoreWebView2 != null)
                            {
                                PdfPreviewControl.CoreWebView2.NavigationCompleted -= navHandler;
                            }
                        }
                        catch (Exception)
                        {
                            // Bỏ qua lỗi gỡ handler nếu control đã unbind
                        }
                        tcs.TrySetResult(e.IsSuccess);
                    };

                    PdfPreviewControl.CoreWebView2.NavigationCompleted += navHandler;
                    PdfPreviewControl.CoreWebView2.Navigate("https://vgcaviewer.local/pdf_viewer.html");

                    var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000));
                    if (completedTask != tcs.Task)
                    {
                        try
                        {
                            if (PdfPreviewControl?.CoreWebView2 != null)
                            {
                                PdfPreviewControl.CoreWebView2.NavigationCompleted -= navHandler;
                            }
                        }
                        catch (Exception ex)
                        {
                            VGCASignToolPro.Services.AppLogger.Warn($"Lỗi gỡ NavigationCompleted handler: {ex.Message}", "DisplayPdfInInteractiveViewerAsync");
                        }
                        RollbackViewerUiState("Lỗi: Quá thời gian chờ tải trình xem PDF.");
                        return;
                    }

                    bool navSuccess = await tcs.Task;
                    if (!navSuccess)
                    {
                        VGCASignToolPro.Services.AppLogger.Error("Không thể điều hướng tới https://vgcaviewer.local/pdf_viewer.html", null, "DisplayPdfInInteractiveViewerAsync");
                        RollbackViewerUiState("Lỗi: Không thể điều hướng tới trang xem PDF.");
                        return;
                    }
                }

                if (PdfPreviewControl == null || PdfPreviewControl.CoreWebView2 == null)
                {
                    RollbackViewerUiState("Lỗi: WebView2 runtime không khả dụng.");
                    return;
                }

                // Đảm bảo DOM và script loadPdfFromUrl / loadPdfFromBase64 đã sẵn sàng
                bool isScriptReady = false;
                for (int i = 0; i < 30; i++)
                {
                    if (PdfPreviewControl?.CoreWebView2 == null) break;
                    string checkScript = await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync("typeof loadPdfFromBase64 === 'function' && typeof loadPdfFromUrl === 'function'");
                    if (checkScript == "true")
                    {
                        isScriptReady = true;
                        break;
                    }
                    await Task.Delay(100);
                }

                if (!isScriptReady || PdfPreviewControl?.CoreWebView2 == null)
                {
                    VGCASignToolPro.Services.AppLogger.Error("Trình xem PDF không tải được engine hiển thị sau 3 giây chờ", null, "DisplayPdfInInteractiveViewerAsync");
                    RollbackViewerUiState("Lỗi: Trình xem PDF không tải được engine hiển thị.");
                    return;
                }

                _pdfRenderCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // CƠ CHẾ NẠP KÉP AN TOÀN TUYỆT ĐỐI (DUAL-MODE PDF INGESTION):
                // 1. Tệp tin <= 25MB: Nạp trực tiếp qua Base64 (0% lỗi mạng, 0% CORS, phản hồi trong <100ms)
                // 2. Tệp tin > 25MB: Nạp qua Virtual URL Streaming
                if (fileInfo.Length <= 25 * 1024 * 1024)
                {
                    VGCASignToolPro.Services.AppLogger.Info($"Nạp tài liệu PDF qua bộ nhớ Base64 ({fileInfo.Length / 1024:N0} KB)...", "DisplayPdfInInteractiveViewerAsync");
                    byte[] fileBytes = await File.ReadAllBytesAsync(pdfPath);
                    string base64Data = Convert.ToBase64String(fileBytes);
                    string safeJson = Newtonsoft.Json.JsonConvert.SerializeObject(base64Data);
                    await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync($"loadPdfFromBase64({safeJson});");
                }
                else
                {
                    VGCASignToolPro.Services.AppLogger.Info($"Nạp tài liệu lớn qua Virtual URL Streaming ({fileInfo.Length / (1024 * 1024):N1} MB)...", "DisplayPdfInInteractiveViewerAsync");
                    string previewCacheDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "VGCASignToolPro", "PreviewCache");
                    if (!Directory.Exists(previewCacheDir))
                    {
                        Directory.CreateDirectory(previewCacheDir);
                    }

                    string previewFileName = $"doc_{Guid.NewGuid():N}.pdf";
                    string destPdf = Path.Combine(previewCacheDir, previewFileName);

                    await Task.Run(() =>
                    {
                        try
                        {
                            File.Copy(pdfPath, destPdf, true);
                        }
                        catch (Exception copyEx)
                        {
                            VGCASignToolPro.Services.AppLogger.Warn($"Lỗi copy cache PDF: {copyEx.Message}. Sao chép bằng FileShare.ReadWrite.", "DisplayPdfInInteractiveViewerAsync");
                            using var srcStream = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            using var dstStream = new FileStream(destPdf, FileMode.Create, FileAccess.Write, FileShare.None);
                            srcStream.CopyTo(dstStream);
                        }

                        try
                        {
                            var di = new DirectoryInfo(previewCacheDir);
                            foreach (var f in di.GetFiles("doc_*.pdf"))
                            {
                                if (f.FullName != destPdf && (DateTime.UtcNow - f.LastWriteTimeUtc).TotalMinutes > 5)
                                {
                                    try { f.Delete(); } catch { }
                                }
                            }
                        }
                        catch { }
                    });

                    string pdfUrl = $"https://vgcacontent.local/{previewFileName}";
                    string safeJsonUrl = Newtonsoft.Json.JsonConvert.SerializeObject(pdfUrl);
                    await PdfPreviewControl.CoreWebView2.ExecuteScriptAsync($"loadPdfFromUrl({safeJsonUrl});");
                }

                // Chờ sự kiện pdfLoaded (thường chỉ mất ~100-150ms) hoặc timeout an toàn (6000ms)
                var renderCompleted = await Task.WhenAny(_pdfRenderCompletionSource.Task, Task.Delay(6000));
                if (renderCompleted != _pdfRenderCompletionSource.Task)
                {
                    VGCASignToolPro.Services.AppLogger.Warn("Chờ pdfLoaded timeout 6000ms, tiếp tục mở giao diện.", "DisplayPdfInInteractiveViewerAsync");
                }
                else
                {
                    VGCASignToolPro.Services.AppLogger.Info("Tài liệu PDF đã render thành công trên WebView2.", "DisplayPdfInInteractiveViewerAsync");
                }

                // Chỉ kích hoạt UI thành công sau khi PDF đã được nạp hoàn tất
                if (PnlPreviewPlaceholder != null)
                {
                    PnlPreviewPlaceholder.Visibility = Visibility.Collapsed;
                }
                _hasUserPlacedStamp = false;
                BtnInsertSignatureStamp.IsEnabled = true;
            }
            catch (ObjectDisposedException)
            {
                RollbackViewerUiState("Lỗi: Trình xem đã bị hủy trong quá trình xử lý.");
            }
            catch (Exception ex)
            {
                VGCASignToolPro.Services.AppLogger.Error("Lỗi nạp PDF tương tác", ex, "DisplayPdfInInteractiveViewerAsync");
                RollbackViewerUiState($"Lỗi tải tài liệu: {ex.Message}");
            }
        }

        private void RollbackViewerUiState(string errorMessage)
        {
            HideLoadingOverlay();
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
                if (!string.IsNullOrWhiteSpace(_originalWordFilePath) && File.Exists(_originalWordFilePath))
                {
                    var confirmConvert = MessageBox.Show(
                        "Văn bản đang ở định dạng Word (.docx).\n\n" +
                        "Để thực hiện ký số chuẩn PAdES theo quy chuẩn Ban Cơ yếu Chính phủ, hệ thống cần xuất sang bản PDF A4 hoàn chỉnh.\n\n" +
                        "Bạn có muốn tự động chuyển đổi sang PDF và tiếp tục ký ngay không?",
                        "Tự Động Xuất PDF Để Ký Số", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (confirmConvert == MessageBoxResult.Yes)
                    {
                        await ConvertWordToPdfAsync();
                        if (string.IsNullOrWhiteSpace(_currentWorkingPdfPath) || !File.Exists(_currentWorkingPdfPath))
                        {
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(_originalExcelFilePath) && File.Exists(_originalExcelFilePath))
                {
                    var confirmConvert = MessageBox.Show(
                        "Bảng tính đang ở định dạng Excel (.xlsx).\n\n" +
                        "Để thực hiện ký số chuẩn PAdES, hệ thống cần xuất sang bản PDF hoàn chỉnh.\n\n" +
                        "Bạn có muốn tự động chuyển đổi sang PDF và tiếp tục ký ngay không?",
                        "Tự Động Xuất PDF Để Ký Số", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (confirmConvert == MessageBoxResult.Yes)
                    {
                        await ConvertExcelToPdfAsync();
                        if (string.IsNullOrWhiteSpace(_currentWorkingPdfPath) || !File.Exists(_currentWorkingPdfPath))
                        {
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    MessageBox.Show("Vui lòng mở file Word, Excel hoặc nạp file PDF trước khi ký.",
                        "Chưa Có Tài Liệu", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // 1. Tự động nhận diện thiết bị chữ ký số VGCA đang kết nối (USB Token hoặc Virtual CSP)
            CertificateModel? activeVgCa = _certService.GetActiveVGCACertificate();
            if (activeVgCa == null || !activeVgCa.HasPrivateKey)
            {
                // Thử quét lại kho chứng thư Windows Store
                _certificates = _certService.LoadCertificates(onlyWithPrivateKey: true);
                activeVgCa = _certService.GetActiveVGCACertificate();
            }

            if (activeVgCa == null || !activeVgCa.HasPrivateKey)
            {
                var retryChoice = MessageBox.Show(
                    "Chưa phát hiện thiết bị chữ ký số Ban Cơ Yếu đang hoạt động!\n\n" +
                    "• Nếu dùng Ký Từ Xa (VGCA Mobile): Vui lòng mở ứng dụng VGCA Virtual CSP trên máy tính.\n" +
                    "• Nếu dùng USB Token: Vui lòng cắm USB Token Ban Cơ Yếu vào máy tính.\n\n" +
                    "Bạn có muốn hệ thống hỗ trợ mở ứng dụng VGCA Virtual CSP ngay bây giờ không?",
                    "Yêu Cầu Kết Nối Chữ Ký Số VGCA", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (retryChoice == MessageBoxResult.Yes)
                {
                    VcspStatusService.LaunchVirtualCsp();
                    UpdateStatus("Đã yêu cầu mở VGCA Virtual CSP. Vui lòng đăng nhập trên Virtual CSP và thử ký lại.");
                }
                return;
            }

            // 2. Tự động kích hoạt danh tính người ký hiện hành đồng bộ theo chứng thư phát hiện
            _currentActiveCert = activeVgCa;
            _vgcaAuth.SetCurrentActiveIdentity(activeVgCa);

            // 3. Nếu là Virtual CSP: tự khởi động ngầm nếu ứng dụng chưa chạy (không hiện hộp thoại hỏi làm phiền hoặc treo ứng dụng)
            if (activeVgCa.IsVirtualCspCertificate)
            {
                if (!VcspStatusService.IsProcessRunning() && VcspStatusService.IsInstalled())
                {
                    VcspStatusService.LaunchVirtualCsp();
                    UpdateStatus("Đã khởi động nền VGCA Virtual CSP.");
                }
            }

            // CHỐT CHẶN BẮT BUỘC: QUY TRÌNH CHÈN ẢNH KÝ RỒI MỚI ĐƯỢC KÝ
            if (!_hasUserPlacedStamp)
            {
                MessageBox.Show(
                    "Theo đúng quy trình ký số văn bản điện tử, bạn cần chèn ảnh chữ ký và đặt vào đúng vị trí trên trang tài liệu trước khi thực hiện ký!\n\n" +
                    "👉 Vui lòng nhấn nút '📍 Chèn Vị Trí Ký' trên thanh công cụ để đưa khung chữ ký lên trang văn bản, kéo thả căn chỉnh vị trí ký rồi mới bấm '✍️ Ký Cá Nhân'.",
                    "Yêu Cầu Chèn Vị Trí Chữ Ký Trước Khi Ký", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            // Xác định tệp hình ảnh con dấu (từ ô nhập hoặc từ Profile đã lưu)
            string? stampPath = TxtStampImagePath.Text.Trim();
            SignatureProfile? selectedProfile = CmbProfiles.SelectedItem as SignatureProfile;

            // Nếu người dùng chọn chế độ chỉ chữ (Mode == 0) hoặc profile không dùng ảnh, không dùng ảnh con dấu
            if (CmbDisplayMode != null && CmbDisplayMode.SelectedIndex == 2)
            {
                stampPath = null;
            }
            else if (string.IsNullOrWhiteSpace(stampPath) || stampPath.StartsWith("Ảnh con dấu") || !File.Exists(stampPath))
            {
                if (selectedProfile != null && !string.IsNullOrWhiteSpace(selectedProfile.ImagePath) && File.Exists(selectedProfile.ImagePath))
                {
                    stampPath = selectedProfile.ImagePath;
                }
                else
                {
                    stampPath = null;
                }
            }

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

            string baseName = !string.IsNullOrWhiteSpace(_originalWordFilePath)
                ? Path.GetFileNameWithoutExtension(_originalWordFilePath)
                : Path.GetFileNameWithoutExtension(_currentWorkingPdfPath);
            if (baseName.EndsWith("_converted", StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName.Substring(0, baseName.Length - "_converted".Length);
            }
            string outputPath = Path.Combine(dir, $"{baseName}_SIGNED.pdf");

            SigningRequest request = new SigningRequest
            {
                SourcePdfPath = _currentWorkingPdfPath,
                OutputPdfPath = outputPath,
                Certificate = _currentActiveCert!,
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

            _signingCts = new System.Threading.CancellationTokenSource();
            ShowLoadingOverlay(
                "Đang tiến hành ký số...",
                "Đang kết nối chứng thư số Ban Cơ Yếu và gửi yêu cầu xác thực tới điện thoại (Virtual CSP) hoặc USB Token...",
                "✍️",
                showCancelButton: true,
                cancelButtonText: "✕ Hủy Chờ Ký");
            SetProcessingBusyState(true, "Đang tiến hành ký số văn bản chuẩn PAdES LTV...");

            Action<string> onSignStatus = msg =>
            {
                UpdateLoadingOverlay(msg);
                UpdateStatus(msg);
            };

            _pdfSigner.StatusChanged += onSignStatus;

            try
            {
                string signedPath = await _pdfSigner.SignPdfAsync(request, _signingCts.Token);
                if (_signingCts.IsCancellationRequested)
                {
                    return;
                }
                _lastSignedPdfPath = signedPath;

                // Nạp file đã ký vào Interactive Viewer để người dùng thẩm định kết quả trực quan
                await DisplayPdfInInteractiveViewerAsync(signedPath);

                TxtCurrentFileName.Text = Path.GetFileName(signedPath);
                TxtCurrentFileStatus.Text = "✅ VĂN BẢN ĐÃ ĐƯỢC KÝ SỐ THÀNH CÔNG VỚI CHỨNG THƯ BAN CƠ YẾU CHÍNH PHỦ.";

                // Hiển thị Banner Thành công và các nút thao tác nhanh trên giao diện
                BorderSignSuccessBanner.Visibility = Visibility.Visible;
                BtnCloseCurrentFile.Visibility = Visibility.Visible;
                TxtSignedSummaryNotice.Text = $"Tệp đã ký: {Path.GetFileName(signedPath)} • Đã nhúng chữ ký số và dấu thời gian TSA Ban Cơ yếu.";

                // ẨN NGAY HỘP THOẠI LOADING OVERLAY VÀ GIẢI PHÓNG TRẠNG THÁI BUSY TRƯỚC KHI HIỆN BẤT KỲ DIALOG/POPUP NÀO
                await HideLoadingOverlayAsync(400);
                SetProcessingBusyState(false);

                // Cập nhật trạng thái các nút workflow trên banner
                if (!string.IsNullOrEmpty(_activeWorkflowReportId))
                {
                    if (_isWorkflowSealingMode)
                    {
                        BtnCommitWorkflowSeal.Visibility = Visibility.Visible;
                        BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                        if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                        BtnSubmitWorkflowReport.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        BtnCommitWorkflowApproval.Visibility = Visibility.Visible;
                        if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Visible;
                        BtnCommitWorkflowSeal.Visibility = Visibility.Collapsed;
                        BtnSubmitWorkflowReport.Visibility = Visibility.Collapsed;

                        ShowWorkflowApprovalChoicePopup();
                    }
                }
                else
                {
                    BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                    BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                    if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                    BtnCommitWorkflowSeal.Visibility = Visibility.Collapsed;

                    MessageBoxResult choice = MessageBox.Show(
                        $"Ký số văn bản thành công!\n\nTệp tin đã lưu tại:\n{signedPath}\n\nChữ ký số đã được nhúng chính xác với dấu thời gian TSA Ban Cơ yếu.\n\nBạn có muốn mở hộp thoại trình ký báo cáo này lên Ban Giám hiệu / Tổ trưởng ngay không?",
                        "Ký Số Thành Công", MessageBoxButton.YesNo, MessageBoxImage.Information);

                    if (choice == MessageBoxResult.Yes)
                    {
                        BtnSubmitWorkflowReport_Click(this, new RoutedEventArgs());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("Đã hủy bỏ thao tác ký số theo yêu cầu của người dùng.");
            }
            catch (Exception ex)
            {
                await HideLoadingOverlayAsync(0);
                SetProcessingBusyState(false);
                if (_signingCts?.IsCancellationRequested == true)
                {
                    UpdateStatus("Đã hủy bỏ thao tác ký số theo yêu cầu của người dùng.");
                    return;
                }
                bool isVc = request.Certificate?.IsVirtualCspCertificate == true;
                var (errTitle, errMsg, errDetails) = VcspStatusService.ExplainSigningError(ex, isVc);
                MessageBox.Show(errMsg, errTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateStatus($"Lỗi ký số: {errTitle}");
            }
            finally
            {
                _pdfSigner.StatusChanged -= onSignStatus;
                await HideLoadingOverlayAsync(0);
                SetProcessingBusyState(false);
                UpdateSignAndSealButtonsState(true);
                _signingCts?.Dispose();
                _signingCts = null;
            }
        }

        private void UpdateSignAndSealButtonsState(bool isPdfReady)
        {
            bool isLoggedIn = _vgcaAuth.IsLoggedIn;
            bool isAnyDocLoaded = isPdfReady || !string.IsNullOrWhiteSpace(_originalWordFilePath) || !string.IsNullOrWhiteSpace(_originalExcelFilePath);
            bool hasSealRight = _vgcaAuth.CurrentTeacher?.CanSeal == true;

            bool canSign = isAnyDocLoaded && isLoggedIn;
            bool canSeal = isAnyDocLoaded && isLoggedIn && hasSealRight;

            if (BtnSignCurrentDocument != null)
            {
                BtnSignCurrentDocument.IsEnabled = canSign;
                if (!isLoggedIn)
                {
                    BtnSignCurrentDocument.ToolTip = "Vui lòng đăng nhập tài khoản để ký tài liệu.";
                }
                else if (!isAnyDocLoaded)
                {
                    BtnSignCurrentDocument.ToolTip = "Vui lòng mở tệp Word, Excel hoặc PDF trước khi ký.";
                }
                else if (!isPdfReady)
                {
                    BtnSignCurrentDocument.ToolTip = "Ký số văn bản (Hệ thống sẽ tự động chuyển đổi Word/Excel sang PDF để ký số chuẩn PAdES).";
                }
                else
                {
                    BtnSignCurrentDocument.ToolTip = "Tiến hành ký số văn bản PDF chuẩn PAdES Ban Cơ yếu Chính phủ.";
                }
            }

            if (BtnInsertSignatureStamp != null) BtnInsertSignatureStamp.IsEnabled = isPdfReady && isLoggedIn;

            if (BtnStampOfficial != null)
            {
                BtnStampOfficial.IsEnabled = canSeal;

                if (!isLoggedIn)
                {
                    BtnStampOfficial.ToolTip = "Vui lòng đăng nhập tài khoản để sử dụng tính năng đóng dấu cơ quan.";
                }
                else if (!hasSealRight)
                {
                    BtnStampOfficial.ToolTip = "Tài khoản của bạn chưa được cấp quyền đóng dấu pháp nhân cơ quan. Vui lòng liên hệ Ban Giám hiệu / Quản trị viên để cấp quyền.";
                }
                else if (!isAnyDocLoaded)
                {
                    BtnStampOfficial.ToolTip = "Vui lòng mở tệp Word, Excel hoặc PDF trước khi đóng dấu.";
                }
                else if (!isPdfReady)
                {
                    BtnStampOfficial.ToolTip = "Đóng dấu cơ quan (Hệ thống sẽ tự động chuyển đổi sang PDF và đóng dấu pháp nhân USB Token).";
                }
                else
                {
                    BtnStampOfficial.ToolTip = "Đóng dấu pháp nhân nhà trường bằng USB Token Ban Cơ yếu (Dành cho Ban Giám hiệu / Văn thư)";
                }
            }
        }

        private async void BtnStampOfficial_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusyProcessing)
            {
                MessageBox.Show("Hệ thống đang xử lý tác vụ khác, vui lòng chờ trong giây lát...",
                    "Hệ Thống Đang Bận", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(_currentWorkingPdfPath) || !File.Exists(_currentWorkingPdfPath))
            {
                if (!string.IsNullOrWhiteSpace(_originalWordFilePath) && File.Exists(_originalWordFilePath))
                {
                    var confirmConvert = MessageBox.Show(
                        "Văn bản đang ở định dạng Word (.docx).\n\n" +
                        "Để thực hiện đóng dấu cơ quan chuẩn PAdES theo quy chuẩn Ban Cơ yếu Chính phủ, hệ thống cần xuất sang bản PDF A4 hoàn chỉnh.\n\n" +
                        "Bạn có muốn tự động chuyển đổi sang PDF và tiếp tục đóng dấu ngay không?",
                        "Tự Động Xuất PDF Để Đóng Dấu", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (confirmConvert == MessageBoxResult.Yes)
                    {
                        await ConvertWordToPdfAsync();
                        if (string.IsNullOrWhiteSpace(_currentWorkingPdfPath) || !File.Exists(_currentWorkingPdfPath))
                        {
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(_originalExcelFilePath) && File.Exists(_originalExcelFilePath))
                {
                    var confirmConvert = MessageBox.Show(
                        "Bảng tính đang ở định dạng Excel (.xlsx).\n\n" +
                        "Để thực hiện đóng dấu cơ quan chuẩn PAdES theo quy định, hệ thống cần xuất sang bản PDF hoàn chỉnh.\n\n" +
                        "Bạn có muốn tự động chuyển đổi sang PDF và tiếp tục đóng dấu ngay không?",
                        "Tự Động Xuất PDF Để Đóng Dấu", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (confirmConvert == MessageBoxResult.Yes)
                    {
                        await ConvertExcelToPdfAsync();
                        if (string.IsNullOrWhiteSpace(_currentWorkingPdfPath) || !File.Exists(_currentWorkingPdfPath))
                        {
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    MessageBox.Show("Vui lòng mở file Word, Excel hoặc nạp file PDF trước khi đóng dấu.",
                        "Chưa Có Tài Liệu", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // 1. Kiểm tra phân quyền giáo viên
            if (_vgcaAuth.CurrentTeacher == null || !_vgcaAuth.CurrentTeacher.CanSeal)
            {
                MessageBox.Show(
                    "Tài khoản của bạn chưa được phân quyền 'Đóng Dấu Cơ Quan'.\n\n" +
                    "Theo quy chế quản lý con dấu và văn thư (Nghị định 30/2020/NĐ-CP), chỉ Ban Giám hiệu, Văn thư hoặc người được phân quyền quản trị thiết bị USB Token mới được phép thực hiện chức năng này.\n\n" +
                    "Vui lòng liên hệ BGH / Quản trị viên để được cấp quyền.",
                    "Chưa Cấp Quyền Đóng Dấu Cơ Quan", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 2. Quét tìm chứng thư số tổ chức trong thiết bị USB Token Ban Cơ yếu
            CertificateModel? orgCert = _certService.GetOrganizationTokenCertificate();
            if (orgCert == null || !orgCert.HasPrivateKey)
            {
                var retryChoice = MessageBox.Show(
                    "Không tìm thấy thiết bị USB Token Ban Cơ yếu chứa chứng thư số con dấu của Nhà trường!\n\n" +
                    "Vui lòng kiểm tra lại:\n" +
                    "1. Thiết bị USB Token (chứa chứng thư pháp nhân Trường học) đã được cắm chắc chắn vào cổng USB máy tính.\n" +
                    "2. Đèn tín hiệu trên USB Token đã sáng.\n" +
                    "3. Đã cài đặt driver Token Manager của Ban Cơ yếu Chính phủ (bit4id TokenME EVO).\n\n" +
                    "Sau khi đã cắm thiết bị USB Token con dấu vào máy tính, bạn có muốn thử lại ngay không?",
                    "Chưa Phát Hiện USB Token Ký Số Nhà Trường", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (retryChoice == MessageBoxResult.Yes)
                {
                    _certificates = _certService.LoadCertificates(onlyWithPrivateKey: true);
                    orgCert = _certService.GetOrganizationTokenCertificate();
                }

                if (orgCert == null || !orgCert.HasPrivateKey)
                {
                    UpdateStatus("Hủy thao tác đóng dấu do chưa có thiết bị USB Token con dấu.");
                    return;
                }
            }

            // 3. Hộp thoại xác nhận đóng dấu pháp nhân
            MessageBoxResult confirm = MessageBox.Show(
                $"XÁC NHẬN ĐÓNG DẤU PHÁP NHÂN NHÀ TRƯỜNG:\n\n" +
                $"• Đơn vị phát hành: {orgCert.CommonName}\n" +
                $"• Nhà cấp chứng thư: {orgCert.IssuerCommonName}\n" +
                $"• Số Serial Token: {orgCert.SerialNumber}\n" +
                $"• Người thực hiện: {_vgcaAuth.CurrentTeacher.FullName} ({_vgcaAuth.CurrentTeacher.Title})\n\n" +
                $"Hệ thống sẽ tiến hành đóng dấu đỏ pháp nhân cơ quan lên tài liệu bằng thiết bị USB Token.\n" +
                $"Bạn có chắc chắn muốn thực hiện không?",
                "Xác Nhận Đóng Dấu Cơ Quan (USB Token)", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            // 4. Tìm kiếm mẫu Profile con dấu đỏ nhà trường (ưu tiên ProfileType == 4 hoặc tên có 'Dấu Đỏ' / 'Cơ Quan')
            SignatureProfile? sealProfile = _profiles.FirstOrDefault(p => p.ProfileType == 4)
                ?? _profiles.FirstOrDefault(p => p.Name.Contains("Dấu Đỏ", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Cơ Quan", StringComparison.OrdinalIgnoreCase));

            if (sealProfile == null)
            {
                sealProfile = new SignatureProfile
                {
                    Name = "Ký Phát Hành Cơ Quan (Dấu Đỏ)",
                    ProfileType = 4,
                    Mode = 1,
                    SignatureWidth = 120,
                    SignatureHeight = 120,
                    FontSize = 9,
                    ShowDate = true,
                    ShowOrg = true,
                    ShowLabel = true
                };
                _profiles.Add(sealProfile);
                _profileManager.SaveProfiles(_profiles);
            }

            // Kiểm tra ảnh con dấu đỏ
            string? sealImagePath = sealProfile.ImagePath;
            if (string.IsNullOrWhiteSpace(sealImagePath) || !File.Exists(sealImagePath))
            {
                if (!string.IsNullOrWhiteSpace(sealProfile.Image))
                {
                    _profileManager.EnsureProfileImagesRestored(new List<SignatureProfile> { sealProfile });
                    sealImagePath = sealProfile.ImagePath;
                }
            }

            if (string.IsNullOrWhiteSpace(sealImagePath) || !File.Exists(sealImagePath))
            {
                var pickImageChoice = MessageBox.Show(
                    "Mẫu 'Ký Phát Hành Cơ Quan (Dấu Đỏ)' hiện chưa có hình ảnh con dấu đỏ.\n\n" +
                    "Bạn có muốn chọn tệp hình ảnh con dấu đỏ (.png) ngay bây giờ không?",
                    "Chưa Chọn Ảnh Con Dấu Đỏ", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (pickImageChoice == MessageBoxResult.Yes)
                {
                    var dlg = new OpenFileDialog
                    {
                        Title = "Chọn hình ảnh con dấu đỏ nhà trường (.png, .jpg)",
                        Filter = "Hình ảnh (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|Tất cả tệp (*.*)|*.*",
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    };
                    if (dlg.ShowDialog() == true)
                    {
                        sealImagePath = _profileManager.PersistStampImageToVault(sealProfile, dlg.FileName);
                        sealProfile.ImagePath = sealImagePath;
                        _profileManager.SaveProfiles(_profiles);
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            // CHỐT CHẶN BẮT BUỘC: QUY TRÌNH CHÈN VỊ TRÍ CON DẤU RỒI MỚI ĐƯỢC ĐÓNG DẤU
            if (!_hasUserPlacedStamp)
            {
                MessageBox.Show(
                    "Theo đúng quy trình đóng dấu pháp nhân, bạn cần chèn con dấu và đặt vào đúng vị trí trên trang tài liệu trước khi thực hiện đóng dấu!\n\n" +
                    "👉 Vui lòng nhấn nút '📍 Chèn Vị Trí Ký' trên thanh công cụ để đưa khung con dấu lên trang văn bản, kéo thả căn chỉnh vị trí đóng dấu rồi mới bấm '🔴 Đóng dấu'.",
                    "Yêu Cầu Chèn Vị Trí Con Dấu Trước Khi Đóng Dấu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 5. Xác định tọa độ đóng dấu (ưu tiên vị trí người dùng kéo thả trên Tab 2)
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
                width = sealProfile.SignatureWidth > 0 ? sealProfile.SignatureWidth : 120;
                height = sealProfile.SignatureHeight > 0 ? sealProfile.SignatureHeight : 120;
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

            string baseName = !string.IsNullOrWhiteSpace(_originalWordFilePath)
                ? Path.GetFileNameWithoutExtension(_originalWordFilePath)
                : Path.GetFileNameWithoutExtension(_currentWorkingPdfPath);
            if (baseName.EndsWith("_converted", StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName.Substring(0, baseName.Length - "_converted".Length);
            }
            string outputPath = Path.Combine(dir, $"{baseName}_SEALED.pdf");

            SigningRequest request = new SigningRequest
            {
                SourcePdfPath = _currentWorkingPdfPath,
                OutputPdfPath = outputPath,
                Certificate = orgCert,
                PageNumber = pageNumber,
                X = coordX,
                Y = coordY,
                Width = width,
                Height = height,
                Reason = "Ký phát hành văn bản cơ quan (Nghị định 30/2020/NĐ-CP)",
                SignerDisplayName = orgCert.CommonName,
                StampImagePath = sealImagePath,
                Profile = sealProfile,
                UseTSA = ChkUseTSA.IsChecked == true,
                TSAUrl = "http://ca.gov.vn/tsa"
            };

            _signingCts = new System.Threading.CancellationTokenSource();
            ShowLoadingOverlay(
                "Đang đóng dấu pháp nhân...",
                $"Đang kết nối USB Token và đóng dấu pháp nhân {orgCert.CommonName}...\nVui lòng nhập mã PIN Token nếu được yêu cầu.",
                "🔴",
                showCancelButton: true,
                cancelButtonText: "✕ Hủy Chờ Ký");
            SetProcessingBusyState(true, $"Đang kết nối USB Token và đóng dấu pháp nhân {orgCert.CommonName}...");

            Action<string> onSealStatus = msg =>
            {
                UpdateLoadingOverlay(msg);
                UpdateStatus(msg);
            };

            _pdfSigner.StatusChanged += onSealStatus;

            try
            {
                string signedPath = await _pdfSigner.SignPdfAsync(request, _signingCts.Token);
                if (_signingCts.IsCancellationRequested)
                {
                    return;
                }
                _lastSignedPdfPath = signedPath;

                // Nạp file đã đóng dấu vào Interactive Viewer
                await DisplayPdfInInteractiveViewerAsync(signedPath);

                TxtCurrentFileName.Text = Path.GetFileName(signedPath);
                TxtCurrentFileStatus.Text = $"✅ VĂN BẢN ĐÃ ĐƯỢC ĐÓNG DẤU PHÁP NHÂN THÀNH CÔNG VỚI USB TOKEN: {orgCert.CommonName}";

                BorderSignSuccessBanner.Visibility = Visibility.Visible;
                BtnCloseCurrentFile.Visibility = Visibility.Visible;
                TxtSignedSummaryNotice.Text = $"Tệp đã đóng dấu: {Path.GetFileName(signedPath)} • Đã nhúng con dấu pháp nhân cơ quan và chứng thư số Ban Cơ yếu.";

                // ẨN NGAY HỘP THOẠI LOADING OVERLAY VÀ GIẢI PHÓNG TRẠNG THÁI BUSY TRƯỚC KHI HIỆN BẤT KỲ DIALOG/POPUP NÀO
                await HideLoadingOverlayAsync(400);
                SetProcessingBusyState(false);

                if (_isWorkflowSealingMode && !string.IsNullOrEmpty(_activeWorkflowReportId))
                {
                    BtnCommitWorkflowSeal.Visibility = Visibility.Visible;
                    BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                    if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                    BtnSubmitWorkflowReport.Visibility = Visibility.Collapsed;

                    MessageBoxResult choice = MessageBox.Show(
                        $"Đóng dấu pháp nhân cơ quan thành công!\n\n" +
                        $"• Đơn vị: {orgCert.CommonName}\n" +
                        $"• Tệp tin đã lưu tại:\n{signedPath}\n\n" +
                        $"Bạn có muốn nộp báo cáo đã đóng dấu hoàn tất lên Kho Toàn Trường ngay không?",
                        "Đóng Dấu Cơ Quan Thành Công", MessageBoxButton.YesNo, MessageBoxImage.Information);

                    if (choice == MessageBoxResult.Yes)
                    {
                        BtnCommitWorkflowSeal_Click(this, new RoutedEventArgs());
                    }
                }
                else
                {
                    BtnCommitWorkflowSeal.Visibility = Visibility.Collapsed;
                    MessageBox.Show(
                        $"Đóng dấu pháp nhân cơ quan thành công!\n\n" +
                        $"• Đơn vị: {orgCert.CommonName}\n" +
                        $"• Số Serial Token: {orgCert.SerialNumber}\n" +
                        $"• Tệp tin đã lưu tại:\n{signedPath}\n\n" +
                        $"Văn bản đã được bảo toàn tính toàn vẹn và có giá trị pháp lý phát hành theo Nghị định 30/2020/NĐ-CP.",
                        "Đóng Dấu Cơ Quan Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("Đã hủy bỏ thao tác đóng dấu theo yêu cầu của người dùng.");
            }
            catch (Exception ex)
            {
                await HideLoadingOverlayAsync(0);
                SetProcessingBusyState(false);
                if (_signingCts?.IsCancellationRequested == true)
                {
                    UpdateStatus("Đã hủy bỏ thao tác đóng dấu theo yêu cầu của người dùng.");
                    return;
                }
                System.Diagnostics.Trace.TraceError($"Lỗi đóng dấu cơ quan bằng USB Token: {ex}");
                bool isVc = request.Certificate?.IsVirtualCspCertificate == true;
                var (errTitle, errMsg, errDetails) = VcspStatusService.ExplainSigningError(ex, isVc);
                MessageBox.Show(errMsg, errTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateStatus($"Lỗi đóng dấu: {errTitle}");
            }
            finally
            {
                _pdfSigner.StatusChanged -= onSealStatus;
                await HideLoadingOverlayAsync(0);
                SetProcessingBusyState(false);
                UpdateSignAndSealButtonsState(true);
                _signingCts?.Dispose();
                _signingCts = null;
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

        private HashSet<string> GetHiddenReportIds(string cccd)
        {
            var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(cccd)) return hidden;

            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VGCASignToolPro");
                Directory.CreateDirectory(dir);
                string filePath = Path.Combine(dir, $"hidden_reports_{cccd}.json");
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath, Encoding.UTF8);
                    var list = JsonConvert.DeserializeObject<List<string>>(json);
                    if (list != null)
                    {
                        foreach (var id in list)
                        {
                            if (!string.IsNullOrWhiteSpace(id)) hidden.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Lỗi đọc hidden reports: {ex.Message}");
            }
            return hidden;
        }

        private void AddHiddenReportId(string cccd, string reportId)
        {
            if (string.IsNullOrWhiteSpace(cccd) || string.IsNullOrWhiteSpace(reportId)) return;

            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VGCASignToolPro");
                Directory.CreateDirectory(dir);
                string filePath = Path.Combine(dir, $"hidden_reports_{cccd}.json");
                var hidden = GetHiddenReportIds(cccd);
                hidden.Add(reportId);
                string json = JsonConvert.SerializeObject(hidden.ToList(), Formatting.Indented);
                File.WriteAllText(filePath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi ghi hidden reports: {ex}");
            }
        }

        public async Task RefreshWorkflowDataAsync()
        {
            try
            {
                string myCccd = _vgcaAuth.CurrentTeacher?.Cccd ?? string.Empty;

                // Tự động đồng bộ danh sách giáo viên mới nhất từ đám mây (nếu có cập nhật từ web quản trị)
                // Áp dụng bộ đệm 60s để tránh gửi request HTTP thừa thãi gây lag khi người dùng thao tác danh sách
                if (DateTime.UtcNow - _lastRosterSyncTime > TimeSpan.FromSeconds(60))
                {
                    try
                    {
                        await _vgcaAuth.SyncRosterFromCloudAsync();
                        _lastRosterSyncTime = DateTime.UtcNow;
                        PopulateWorkflowApprovers(myCccd);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.TraceWarning($"Bỏ qua lỗi tự động đồng bộ roster: {ex.Message}");
                    }
                }
                else
                {
                    PopulateWorkflowApprovers(myCccd);
                }

                if (string.IsNullOrEmpty(myCccd))
                {
                    _realtimeWatcher.Stop();
                    LvInboxReports.ItemsSource = null;
                    LvOutboxReports.ItemsSource = null;
                    LvReturnedReports.ItemsSource = null;
                    LvArchiveReports.ItemsSource = null;
                    LvWaitingSealReports.ItemsSource = null;
                    TxtCountInbox.Text = "0";
                    TxtCountOutbox.Text = "0";
                    TxtCountReturned.Text = "0";
                    TxtCountCompleted.Text = "0";
                    TxtCountWaitingSeal.Text = "0";
                    TabItemWaitingSeal.Visibility = Visibility.Collapsed;
                    BorderWaitingSealCount.Visibility = Visibility.Collapsed;
                    return;
                }

                // Tự động khởi động kênh lắng nghe thời gian thực cho giáo viên hiện tại
                _realtimeWatcher.Start(myCccd);

                var all = await _workflowService.GetAllReportsAsync();
                _allReports = all;
                var hiddenIds = GetHiddenReportIds(myCccd);

                // 1. Inbox: Chờ tôi duyệt (người duyệt là tôi và trạng thái còn hiệu lực)
                var inbox = all.Where(r => 
                    string.Equals(r.ApproverCccd, myCccd, StringComparison.OrdinalIgnoreCase) &&
                    (r.Status == "WAITING_OPEN" || r.Status == "ACQUIRED_LOCKED" || r.Status == "LOCK_TIMEOUT")
                ).ToList();

                // 2. Outbox: Tôi đã gửi hoặc đã tham gia ký duyệt/chuyển tiếp
                var outbox = all.Where(r =>
                    (string.Equals(r.SenderCccd, myCccd, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(r.SignedBy, myCccd, StringComparison.OrdinalIgnoreCase) ||
                     (r.SignersHistory != null && r.SignersHistory.Any(s => string.Equals(s, myCccd, StringComparison.OrdinalIgnoreCase)))) &&
                    r.Status != "RETURNED" &&
                    !hiddenIds.Contains(r.ReportId)
                ).ToList();

                // 3. Returned: Báo cáo trả về (chỉ hiển thị các báo cáo bị trả về cho tôi)
                var returned = all.Where(r =>
                    string.Equals(r.SenderCccd, myCccd, StringComparison.OrdinalIgnoreCase) &&
                    r.Status == "RETURNED" &&
                    !hiddenIds.Contains(r.ReportId)
                ).OrderByDescending(r => r.ReturnedAt).ToList();

                for (int i = 0; i < returned.Count; i++)
                {
                    returned[i].OrderIndex = i + 1; // Số thứ tự: 1, 2, 3...
                }

                // 4. Archive: Toàn trường đã hoàn tất
                var archive = all.Where(r => r.Status == "SIGNED_COMMITTED").ToList();

                // 5. WaitingSeal: Báo cáo chờ đóng dấu (Dành cho tài khoản có quyền CanSeal)
                bool canSeal = _vgcaAuth.CurrentTeacher?.CanSeal ?? false;
                List<ReportModel> waitingSeal = new();
                if (canSeal)
                {
                    waitingSeal = all.Where(r => r.Status == "WAITING_SEAL").OrderByDescending(r => r.UpdatedAt).ToList();
                    for (int i = 0; i < waitingSeal.Count; i++)
                    {
                        waitingSeal[i].OrderIndex = i + 1;
                    }
                }

                var prevInboxId = (LvInboxReports.SelectedItem as ReportModel)?.ReportId;
                var prevOutboxId = (LvOutboxReports.SelectedItem as ReportModel)?.ReportId;
                var prevReturnedId = (LvReturnedReports.SelectedItem as ReportModel)?.ReportId;
                var prevArchiveId = (LvArchiveReports.SelectedItem as ReportModel)?.ReportId;
                var prevWaitingSealId = (LvWaitingSealReports.SelectedItem as ReportModel)?.ReportId;

                LvInboxReports.ItemsSource = inbox;
                LvOutboxReports.ItemsSource = outbox;
                LvReturnedReports.ItemsSource = returned;
                LvArchiveReports.ItemsSource = archive;

                if (canSeal)
                {
                    LvWaitingSealReports.ItemsSource = waitingSeal;
                    TabItemWaitingSeal.Visibility = Visibility.Visible;
                    BorderWaitingSealCount.Visibility = Visibility.Visible;
                    TxtCountWaitingSeal.Text = waitingSeal.Count.ToString();

                    if (waitingSeal.Count > 0)
                    {
                        LvWaitingSealReports.SelectedItem = waitingSeal.FirstOrDefault(x => x.ReportId == prevWaitingSealId) ?? waitingSeal[0];
                    }
                }
                else
                {
                    LvWaitingSealReports.ItemsSource = null;
                    TabItemWaitingSeal.Visibility = Visibility.Collapsed;
                    BorderWaitingSealCount.Visibility = Visibility.Collapsed;
                    TxtCountWaitingSeal.Text = "0";
                }

                // Tự động khôi phục lựa chọn hoặc chọn dòng đầu tiên để người dùng bấm nút là mở được ngay
                if (inbox.Count > 0)
                {
                    LvInboxReports.SelectedItem = inbox.FirstOrDefault(x => x.ReportId == prevInboxId) ?? inbox[0];
                }
                if (outbox.Count > 0)
                {
                    LvOutboxReports.SelectedItem = outbox.FirstOrDefault(x => x.ReportId == prevOutboxId) ?? outbox[0];
                }
                if (returned.Count > 0)
                {
                    LvReturnedReports.SelectedItem = returned.FirstOrDefault(x => x.ReportId == prevReturnedId) ?? returned[0];
                }
                if (archive.Count > 0)
                {
                    LvArchiveReports.SelectedItem = archive.FirstOrDefault(x => x.ReportId == prevArchiveId) ?? archive[0];
                }

                TxtCountInbox.Text = inbox.Count.ToString();
                TxtCountOutbox.Text = outbox.Count.ToString();
                TxtCountReturned.Text = returned.Count.ToString();
                TxtCountCompleted.Text = archive.Count.ToString();

                if (inbox.Count > 0)
                {
                    _ = Task.Run(() =>
                    {
                        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi làm mới dữ liệu workflow: {ex}");
            }
        }

        /// <summary>
        /// Làm mới dữ liệu báo cáo trong im lặng (Silent Real-time Sync)
        /// Không hiện overlay loading che màn hình, tự động phát hiện số lượng thay đổi để phát âm thanh & cập nhật huy hiệu
        /// </summary>
        public async Task SilentRefreshWorkflowDataAsync()
        {
            try
            {
                string myCccd = _vgcaAuth.CurrentTeacher?.Cccd ?? string.Empty;
                if (string.IsNullOrEmpty(myCccd)) return;

                int prevReturnedCount = 0;
                int.TryParse(TxtCountReturned.Text, out prevReturnedCount);
                int prevInboxCount = 0;
                int.TryParse(TxtCountInbox.Text, out prevInboxCount);
                int prevWaitingSealCount = 0;
                int.TryParse(TxtCountWaitingSeal.Text, out prevWaitingSealCount);

                var all = await _workflowService.GetAllReportsAsync();
                _allReports = all;
                var hiddenIds = GetHiddenReportIds(myCccd);

                var inbox = all.Where(r => 
                    string.Equals(r.ApproverCccd, myCccd, StringComparison.OrdinalIgnoreCase) &&
                    (r.Status == "WAITING_OPEN" || r.Status == "ACQUIRED_LOCKED" || r.Status == "LOCK_TIMEOUT")
                ).ToList();

                var outbox = all.Where(r =>
                    string.Equals(r.SenderCccd, myCccd, StringComparison.OrdinalIgnoreCase) &&
                    r.Status != "RETURNED" &&
                    !hiddenIds.Contains(r.ReportId)
                ).ToList();

                var returned = all.Where(r =>
                    string.Equals(r.SenderCccd, myCccd, StringComparison.OrdinalIgnoreCase) &&
                    r.Status == "RETURNED" &&
                    !hiddenIds.Contains(r.ReportId)
                ).OrderByDescending(r => r.ReturnedAt).ToList();

                for (int i = 0; i < returned.Count; i++)
                {
                    returned[i].OrderIndex = i + 1;
                }

                var archive = all.Where(r => r.Status == "SIGNED_COMMITTED").ToList();

                bool canSeal = _vgcaAuth.CurrentTeacher?.CanSeal ?? false;
                List<ReportModel> waitingSeal = new();
                if (canSeal)
                {
                    waitingSeal = all.Where(r => r.Status == "WAITING_SEAL").OrderByDescending(r => r.UpdatedAt).ToList();
                    for (int i = 0; i < waitingSeal.Count; i++)
                    {
                        waitingSeal[i].OrderIndex = i + 1;
                    }
                }

                var prevInboxId = (LvInboxReports.SelectedItem as ReportModel)?.ReportId;
                var prevOutboxId = (LvOutboxReports.SelectedItem as ReportModel)?.ReportId;
                var prevReturnedId = (LvReturnedReports.SelectedItem as ReportModel)?.ReportId;
                var prevArchiveId = (LvArchiveReports.SelectedItem as ReportModel)?.ReportId;
                var prevWaitingSealId = (LvWaitingSealReports.SelectedItem as ReportModel)?.ReportId;

                LvInboxReports.ItemsSource = inbox;
                LvOutboxReports.ItemsSource = outbox;
                LvReturnedReports.ItemsSource = returned;
                LvArchiveReports.ItemsSource = archive;

                if (canSeal)
                {
                    LvWaitingSealReports.ItemsSource = waitingSeal;
                    TabItemWaitingSeal.Visibility = Visibility.Visible;
                    BorderWaitingSealCount.Visibility = Visibility.Visible;
                    TxtCountWaitingSeal.Text = waitingSeal.Count.ToString();

                    if (waitingSeal.Count > 0)
                    {
                        LvWaitingSealReports.SelectedItem = waitingSeal.FirstOrDefault(x => x.ReportId == prevWaitingSealId) ?? waitingSeal[0];
                    }
                }
                else
                {
                    LvWaitingSealReports.ItemsSource = null;
                    TabItemWaitingSeal.Visibility = Visibility.Collapsed;
                    BorderWaitingSealCount.Visibility = Visibility.Collapsed;
                    TxtCountWaitingSeal.Text = "0";
                }

                if (inbox.Count > 0)
                {
                    LvInboxReports.SelectedItem = inbox.FirstOrDefault(x => x.ReportId == prevInboxId) ?? inbox[0];
                }
                if (outbox.Count > 0)
                {
                    LvOutboxReports.SelectedItem = outbox.FirstOrDefault(x => x.ReportId == prevOutboxId) ?? outbox[0];
                }
                if (returned.Count > 0)
                {
                    LvReturnedReports.SelectedItem = returned.FirstOrDefault(x => x.ReportId == prevReturnedId) ?? returned[0];
                }
                if (archive.Count > 0)
                {
                    LvArchiveReports.SelectedItem = archive.FirstOrDefault(x => x.ReportId == prevArchiveId) ?? archive[0];
                }

                TxtCountInbox.Text = inbox.Count.ToString();
                TxtCountOutbox.Text = outbox.Count.ToString();
                TxtCountReturned.Text = returned.Count.ToString();
                TxtCountCompleted.Text = archive.Count.ToString();

                // Phát hiện thay đổi số lượng mới để phát chuông và cập nhật thanh trạng thái
                if (returned.Count > prevReturnedCount)
                {
                    _ = Task.Run(() =>
                    {
                        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    });
                    UpdateStatus($"🔔 [Thời gian thực] Có {returned.Count - prevReturnedCount} báo cáo mới bị trả về!");
                }
                else if (inbox.Count > prevInboxCount)
                {
                    _ = Task.Run(() =>
                    {
                        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    });
                    UpdateStatus($"🔔 [Thời gian thực] Có {inbox.Count - prevInboxCount} báo cáo mới cần duyệt!");
                }
                else if (canSeal && waitingSeal.Count > prevWaitingSealCount)
                {
                    _ = Task.Run(() =>
                    {
                        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    });
                    UpdateStatus($"🔔 [Thời gian thực] Có {waitingSeal.Count - prevWaitingSealCount} báo cáo mới chờ đóng dấu!");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Lỗi đồng bộ ngầm thời gian thực: {ex.Message}");
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

            // Luôn đồng bộ và nạp danh sách người nhận phê duyệt mới nhất
            PopulateWorkflowApprovers(_vgcaAuth.CurrentTeacher.Cccd);

            BtnConfirmSubmitWorkflow.IsEnabled = true;

            // Hiển thị backdrop làm tối và mở Popup độc lập HWND nổi hoàn toàn lên trên WebView2
            if (OverlayLoadingModal != null)
            {
                OverlayLoadingModal.Visibility = Visibility.Visible;
            }

            if (OverlaySubmitWorkflowPopup != null)
            {
                if (MainRootGrid != null)
                {
                    OverlaySubmitWorkflowPopup.PlacementTarget = MainRootGrid;
                }
                OverlaySubmitWorkflowPopup.IsOpen = true;
                RepositionLoadingPopup();

                Dispatcher.InvokeAsync(() =>
                {
                    TxtWorkflowSubmitTitle.Focus();
                    TxtWorkflowSubmitTitle.SelectAll();
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void BtnCancelSubmitWorkflow_Click(object sender, RoutedEventArgs e)
        {
            if (OverlaySubmitWorkflowPopup != null)
            {
                OverlaySubmitWorkflowPopup.IsOpen = false;
            }
            if (OverlayLoadingModal != null)
            {
                OverlayLoadingModal.Visibility = Visibility.Collapsed;
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
                    if (OverlaySubmitWorkflowPopup != null)
                    {
                        OverlaySubmitWorkflowPopup.IsOpen = false;
                    }
                    if (OverlayLoadingModal != null)
                    {
                        OverlayLoadingModal.Visibility = Visibility.Collapsed;
                    }

                    MessageBox.Show(
                        $"Đã gửi trình ký báo cáo thành công!\n\n• Mã báo cáo: {result.ReportId}\n• Người nhận duyệt: {approver.FullName}\n• Trạng thái: Chờ mở duyệt (WAITING_OPEN)",
                        "Trình Ký Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);

                    UpdateStatus($"Đã gửi trình ký báo cáo '{title}' tới {approver.FullName} (Mã: {result.ReportId})");

                    // Tự động lưu bản sao vào bộ nhớ đệm cục bộ của máy tính để người gửi mở xem tức thì (<10ms)
                    try
                    {
                        string tempDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro_Reports");
                        Directory.CreateDirectory(tempDir);
                        string baseHash = ReportWorkflowService.ComputeSha256(_lastSignedPdfPath);
                        string cacheDest = Path.Combine(tempDir, $"{result.ReportId}_{baseHash}.pdf");
                        File.Copy(_lastSignedPdfPath, cacheDest, true);
                    }
                    catch (Exception cacheEx)
                    {
                        System.Diagnostics.Trace.TraceWarning($"Lỗi lưu cache báo cáo gửi: {cacheEx.Message}");
                    }

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
            if (_isBusyProcessing)
            {
                return;
            }

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
            ShowLoadingOverlay("Đang nạp báo cáo...", $"Đang khóa phiên và nạp văn bản: {report.Title}...", "📑");
            SetProcessingBusyState(true);

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
                    BtnReturnWorkflowReport.Visibility = Visibility.Visible;

                    await LoadPdfDocumentAsync(result.DownloadedFilePath);

                    MainTabControl.SelectedIndex = 1;
                    UpdateStatus($"Đã nạp báo cáo '{report.Title}' sang không gian ký duyệt. Vui lòng ký số hoặc bấm 'Trả Về Báo Cáo'.");
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
                await HideLoadingOverlayAsync(50);
                SetProcessingBusyState(false);
                BtnOpenForReview.IsEnabled = true;
            }
        }

        private void BtnExitWorkflowReviewMode_Click(object sender, RoutedEventArgs e)
        {
            if (_isOutboxViewMode)
            {
                _isOutboxViewMode = false;
                BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                CloseWorkflowApprovalChoicePopup();
                CloseForwardWorkflowPopup();
                BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                BtnReturnWorkflowReport.Visibility = Visibility.Collapsed;
                BtnSignCurrentDocument.IsEnabled = _lastSignedPdfPath != null;
                MainTabControl.SelectedIndex = 2;
                TabControlWorkflowSub.SelectedIndex = 1;
                UpdateStatus("Đã đóng chế độ xem báo cáo đã gửi.");
                return;
            }

            if (_isWorkflowSealingMode)
            {
                var confirmSealExit = MessageBox.Show("Bạn có chắc chắn muốn thoát khỏi chế độ đóng dấu báo cáo không?",
                    "Thoát Chế Độ Đóng Dấu", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (confirmSealExit == MessageBoxResult.Yes)
                {
                    _activeWorkflowReportId = null;
                    _activeWorkflowExpectedBaseHash = null;
                    _isWorkflowSealingMode = false;
                    BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                    BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                    if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                    BtnCommitWorkflowSeal.Visibility = Visibility.Collapsed;
                    BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                    BtnReturnWorkflowReport.Visibility = Visibility.Collapsed;
                    CloseWorkflowApprovalChoicePopup();
                    CloseForwardWorkflowPopup();
                    MainTabControl.SelectedIndex = 2;
                    TabControlWorkflowSub.SelectedItem = TabItemWaitingSeal;
                    UpdateStatus("Đã thoát chế độ đóng dấu báo cáo.");
                }
                return;
            }

            var confirm = MessageBox.Show("Bạn có chắc chắn muốn thoát khỏi chế độ phê duyệt báo cáo liên thông không?",
                "Thoát Chế Độ Phê Duyệt", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm == MessageBoxResult.Yes)
            {
                _activeWorkflowReportId = null;
                _activeWorkflowExpectedBaseHash = null;
                _isWorkflowSealingMode = false;
                BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                BtnCommitWorkflowSeal.Visibility = Visibility.Collapsed;
                BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                BtnReturnWorkflowReport.Visibility = Visibility.Collapsed;
                CloseWorkflowApprovalChoicePopup();
                CloseForwardWorkflowPopup();
                UpdateStatus("Đã thoát chế độ phê duyệt liên thông.");
            }
        }

        private async void BtnReturnWorkflowReport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_activeWorkflowReportId))
            {
                MessageBox.Show("Không có phiên phê duyệt báo cáo nào đang mở.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_vgcaAuth.IsLoggedIn || _vgcaAuth.CurrentTeacher == null)
            {
                MessageBox.Show("Bạn cần đăng nhập tài khoản chữ ký số VGCA để thực hiện trả về báo cáo.",
                    "Chưa Đăng Nhập", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var promptWindow = new Window
            {
                Title = "↩️ Trả Về Báo Cáo Cho Giáo Viên",
                Width = 480,
                Height = 260,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = (Brush)new BrushConverter().ConvertFromString("#F8FAFC")!
            };

            var stack = new StackPanel { Margin = new Thickness(16) };

            var lblHeader = new TextBlock
            {
                Text = "Vui lòng nhập lý do trả về báo cáo để giáo viên tiếp thu, chỉnh sửa:",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#1E293B")!,
                Margin = new Thickness(0, 0, 0, 8)
            };
            stack.Children.Add(lblHeader);

            var txtReason = new TextBox
            {
                Height = 85,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8),
                FontSize = 12,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#CBD5E1")!,
                Background = Brushes.White
            };
            stack.Children.Add(txtReason);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var btnCancel = new Button
            {
                Content = "Hủy Bỏ",
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true
            };

            var btnOk = new Button
            {
                Content = "↩️ Xác Nhận Trả Về",
                Padding = new Thickness(16, 6, 16, 6),
                Background = (Brush)new BrushConverter().ConvertFromString("#EA580C")!,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0)
            };

            bool isConfirmed = false;
            btnOk.Click += (s, ev) =>
            {
                if (string.IsNullOrWhiteSpace(txtReason.Text))
                {
                    MessageBox.Show("Vui lòng nhập lý do trả về để giáo viên hiểu rõ nội dung cần điều chỉnh.",
                        "Chưa Nhập Lý Do", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                isConfirmed = true;
                promptWindow.Close();
            };

            btnPanel.Children.Add(btnCancel);
            btnPanel.Children.Add(btnOk);
            stack.Children.Add(btnPanel);

            promptWindow.Content = stack;
            promptWindow.ShowDialog();

            if (!isConfirmed) return;

            string reason = txtReason.Text.Trim();
            string approverCccd = _vgcaAuth.CurrentTeacher.Cccd;
            string approverName = _vgcaAuth.CurrentTeacher.FullName;
            string targetReportId = _activeWorkflowReportId;

            UpdateStatus($"Đang trả về báo cáo '{targetReportId}'...");
            SetProcessingBusyState(true);

            try
            {
                var res = await _workflowService.ReturnReportAsync(targetReportId, approverCccd, approverName, reason);
                if (res.Success)
                {
                    MessageBox.Show($"Đã trả về báo cáo thành công cho người lập!\n\nLý do: {reason}",
                        "Trả Về Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);

                    _activeWorkflowReportId = null;
                    _activeWorkflowExpectedBaseHash = null;
                    BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                    BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                    if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                    BtnCommitWorkflowSeal.Visibility = Visibility.Collapsed;
                    BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                    BtnReturnWorkflowReport.Visibility = Visibility.Collapsed;
                    CloseWorkflowApprovalChoicePopup();
                    CloseForwardWorkflowPopup();

                    MainTabControl.SelectedIndex = 2; // Quay về Tab 3
                    TabControlWorkflowSub.SelectedIndex = 0; // Hộp thư đến
                    await RefreshWorkflowDataAsync();
                }
                else
                {
                    MessageBox.Show($"Không thể trả về báo cáo:\n{res.Message}", "Lỗi Trả Về", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi hệ thống khi trả về: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetProcessingBusyState(false);
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
                bool isLeadership = IsLeadership(_vgcaAuth.CurrentTeacher?.Title);
                string approverName = _vgcaAuth.CurrentTeacher?.FullName ?? "";

                var result = await _workflowService.CommitSignedReportAsync(
                    reportId: _activeWorkflowReportId,
                    approverCccd: _vgcaAuth.CurrentTeacher?.Cccd ?? "",
                    signedPdfPath: _lastSignedPdfPath,
                    expectedBaseHash: _activeWorkflowExpectedBaseHash ?? "",
                    isLeadershipSigner: isLeadership,
                    approverName: approverName
                );

                if (result.Success)
                {
                    if (isLeadership)
                    {
                        MessageBox.Show(
                            "Đã ký số phê duyệt thành công!\n\n" +
                            "Do người ký duyệt là Ban Giám Hiệu, báo cáo đã được chuyển vào danh mục [🔴 Báo Cáo Chờ Đóng Dấu].\n\n" +
                            "Người được ủy quyền đóng dấu (Văn thư / BGH) sẽ cắm USB Token Ban Cơ Yếu đóng con dấu pháp nhân cơ quan để hoàn tất ban hành.",
                            "Ký Duyệt Thành Công - Chờ Đóng Dấu", MessageBoxButton.OK, MessageBoxImage.Information);

                        _activeWorkflowReportId = null;
                        _activeWorkflowExpectedBaseHash = null;
                        _isWorkflowSealingMode = false;
                        BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                        BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                        if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                        BtnCommitWorkflowSeal.Visibility = Visibility.Collapsed;
                        BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                        BtnReturnWorkflowReport.Visibility = Visibility.Collapsed;
                        CloseWorkflowApprovalChoicePopup();
                        CloseForwardWorkflowPopup();

                        MainTabControl.SelectedIndex = 2;
                        if (_vgcaAuth.CurrentTeacher?.CanSeal == true)
                        {
                            TabControlWorkflowSub.SelectedItem = TabItemWaitingSeal;
                        }
                        else
                        {
                            TabControlWorkflowSub.SelectedItem = TabItemArchive;
                        }
                        await RefreshWorkflowDataAsync();
                    }
                    else
                    {
                        MessageBox.Show(
                            "Đã phê duyệt và nộp báo cáo hoàn tất thành công!\n\n" +
                            "Văn bản đã được lưu trữ vĩnh viễn vào Kho Toàn Trường với đầy đủ chữ ký số PAdES LTV.",
                            "Phê Duyệt Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);

                        _activeWorkflowReportId = null;
                        _activeWorkflowExpectedBaseHash = null;
                        _isWorkflowSealingMode = false;
                        BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                        BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                        if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                        BtnCommitWorkflowSeal.Visibility = Visibility.Collapsed;
                        BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                        BtnReturnWorkflowReport.Visibility = Visibility.Collapsed;
                        CloseWorkflowApprovalChoicePopup();
                        CloseForwardWorkflowPopup();

                        MainTabControl.SelectedIndex = 2;
                        TabControlWorkflowSub.SelectedItem = TabItemArchive;
                        await RefreshWorkflowDataAsync();
                    }
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

        #region Workflow Approval Choice & Forwarding Handlers

        private void ShowWorkflowApprovalChoicePopup()
        {
            if (OverlayApprovalChoicePopup != null)
            {
                if (MainRootGrid != null)
                {
                    OverlayApprovalChoicePopup.PlacementTarget = MainRootGrid;
                }
                OverlayApprovalChoicePopup.IsOpen = true;
            }
        }

        private void CloseWorkflowApprovalChoicePopup()
        {
            if (OverlayApprovalChoicePopup != null)
            {
                OverlayApprovalChoicePopup.IsOpen = false;
            }
        }

        private void BtnCloseApprovalChoice_Click(object sender, RoutedEventArgs e)
        {
            CloseWorkflowApprovalChoicePopup();
        }

        private void ChoiceCommitApproval_Click(object sender, RoutedEventArgs e)
        {
            CloseWorkflowApprovalChoicePopup();
            BtnCommitWorkflowApproval_Click(sender, e);
        }

        private void ChoiceForwardApproval_Click(object sender, RoutedEventArgs e)
        {
            CloseWorkflowApprovalChoicePopup();
            OpenForwardWorkflowPopup();
        }

        private void BtnForwardWorkflowReport_Click(object sender, RoutedEventArgs e)
        {
            OpenForwardWorkflowPopup();
        }

        private void OpenForwardWorkflowPopup()
        {
            if (string.IsNullOrEmpty(_activeWorkflowReportId))
            {
                MessageBox.Show("Không có phiên phê duyệt báo cáo nào đang mở.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_lastSignedPdfPath) || !File.Exists(_lastSignedPdfPath))
            {
                MessageBox.Show("Vui lòng thực hiện ký số phê duyệt văn bản trước khi chuyển tiếp lên cấp trên.", "Chưa Ký Số", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (TxtForwardReportTitle != null)
            {
                TxtForwardReportTitle.Text = TxtWorkflowReviewNoticeHeader != null && !string.IsNullOrWhiteSpace(TxtWorkflowReviewNoticeHeader.Text)
                    ? TxtWorkflowReviewNoticeHeader.Text
                    : Path.GetFileName(_lastSignedPdfPath);
            }

            if (TxtForwardCurrentSigner != null)
            {
                TxtForwardCurrentSigner.Text = _vgcaAuth.CurrentTeacher != null
                    ? $"{_vgcaAuth.CurrentTeacher.FullName} ({_vgcaAuth.CurrentTeacher.Title})"
                    : "Người duyệt hiện tại";
            }

            string currentCccd = _vgcaAuth.CurrentTeacher?.Cccd ?? "";
            PopulateForwardApprovers(currentCccd);

            if (TxtForwardNotes != null)
            {
                TxtForwardNotes.Text = string.Empty;
            }

            if (OverlayForwardWorkflowPopup != null)
            {
                if (MainRootGrid != null)
                {
                    OverlayForwardWorkflowPopup.PlacementTarget = MainRootGrid;
                }
                OverlayForwardWorkflowPopup.IsOpen = true;
            }
        }

        private void CloseForwardWorkflowPopup()
        {
            if (OverlayForwardWorkflowPopup != null)
            {
                OverlayForwardWorkflowPopup.IsOpen = false;
            }
        }

        private void PopulateForwardApprovers(string currentCccd)
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

                if (CboForwardNextApprover != null)
                {
                    CboForwardNextApprover.ItemsSource = sorted;
                    if (sorted.Count > 0)
                    {
                        CboForwardNextApprover.SelectedIndex = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi nạp danh sách người chuyển tiếp: {ex}");
            }
        }

        private void BtnCancelForwardWorkflow_Click(object sender, RoutedEventArgs e)
        {
            CloseForwardWorkflowPopup();
        }

        private async void BtnConfirmForwardWorkflow_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_activeWorkflowReportId))
            {
                MessageBox.Show("Không có báo cáo nào đang mở để chuyển tiếp.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_lastSignedPdfPath) || !File.Exists(_lastSignedPdfPath))
            {
                MessageBox.Show("Vui lòng thực hiện ký số phê duyệt trước khi chuyển tiếp lên cấp trên.", "Chưa Ký Số", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CboForwardNextApprover.SelectedItem is not WorkflowApproverItem targetApprover)
            {
                MessageBox.Show("Vui lòng chọn người duyệt tiếp theo (BGH / Lãnh đạo).", "Chưa Chọn Người Duyệt", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string notes = TxtForwardNotes?.Text?.Trim() ?? string.Empty;
            string currentCccd = _vgcaAuth.CurrentTeacher?.Cccd ?? "";
            string currentName = _vgcaAuth.CurrentTeacher?.FullName ?? "Tổ trưởng";

            BtnConfirmForwardWorkflow.IsEnabled = false;
            UpdateStatus("Đang tải tệp đã ký và chuyển tiếp báo cáo lên cấp trên...");

            try
            {
                var forwardResult = await _workflowService.ForwardReportAsync(
                    reportId: _activeWorkflowReportId,
                    currentSignerCccd: currentCccd,
                    currentSignerName: currentName,
                    nextApproverCccd: targetApprover.Cccd,
                    nextApproverName: targetApprover.FullName,
                    signedPdfPath: _lastSignedPdfPath,
                    expectedBaseHash: _activeWorkflowExpectedBaseHash ?? "",
                    forwardNotes: notes
                );

                if (forwardResult.Success)
                {
                    CloseForwardWorkflowPopup();
                    CloseWorkflowApprovalChoicePopup();

                    MessageBox.Show(
                        $"Đã chuyển tiếp báo cáo thành công!\n\n" +
                        $"• Báo cáo đã gửi tới: {targetApprover.FullName}\n" +
                        $"• Trạng thái: Chờ mở duyệt và ký tiếp\n" +
                        $"• Tệp PDF chuyển tiếp mang đầy đủ chữ ký số tích lũy của bạn.",
                        "Chuyển Tiếp Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);

                    _activeWorkflowReportId = null;
                    _activeWorkflowExpectedBaseHash = null;
                    _isWorkflowSealingMode = false;
                    BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                    BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                    if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                    BtnCommitWorkflowSeal.Visibility = Visibility.Collapsed;
                    BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                    BtnReturnWorkflowReport.Visibility = Visibility.Collapsed;

                    MainTabControl.SelectedIndex = 2;
                    TabControlWorkflowSub.SelectedItem = TabItemOutbox;
                    await RefreshWorkflowDataAsync();
                }
                else
                {
                    MessageBox.Show($"Không thể chuyển tiếp báo cáo:\n{forwardResult.Message}", "Lỗi Chuyển Tiếp", MessageBoxButton.OK, MessageBoxImage.Error);
                    UpdateStatus($"Lỗi chuyển tiếp: {forwardResult.Message}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi hệ thống khi chuyển tiếp: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnConfirmForwardWorkflow.IsEnabled = true;
            }
        }

        #endregion

        private async void BtnOpenForSealing_Click(object sender, RoutedEventArgs e)
        {
            if (LvWaitingSealReports.SelectedItem is not ReportModel report)
            {
                MessageBox.Show("Vui lòng chọn một báo cáo trong danh sách để mở đóng dấu.",
                    "Chưa Chọn Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_vgcaAuth.IsLoggedIn || _vgcaAuth.CurrentTeacher == null)
            {
                MessageBox.Show("Bạn cần đăng nhập tài khoản chữ ký số VGCA để mở và đóng dấu văn bản.",
                    "Chưa Đăng Nhập", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!_vgcaAuth.CurrentTeacher.CanSeal)
            {
                MessageBox.Show("Tài khoản của bạn chưa được phân quyền 'Đóng Dấu Cơ Quan'.\n\nChỉ Ban Giám Hiệu, Văn thư hoặc người được phân quyền quản lý thiết bị USB Token mới được phép thực hiện chức năng này.",
                    "Chưa Cấp Quyền Đóng Dấu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"XÁC NHẬN MỞ VĂN BẢN ĐỂ ĐÓNG DẤU PHÁP NHÂN:\n\n" +
                $"• Tên báo cáo: \"{report.Title}\"\n" +
                $"• Người ký duyệt: {report.SignedByName ?? report.ApproverName}\n" +
                $"• Thời gian ký: {report.SignedTimeFormatted}\n\n" +
                $"Bạn có muốn nạp văn bản này sang bàn làm việc để đóng con dấu cơ quan không?",
                "Xác Nhận Đóng Dấu Pháp Nhân", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            UpdateStatus($"Đang nạp tệp báo cáo chờ đóng dấu '{report.ReportId}'...");
            BtnOpenForSealing.IsEnabled = false;
            ShowLoadingOverlay("Đang nạp báo cáo...", $"Đang nạp văn bản chờ đóng dấu: {report.Title}...", "🔴");
            SetProcessingBusyState(true);

            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro_Reports");
                Directory.CreateDirectory(tempDir);
                string cacheKey = !string.IsNullOrWhiteSpace(report.SignedHash)
                    ? report.SignedHash
                    : (!string.IsNullOrWhiteSpace(report.BaseHash) ? report.BaseHash : report.DriveFileId);
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    cacheKey = cacheKey.Replace(c, '_');
                }
                string destPath = Path.Combine(tempDir, $"{report.ReportId}_{cacheKey}.pdf");

                if (!File.Exists(destPath) || new FileInfo(destPath).Length == 0)
                {
                    bool downloaded = await _workflowService.DownloadFileAsync(report.DriveFileId, destPath);
                    if (!downloaded || !File.Exists(destPath))
                    {
                        MessageBox.Show("Không thể tải tệp báo cáo từ máy chủ đám mây.", "Lỗi Tải Tệp", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }

                _activeWorkflowReportId = report.ReportId;
                _activeWorkflowExpectedBaseHash = report.BaseHash;
                _isWorkflowSealingMode = true;

                BorderWorkflowReviewNotice.Visibility = Visibility.Visible;
                TxtWorkflowReviewNoticeHeader.Text = $"CHẾ ĐỘ ĐÓNG DẤU PHÁP NHÂN CƠ QUAN: {report.Title}";
                TxtWorkflowReviewNoticeDetail.Text = $"Người ký duyệt: {report.SignedByName ?? report.ApproverName} • Ngày ký: {report.SignedTimeFormatted} • Đã sẵn sàng đóng dấu.";

                BtnCommitWorkflowSeal.Visibility = Visibility.Visible;
                BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                BtnSubmitWorkflowReport.Visibility = Visibility.Collapsed;
                BtnReturnWorkflowReport.Visibility = Visibility.Collapsed;

                await LoadPdfDocumentAsync(destPath);

                MainTabControl.SelectedIndex = 1;
                UpdateStatus($"Đã nạp văn bản '{report.Title}' sang Tab 2. Vui lòng bấm [📍 Chèn Vị Trí Ký], căn chỉnh vị trí con dấu trùm 1/3 chữ ký rồi bấm [🔴 Đóng dấu].");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi nạp văn bản: {ex.Message}", "Lỗi Hệ Thống", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                await HideLoadingOverlayAsync(50);
                SetProcessingBusyState(false);
                BtnOpenForSealing.IsEnabled = true;
            }
        }

        private void LvWaitingSealReports_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            BtnOpenForSealing_Click(sender, e);
        }

        private async void BtnCommitWorkflowSeal_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_activeWorkflowReportId))
            {
                MessageBox.Show("Không có phiên đóng dấu báo cáo nào đang mở.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_lastSignedPdfPath) || !File.Exists(_lastSignedPdfPath))
            {
                MessageBox.Show("Vui lòng thực hiện đóng dấu pháp nhân trước khi nộp lên hệ thống.",
                    "Chưa Đóng Dấu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnCommitWorkflowSeal.IsEnabled = false;
            UpdateStatus("Đang tải lên bản báo cáo đã đóng dấu và cập nhật hệ thống...");

            try
            {
                var result = await _workflowService.CommitSealedReportAsync(
                    reportId: _activeWorkflowReportId,
                    sealerCccd: _vgcaAuth.CurrentTeacher?.Cccd ?? "",
                    sealerName: _vgcaAuth.CurrentTeacher?.FullName ?? "",
                    sealedPdfPath: _lastSignedPdfPath
                );

                if (result.Success)
                {
                    MessageBox.Show(
                        "Đã đóng dấu pháp nhân và ban hành báo cáo hoàn tất thành công!\n\n" +
                        "Văn bản đã được lưu trữ vĩnh viễn vào Kho Toàn Trường với đầy đủ chữ ký số cá nhân và con dấu pháp nhân cơ quan theo Nghị định 30/2020/NĐ-CP.",
                        "Ban Hành Hoàn Tất", MessageBoxButton.OK, MessageBoxImage.Information);

                    _activeWorkflowReportId = null;
                    _activeWorkflowExpectedBaseHash = null;
                    _isWorkflowSealingMode = false;

                    BorderWorkflowReviewNotice.Visibility = Visibility.Collapsed;
                    BtnCommitWorkflowSeal.Visibility = Visibility.Collapsed;
                    BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                    if (BtnForwardWorkflowReport != null) BtnForwardWorkflowReport.Visibility = Visibility.Collapsed;
                    BtnSubmitWorkflowReport.Visibility = Visibility.Visible;
                    BtnReturnWorkflowReport.Visibility = Visibility.Collapsed;
                    CloseWorkflowApprovalChoicePopup();
                    CloseForwardWorkflowPopup();

                    MainTabControl.SelectedIndex = 2;
                    TabControlWorkflowSub.SelectedItem = TabItemArchive;
                    await RefreshWorkflowDataAsync();
                }
                else
                {
                    MessageBox.Show($"Không thể nộp bản đã đóng dấu:\n{result.Message}", "Lỗi Nộp Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Error);
                    UpdateStatus($"Lỗi nộp báo cáo: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi hệ thống: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnCommitWorkflowSeal.IsEnabled = true;
            }
        }

        private void LvInboxReports_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BtnOpenForReview != null)
            {
                BtnOpenForReview.IsEnabled = LvInboxReports?.SelectedItem != null;
            }
        }

        private void LvOutboxReports_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool hasItem = LvOutboxReports?.SelectedItem != null;
            if (BtnViewOutboxReport != null) BtnViewOutboxReport.IsEnabled = hasItem;
            if (BtnDismissOutboxReport != null) BtnDismissOutboxReport.IsEnabled = hasItem;

            if (LvOutboxReports?.SelectedItem is ReportModel report)
            {
                if (report.Status == "RECALLED" || report.Status == "SIGNED_COMMITTED" || report.Status == "RETURNED")
                {
                    BtnRecallReport.IsEnabled = false;
                    BtnRecallReport.ToolTip = report.Status == "RECALLED"
                        ? "Báo cáo này đã được thu hồi trước đó rồi."
                        : (report.Status == "RETURNED" ? "Báo cáo này đã bị Ban Giám hiệu trả về." : "Báo cáo đã được ký duyệt hoàn tất, không thể thu hồi.");
                }
                else
                {
                    BtnRecallReport.IsEnabled = true;
                    BtnRecallReport.ToolTip = "Thu hồi báo cáo đã gửi (khi Ban Giám hiệu chưa ký duyệt).";
                }
            }
            else
            {
                BtnRecallReport.IsEnabled = false;
                BtnRecallReport.ToolTip = null;
            }
        }

        private void LvReturnedReports_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool hasItem = LvReturnedReports?.SelectedItem != null;
            if (BtnViewReturnedReport != null) BtnViewReturnedReport.IsEnabled = hasItem;
            if (BtnDismissReturnedReport != null) BtnDismissReturnedReport.IsEnabled = hasItem;
        }

        private void LvWaitingSealReports_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BtnOpenForSealing != null)
            {
                BtnOpenForSealing.IsEnabled = LvWaitingSealReports?.SelectedItem != null;
            }
        }

        private void LvArchiveReports_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool hasItem = LvArchiveReports?.SelectedItem != null;
            if (BtnViewArchiveReport != null) BtnViewArchiveReport.IsEnabled = hasItem;
            if (BtnDownloadArchiveReport != null) BtnDownloadArchiveReport.IsEnabled = hasItem;
        }

        private async void BtnRecallReport_Click(object sender, RoutedEventArgs e)
        {
            if (LvOutboxReports.SelectedItem is not ReportModel report)
            {
                MessageBox.Show("Vui lòng chọn một báo cáo bạn đã gửi để thu hồi.", "Chưa Chọn Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (report.Status == "RECALLED")
            {
                MessageBox.Show("Báo cáo này đã được thu hồi trước đó rồi.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (report.Status == "RETURNED")
            {
                MessageBox.Show("Báo cáo này đã bị Ban Giám hiệu trả về, không thể thu hồi.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        private async void BtnDismissOutboxReport_Click(object sender, RoutedEventArgs e)
        {
            if (LvOutboxReports.SelectedItem is not ReportModel report)
            {
                MessageBox.Show("Vui lòng chọn một báo cáo cần xóa khỏi danh sách hiển thị.", "Chưa Chọn Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"Bạn có chắc chắn muốn xóa báo cáo '{report.Title}' (Mã: {report.ReportId}) khỏi danh sách hiển thị trên máy không?\n\n(Lưu ý: Tệp gốc và lịch sử trên hệ thống vẫn được lưu trữ an toàn, chỉ ẩn khỏi giao diện của bạn).",
                "Xác Nhận Xóa Khỏi Danh Sách",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            string myCccd = _vgcaAuth.CurrentTeacher?.Cccd ?? string.Empty;
            AddHiddenReportId(myCccd, report.ReportId);
            UpdateStatus($"Đã xóa báo cáo '{report.ReportId}' khỏi danh sách hiển thị.");
            await RefreshWorkflowDataAsync();
        }

        private async void BtnDismissReturnedReport_Click(object sender, RoutedEventArgs e)
        {
            if (LvReturnedReports.SelectedItem is not ReportModel report)
            {
                MessageBox.Show("Vui lòng chọn một báo cáo trả về cần xóa khỏi danh sách hiển thị.", "Chưa Chọn Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"Bạn có chắc chắn muốn xóa báo cáo trả về '{report.Title}' (Mã: {report.ReportId}) khỏi danh sách hiển thị không?\n\n(Lưu ý: Tệp gốc và lý do trả về trên hệ thống vẫn được lưu trữ an toàn).",
                "Xác Nhận Xóa Khỏi Danh Sách",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            string myCccd = _vgcaAuth.CurrentTeacher?.Cccd ?? string.Empty;
            AddHiddenReportId(myCccd, report.ReportId);
            UpdateStatus($"Đã xóa báo cáo '{report.ReportId}' khỏi danh sách trả về.");
            await RefreshWorkflowDataAsync();
        }

        private async void BtnViewOutboxReport_Click(object sender, RoutedEventArgs e)
        {
            await OpenSelectedOutboxReportForViewingAsync();
        }

        private async void LvOutboxReports_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            await OpenSelectedOutboxReportForViewingAsync();
        }

        private async void BtnViewReturnedReport_Click(object sender, RoutedEventArgs e)
        {
            await OpenSelectedReturnedReportForViewingAsync();
        }

        private async void LvReturnedReports_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            await OpenSelectedReturnedReportForViewingAsync();
        }

        private async Task OpenSelectedOutboxReportForViewingAsync()
        {
            if (LvOutboxReports.SelectedItem is not ReportModel report)
            {
                MessageBox.Show("Vui lòng chọn một báo cáo trong danh sách đã gửi để xem.",
                    "Chưa Chọn Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await OpenReportFileForViewingAsync(report, BtnViewOutboxReport);
        }

        private async Task OpenSelectedReturnedReportForViewingAsync()
        {
            if (LvReturnedReports.SelectedItem is not ReportModel report)
            {
                MessageBox.Show("Vui lòng chọn một báo cáo trong danh sách trả về để xem.",
                    "Chưa Chọn Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await OpenReportFileForViewingAsync(report, BtnViewReturnedReport);
        }

        /// <summary>
        /// Mở xem tệp báo cáo nhanh không nhấp nháy, kiểm tra cache cục bộ trước (<10ms)
        /// </summary>
        private async Task OpenReportFileForViewingAsync(ReportModel report, Button? sourceButton = null)
        {
            if (_isBusyProcessing || report == null) return;

            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro_Reports");
                Directory.CreateDirectory(tempDir);

                string? localFile = null;

                // 1. Kiểm tra chính xác theo cacheKey (chỉ chấp nhận cache đúng phiên bản SignedHash hoặc BaseHash/DriveFileId hiện tại)
                string cacheKey = !string.IsNullOrWhiteSpace(report.SignedHash)
                    ? report.SignedHash
                    : (!string.IsNullOrWhiteSpace(report.BaseHash) ? report.BaseHash : report.DriveFileId);

                if (!string.IsNullOrWhiteSpace(cacheKey))
                {
                    foreach (char c in Path.GetInvalidFileNameChars())
                    {
                        cacheKey = cacheKey.Replace(c, '_');
                    }
                    string exactPath = Path.Combine(tempDir, $"{report.ReportId}_{cacheKey}.pdf");
                    if (File.Exists(exactPath) && new FileInfo(exactPath).Length > 0)
                    {
                        localFile = exactPath;
                    }
                }

                // 2. NẾU ĐÃ CÓ FILE CỤC BỘ ĐÚNG PHIÊN BẢN: MỞ TRỰC TIẾP NGAY LẬP TỨC (<10ms)
                if (localFile != null && File.Exists(localFile))
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = localFile,
                            UseShellExecute = true
                        };
                        System.Diagnostics.Process.Start(psi);
                        UpdateStatus($"Đã mở xem báo cáo '{report.Title}' trên máy tính.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.TraceWarning($"Lỗi mở PDF bên ngoài: {ex.Message}. Mở sang Tab 2.");
                        _isOutboxViewMode = true;
                        _activeWorkflowReportId = null;

                        BorderWorkflowReviewNotice.Visibility = Visibility.Visible;
                        TxtWorkflowReviewNoticeHeader.Text = $"CHẾ ĐỘ XEM BÁO CÁO (CHỈ ĐỌC): {report.Title}";
                        TxtWorkflowReviewNoticeDetail.Text = $"Người nhận: {report.ApproverName} • Ngày gửi: {report.CreatedTimeFormatted} • Trạng thái: {report.StatusText}.";
                        BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                        BtnSubmitWorkflowReport.Visibility = Visibility.Collapsed;
                        BtnReturnWorkflowReport.Visibility = Visibility.Collapsed;
                        BtnSignCurrentDocument.IsEnabled = false;
                        BtnInsertSignatureStamp.IsEnabled = false;

                        await LoadPdfDocumentAsync(localFile);
                        MainTabControl.SelectedIndex = 1;
                        UpdateStatus($"Đang hiển thị bản gửi: '{report.Title}' (Chế độ xem chỉ đọc).");
                        return;
                    }
                }

                // 4. NẾU CHƯA CÓ TRÊN MÁY CỤC BỘ: Tải từ máy chủ đám mây
                UpdateStatus($"Đang tải bản sao báo cáo '{report.ReportId}' từ máy chủ...");
                if (sourceButton != null) sourceButton.IsEnabled = false;
                ShowLoadingOverlay("Đang tải văn bản...", $"Đang tải tệp báo cáo: {report.Title}...", "📥");
                SetProcessingBusyState(true);

                string destPath = Path.Combine(tempDir, $"{report.ReportId}_{cacheKey}.pdf");
                bool downloaded = await _workflowService.DownloadFileAsync(report.DriveFileId, destPath);

                if (downloaded && File.Exists(destPath))
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = destPath,
                            UseShellExecute = true
                        };
                        System.Diagnostics.Process.Start(psi);
                        UpdateStatus($"Đã mở xem báo cáo '{report.Title}' trên máy tính thành công.");
                    }
                    catch (Exception launchEx)
                    {
                        System.Diagnostics.Trace.TraceWarning($"Lỗi mở PDF ngoài: {launchEx.Message}. Chuyển sang Tab 2.");
                        _isOutboxViewMode = true;
                        _activeWorkflowReportId = null;

                        BorderWorkflowReviewNotice.Visibility = Visibility.Visible;
                        TxtWorkflowReviewNoticeHeader.Text = $"CHẾ ĐỘ XEM BÁO CÁO (CHỈ ĐỌC): {report.Title}";
                        TxtWorkflowReviewNoticeDetail.Text = $"Người nhận: {report.ApproverName} • Ngày gửi: {report.CreatedTimeFormatted} • Trạng thái: {report.StatusText}.";
                        BtnCommitWorkflowApproval.Visibility = Visibility.Collapsed;
                        BtnSubmitWorkflowReport.Visibility = Visibility.Collapsed;
                        BtnReturnWorkflowReport.Visibility = Visibility.Collapsed;
                        BtnSignCurrentDocument.IsEnabled = false;
                        BtnInsertSignatureStamp.IsEnabled = false;

                        await LoadPdfDocumentAsync(destPath);
                        MainTabControl.SelectedIndex = 1;
                        UpdateStatus($"Đang hiển thị: '{report.Title}'.");
                    }
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
                await HideLoadingOverlayAsync(50);
                SetProcessingBusyState(false);
                if (sourceButton != null) sourceButton.IsEnabled = true;
            }
        }

        private void LvArchiveReports_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            BtnViewArchiveReport_Click(sender, e);
        }

        private async void BtnViewArchiveReport_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusyProcessing)
            {
                return;
            }

            if (LvArchiveReports.SelectedItem is not ReportModel report)
            {
                MessageBox.Show("Vui lòng chọn một báo cáo hoàn tất trong danh sách để xem.", "Chưa Chọn Báo Cáo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro_Reports");
                Directory.CreateDirectory(tempDir);

                string cacheKey = !string.IsNullOrWhiteSpace(report.SignedHash)
                    ? report.SignedHash
                    : (!string.IsNullOrWhiteSpace(report.BaseHash) ? report.BaseHash : report.DriveFileId);

                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    cacheKey = cacheKey.Replace(c, '_');
                }

                string destPath = Path.Combine(tempDir, $"{report.ReportId}_{cacheKey}_Completed.pdf");

                // Kiểm tra nếu đã có file cục bộ trên máy -> mở trực tiếp ngay lập tức (<10ms)
                if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = destPath,
                            UseShellExecute = true
                        };
                        System.Diagnostics.Process.Start(psi);
                        UpdateStatus($"Đã mở báo cáo hoàn tất '{report.Title}' trên máy tính.");
                        return;
                    }
                    catch
                    {
                        await LoadPdfDocumentAsync(destPath);
                        MainTabControl.SelectedIndex = 1;
                        UpdateStatus($"Đang hiển thị báo cáo hoàn tất: {report.Title}");
                        return;
                    }
                }

                // Nếu chưa có file cục bộ, tải từ máy chủ đám mây
                UpdateStatus($"Đang tải tệp hoàn tất '{report.ReportId}' để hiển thị...");
                ShowLoadingOverlay("Đang mở văn bản...", $"Đang nạp báo cáo hoàn tất: {report.Title}...", "📥");
                SetProcessingBusyState(true);

                bool fileReady = await _workflowService.DownloadFileAsync(report.DriveFileId, destPath);

                if (fileReady && File.Exists(destPath))
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = destPath,
                            UseShellExecute = true
                        };
                        System.Diagnostics.Process.Start(psi);
                        UpdateStatus($"Đã mở báo cáo hoàn tất '{report.Title}' trên máy tính.");
                    }
                    catch
                    {
                        await LoadPdfDocumentAsync(destPath);
                        MainTabControl.SelectedIndex = 1;
                        UpdateStatus($"Đang hiển thị báo cáo hoàn tất: {report.Title}");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(report.GoogleDriveUrl))
                {
                    // Mở trực tiếp link xem Google Drive trên trình duyệt web
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = report.GoogleDriveUrl,
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                    UpdateStatus($"Đang mở báo cáo '{report.Title}' trên Google Drive...");
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
            finally
            {
                await HideLoadingOverlayAsync(50);
                SetProcessingBusyState(false);
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
                    string tempDir = Path.Combine(Path.GetTempPath(), "VGCASignToolPro_Reports");
                    string cacheKey = !string.IsNullOrWhiteSpace(report.SignedHash)
                        ? report.SignedHash
                        : (!string.IsNullOrWhiteSpace(report.BaseHash) ? report.BaseHash : report.DriveFileId);

                    foreach (char c in Path.GetInvalidFileNameChars())
                    {
                        cacheKey = cacheKey.Replace(c, '_');
                    }

                    string cachedPath = Path.Combine(tempDir, $"{report.ReportId}_{cacheKey}_Completed.pdf");
                    bool success = false;
                    if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 0)
                    {
                        File.Copy(cachedPath, sfd.FileName, true);
                        success = true;
                    }
                    else
                    {
                        success = await _workflowService.DownloadFileAsync(report.DriveFileId, sfd.FileName);
                    }

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

        /// <summary>
        /// Quản lý trạng thái hiển thị của WebView2 theo Tab hiện tại.
        /// Khi ở Tab 3, Tab 1, Tab 4: Thu gọn (Collapsed) PdfPreviewControl để giải phóng hoàn toàn HWND Win32,
        /// triệt tiêu triệt để hiện tượng tranh chấp chuột, trỏ chuột và luồng vẽ nền (WM_MOUSEMOVE, WM_PAINT) gây giật lag.
        /// Khi quay lại Tab 2: Phục hồi Visible ngay tức thì, bảo toàn nguyên vẹn tài liệu đang nạp.
        /// </summary>
        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Bắt buộc kiểm tra chỉ xử lý khi chính MainTabControl kích hoạt sự kiện
            // Tránh bắt nhầm các sự kiện SelectionChanged từ TabControl con (như TabControlWorkflowSub) hoặc ListView/ComboBox
            if (!ReferenceEquals(e.OriginalSource, MainTabControl))
            {
                return;
            }

            if (PdfPreviewControl == null) return;

            if (MainTabControl.SelectedIndex == 1)
            {
                PdfPreviewControl.Visibility = Visibility.Visible;
            }
            else
            {
                PdfPreviewControl.Visibility = Visibility.Collapsed;
            }
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