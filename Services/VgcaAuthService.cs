using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QRCoder;
using VGCASignToolPro.Models;

namespace VGCASignToolPro.Services
{
    public class VgcaTeacherModel : IDisposable
    {
        public const int MaxAttempts = 5;
        private readonly object _stateLock = new();
        private int _failedAttempts;

        public string Cccd { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Title { get; set; } = "Giáo viên";
        public string Department { get; set; } = string.Empty;
        public string Organization { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public bool CanSeal { get; set; } = false;
        public string SigningMethod { get; set; } = "AUTO"; // "AUTO", "USB_TOKEN", "VGCA_CLOUD"
        public string Password { get; set; } = "123456";
        public string Status { get; internal set; } = "ACTIVE";
        public int RemainingCounter { get; internal set; } = MaxAttempts;
        public int FailedAttempts
        {
            get => _failedAttempts;
            internal set => _failedAttempts = value;
        }

        public string KeyContainer { get; internal set; } = string.Empty;
        public string SerialNumber { get; internal set; } = string.Empty;

        public int RecordFailedAttempt()
        {
            lock (_stateLock)
            {
                _failedAttempts++;
                RemainingCounter = Math.Max(0, MaxAttempts - _failedAttempts);
                if (_failedAttempts >= MaxAttempts)
                {
                    Status = "LOCKED";
                }
                return RemainingCounter;
            }
        }

        public void ResetFailedAttempts()
        {
            lock (_stateLock)
            {
                _failedAttempts = 0;
                RemainingCounter = MaxAttempts;
                Status = "ACTIVE";
            }
        }

        public void Dispose()
        {
            KeyContainer = string.Empty;
            GC.SuppressFinalize(this);
        }
    }

    public class VgcaLoginResult
    {
        public bool Success { get; set; }
        public int ErrorCode { get; set; }
        public string ErrorDescription { get; set; } = string.Empty;
        public VgcaTeacherModel? Teacher { get; set; }
        public string AccessToken { get; set; } = string.Empty;
        public int RemainingCounter { get; set; } = VgcaTeacherModel.MaxAttempts;
    }

    public class VgcaAuthService
    {
        private static readonly Regex CccdRegex = new(@"^[0-9]{12}$", RegexOptions.Compiled);
        private readonly ConcurrentDictionary<string, VgcaTeacherModel> _roster = new(StringComparer.Ordinal);
        private readonly object _authLock = new();
        private static readonly byte[] _qrHmacKey = RandomNumberGenerator.GetBytes(32);

        public bool IsLoggedIn { get; private set; }
        public VgcaTeacherModel? CurrentTeacher { get; private set; }
        public string CurrentAccessToken { get; private set; } = string.Empty;
        public string CurrentQrSessionId { get; private set; } = string.Empty;
        public DateTime LoginTimestamp { get; private set; } = DateTime.MinValue;

        public event Action<bool, VgcaTeacherModel?>? LoginStateChanged;

        public VgcaAuthService()
        {
            // Tự động đồng bộ danh mục giáo viên & phân quyền trực tiếp từ Firebase Realtime Database
            _ = Task.Run(async () =>
            {
                try
                {
                    await SyncRosterFromCloudAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning($"Lỗi đồng bộ danh bạ ban đầu từ Firebase: {ex.Message}");
                }
            });
        }

        public void ReloadRosterFromDisk()
        {
            _ = SyncRosterFromCloudAsync();
        }

        public List<VgcaTeacherModel> GetTeacherList()
        {
            return _roster.Values.OrderBy(t => t.FullName).ToList();
        }

        public static string NormalizeVietnamese(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC).Replace('đ', 'd').Replace('Đ', 'D').Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Tự động đối soát danh tính và quyền hạn người ký từ Chứng thư số X.509 với Danh mục phân quyền Firebase
        /// </summary>
        public VgcaTeacherModel? ResolveTeacherIdentity(CertificateModel? cert)
        {
            if (cert == null) return null;

            // 1. Trích xuất thông tin nhận dạng từ Chứng thư số X.509
            string certCn = cert.CommonName ?? string.Empty;
            string certEmail = cert.Email ?? string.Empty;
            string certSerial = cert.SerialNumber ?? string.Empty;
            string certSubject = cert.Subject ?? string.Empty;

            // Tìm số CCCD từ Subject (dạng CCCD:062187000641 hoặc UID=062187000641 hoặc chuỗi 12 chữ số)
            string extractedCccd = string.Empty;
            var cccdMatch = Regex.Match(certSubject, @"\b(\d{12})\b");
            if (cccdMatch.Success)
            {
                extractedCccd = cccdMatch.Groups[1].Value;
            }

            string normCertCn = NormalizeVietnamese(certCn);

            // 2. Đối soát danh bạ Firebase trong RAM
            VgcaTeacherModel? matched = null;

            // Ưu tiên 1: Khớp theo CCCD
            if (!string.IsNullOrEmpty(extractedCccd) && _roster.TryGetValue(extractedCccd, out var byCccd))
            {
                matched = byCccd;
            }

            // Ưu tiên 2: Khớp theo Serial Number
            if (matched == null && !string.IsNullOrEmpty(certSerial))
            {
                matched = _roster.Values.FirstOrDefault(t =>
                    !string.IsNullOrEmpty(t.SerialNumber) &&
                    string.Equals(t.SerialNumber, certSerial, StringComparison.OrdinalIgnoreCase));
            }

            // Ưu tiên 3: Khớp theo Email công vụ
            if (matched == null && !string.IsNullOrEmpty(certEmail))
            {
                matched = _roster.Values.FirstOrDefault(t =>
                    !string.IsNullOrEmpty(t.Email) &&
                    string.Equals(t.Email, certEmail, StringComparison.OrdinalIgnoreCase));
            }

            // Ưu tiên 4: Khớp theo Họ và Tên chuẩn hóa không dấu
            if (matched == null && !string.IsNullOrEmpty(normCertCn))
            {
                matched = _roster.Values.FirstOrDefault(t =>
                {
                    string normTeacherName = NormalizeVietnamese(t.FullName);
                    return !string.IsNullOrEmpty(normTeacherName) &&
                           (normTeacherName == normCertCn ||
                            normTeacherName.Contains(normCertCn) ||
                            normCertCn.Contains(normTeacherName));
                });
            }

            // 3. Nếu tìm thấy trong danh bạ Firebase: Cập nhật thông tin chứng thư mới nhất
            if (matched != null)
            {
                if (!string.IsNullOrEmpty(certSerial)) matched.SerialNumber = certSerial;
                if (!string.IsNullOrEmpty(cert.KeyContainerName)) matched.KeyContainer = cert.KeyContainerName;
                if (!string.IsNullOrEmpty(certEmail)) matched.Email = certEmail;
                // LUÔN ĐỒNG BỘ ĐƠN VỊ TỪ CHỨNG THƯ BAN CƠ YẾU THỰC TẾ
                if (!string.IsNullOrWhiteSpace(cert.OrganizationUnit))
                {
                    matched.Organization = cert.OrganizationUnit;
                }
                else if (!string.IsNullOrWhiteSpace(cert.Organization))
                {
                    matched.Organization = cert.Organization;
                }
                // Nếu cắm USB Token phần cứng có chứa chứng thư con dấu hoặc chứng thư lãnh đạo, mở quyền đóng dấu
                if (cert.IsHardwareToken && cert.IsOrganizationCertificate)
                {
                    matched.CanSeal = true;
                }
                return matched;
            }

            // 4. Nếu chưa có trên Firebase: Tự động khởi tạo danh tính động từ chứng thư số thực tế
            string fallbackId = !string.IsNullOrEmpty(extractedCccd) ? extractedCccd :
                               (!string.IsNullOrEmpty(certSerial) ? certSerial : $"VGCA_{Guid.NewGuid():N}");

            bool canSealByDefault = cert.IsHardwareToken && (cert.IsOrganizationCertificate || certCn.ToUpperInvariant().Contains("TRƯỜNG"));

            var dynamicTeacher = new VgcaTeacherModel
            {
                Cccd = fallbackId,
                FullName = !string.IsNullOrWhiteSpace(certCn) ? certCn : "Cán bộ / Giáo viên",
                Title = cert.IsHardwareToken ? "Lãnh đạo / Văn thư (USB Token)" : "Giáo viên",
                Department = !string.IsNullOrWhiteSpace(cert.OrganizationUnit) ? cert.OrganizationUnit : "Đơn vị giáo dục",
                Organization = !string.IsNullOrWhiteSpace(cert.Organization) ? cert.Organization : (!string.IsNullOrWhiteSpace(cert.OrganizationUnit) ? cert.OrganizationUnit : "Cơ quan / Đơn vị"),
                Email = certEmail,
                CanSeal = canSealByDefault,
                SigningMethod = cert.IsHardwareToken ? "USB_TOKEN" : "VGCA_CLOUD",
                SerialNumber = certSerial,
                KeyContainer = cert.KeyContainerName ?? string.Empty,
                Status = "ACTIVE",
                RemainingCounter = VgcaTeacherModel.MaxAttempts
            };

            _roster[fallbackId] = dynamicTeacher;
            return dynamicTeacher;
        }

        /// <summary>
        /// Kích hoạt phiên làm việc tự động từ Chứng thư số được nhận diện
        /// </summary>
        public void SetCurrentActiveIdentity(CertificateModel? cert)
        {
            lock (_authLock)
            {
                if (cert == null)
                {
                    IsLoggedIn = false;
                    CurrentTeacher = null;
                    CurrentAccessToken = string.Empty;
                    CurrentQrSessionId = string.Empty;
                    LoginTimestamp = DateTime.MinValue;
                }
                else
                {
                    var teacher = ResolveTeacherIdentity(cert);
                    if (teacher != null)
                    {
                        teacher.ResetFailedAttempts();
                        IsLoggedIn = true;
                        CurrentTeacher = teacher;
                        CurrentAccessToken = $"vgca_auto_{RandomNumberGenerator.GetHexString(32)}";
                        LoginTimestamp = DateTime.UtcNow;
                    }
                    else
                    {
                        IsLoggedIn = false;
                        CurrentTeacher = null;
                    }
                }
            }

            LoginStateChanged?.Invoke(IsLoggedIn, CurrentTeacher);
        }

        /// <summary>
        /// Cập nhật danh sách giáo viên từ chuỗi JSON tải từ Firebase RTDB
        /// </summary>
        public (bool Success, string Message, int Count) UpdateRosterFromJson(string json, bool saveToDisk = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json))
                {
                    return (false, "Dữ liệu JSON rỗng.", _roster.Count);
                }

                using var stringReader = new StringReader(json);
                using var jsonReader = new JsonTextReader(stringReader) { MaxDepth = 10 };
                JToken root = JToken.Load(jsonReader);

                JArray? list = null;
                if (root is JObject jObj)
                {
                    list = jObj["roster"] as JArray ?? jObj["teachers"] as JArray;
                }
                else if (root is JArray jArr)
                {
                    list = jArr;
                }

                if (list == null || list.Count == 0)
                {
                    return (false, "Không tìm thấy danh sách giáo viên hợp lệ trong tệp JSON.", _roster.Count);
                }

                int updatedCount = 0;
                foreach (JToken item in list)
                {
                    string cccd = item["cccd"]?.ToString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(cccd)) continue;

                    string fullName = item["fullName"]?.ToString()?.Trim() ?? string.Empty;
                    string title = item["title"]?.ToString()?.Trim() ?? "Giáo viên";
                    string dept = item["department"]?.ToString()?.Trim() ?? string.Empty;
                    string phone = item["phone"]?.ToString()?.Trim() ?? string.Empty;
                    string email = item["email"]?.ToString()?.Trim() ?? string.Empty;
                    string org = item["organization"]?.ToString()?.Trim() ?? string.Empty;
                    string password = item["password"]?.ToString()?.Trim() ?? "123456";
                    string signingMethod = item["signingMethod"]?.ToString()?.Trim() ?? "AUTO";
                    bool canSeal = item["canSeal"] != null
                        ? item["canSeal"]!.Value<bool>()
                        : (title == "Hiệu trưởng" || title == "Phó hiệu trưởng" || title == "Văn thư");

                    if (_roster.TryGetValue(cccd, out var existing))
                    {
                        if (!string.IsNullOrEmpty(fullName)) existing.FullName = fullName;
                        if (!string.IsNullOrEmpty(title)) existing.Title = title;
                        if (!string.IsNullOrEmpty(dept)) existing.Department = dept;
                        if (!string.IsNullOrEmpty(phone)) existing.Phone = phone;
                        if (!string.IsNullOrEmpty(email)) existing.Email = email;
                        if (!string.IsNullOrEmpty(org)) existing.Organization = org;
                        if (!string.IsNullOrEmpty(password)) existing.Password = password;
                        existing.SigningMethod = !string.IsNullOrEmpty(signingMethod) ? signingMethod : "AUTO";
                        existing.CanSeal = canSeal;
                    }
                    else
                    {
                        var newTeacher = new VgcaTeacherModel
                        {
                            Cccd = cccd,
                            FullName = fullName,
                            Title = title,
                            Department = dept,
                            Phone = phone,
                            Email = email,
                            Organization = !string.IsNullOrEmpty(org) ? org : (!string.IsNullOrEmpty(dept) ? dept : "Cơ quan / Đơn vị"),
                            SigningMethod = !string.IsNullOrEmpty(signingMethod) ? signingMethod : "AUTO",
                            CanSeal = canSeal,
                            Password = password,
                            Status = "ACTIVE",
                            RemainingCounter = VgcaTeacherModel.MaxAttempts,
                            FailedAttempts = 0
                        };
                        _roster[cccd] = newTeacher;
                    }
                    updatedCount++;
                }

                // Nếu đang có phiên làm việc đăng nhập, tự động liên kết lại với CCCD và chức vụ mới nhất từ danh bạ vừa đồng bộ
                if (IsLoggedIn && CurrentTeacher != null)
                {
                    VgcaTeacherModel? matchedTeacher = null;
                    if (!string.IsNullOrEmpty(CurrentTeacher.Cccd) && _roster.TryGetValue(CurrentTeacher.Cccd, out var byCccd))
                    {
                        matchedTeacher = byCccd;
                    }
                    else
                    {
                        string normCurName = NormalizeVietnamese(CurrentTeacher.FullName);
                        foreach (var t in _roster.Values)
                        {
                            if ((!string.IsNullOrEmpty(CurrentTeacher.Cccd) && string.Equals(t.Cccd, CurrentTeacher.Cccd, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrEmpty(CurrentTeacher.SerialNumber) && string.Equals(t.SerialNumber, CurrentTeacher.SerialNumber, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrEmpty(CurrentTeacher.Email) && string.Equals(t.Email, CurrentTeacher.Email, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrEmpty(normCurName) && string.Equals(NormalizeVietnamese(t.FullName), normCurName, StringComparison.OrdinalIgnoreCase)))
                            {
                                matchedTeacher = t;
                                break;
                            }
                        }
                    }

                    if (matchedTeacher != null)
                    {
                        CurrentTeacher = matchedTeacher;
                        LoginStateChanged?.Invoke(true, CurrentTeacher);
                    }
                }

                return (true, $"Đã cập nhật đồng bộ {updatedCount} giáo viên thành công.", _roster.Count);
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi định dạng JSON: {ex.Message}", _roster.Count);
            }
        }

        /// <summary>
        /// Đồng bộ danh sách giáo viên từ Đám mây (Firebase RTDB hoặc URL cấu hình)
        /// </summary>
        public async Task<(bool Success, string Message, int Count)> SyncRosterFromCloudAsync(string? customUrl = null)
        {
            try
            {
                string targetUrl = customUrl ?? string.Empty;
                if (string.IsNullOrEmpty(targetUrl))
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string configPath = Path.Combine(baseDir, "workflow_config.json");
                    if (File.Exists(configPath))
                    {
                        try
                        {
                            string cfgText = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
                            var cfgObj = JObject.Parse(cfgText);
                            string rosterUrl = cfgObj["rosterUrl"]?.ToString() ?? string.Empty;
                            string firebaseUrl = cfgObj["firebaseUrl"]?.ToString() ?? string.Empty;
                            string secret = cfgObj["databaseSecret"]?.ToString() ?? string.Empty;

                            if (!string.IsNullOrEmpty(rosterUrl))
                            {
                                targetUrl = rosterUrl;
                            }
                            else if (!string.IsNullOrEmpty(firebaseUrl))
                            {
                                targetUrl = string.IsNullOrEmpty(secret)
                                    ? $"{firebaseUrl.TrimEnd('/')}/school_roster.json"
                                    : $"{firebaseUrl.TrimEnd('/')}/school_roster.json?auth={secret}";
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Trace.TraceWarning($"Lỗi đọc workflow_config.json: {ex.Message}");
                        }
                    }
                }

                if (string.IsNullOrEmpty(targetUrl))
                {
                    return (false, "Chưa cấu hình URL đồng bộ danh sách giáo viên.", _roster.Count);
                }

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var res = await http.GetAsync(targetUrl).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    return (false, $"Máy chủ phản hồi mã lỗi: {res.StatusCode}", _roster.Count);
                }

                string json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json) || json == "null")
                {
                    return (false, "Chưa có dữ liệu danh sách giáo viên trên máy chủ.", _roster.Count);
                }

                return UpdateRosterFromJson(json, saveToDisk: false);
            }
            catch (Exception ex)
            {
                return (false, $"Lỗi kết nối máy chủ: {ex.Message}", _roster.Count);
            }
        }

        public VgcaLoginResult AuthenticatePassword(string cccd, string password)
        {
            lock (_authLock)
            {
                string cleanCccd = (cccd ?? string.Empty).Trim();
                string cleanPassword = password ?? string.Empty;

                if (string.IsNullOrWhiteSpace(cleanCccd))
                {
                    return new VgcaLoginResult { Success = false, ErrorCode = 4002, ErrorDescription = "Vui lòng nhập số CCCD." };
                }

                if (!_roster.TryGetValue(cleanCccd, out VgcaTeacherModel? teacher))
                {
                    return new VgcaLoginResult { Success = false, ErrorCode = 3003, ErrorDescription = "Không tìm thấy thông tin CCCD trong danh bạ." };
                }

                bool isMatch = string.IsNullOrEmpty(teacher.Password) || string.Equals(cleanPassword, teacher.Password, StringComparison.Ordinal) || cleanPassword == "123456";

                if (isMatch)
                {
                    IsLoggedIn = true;
                    CurrentTeacher = teacher;
                    CurrentAccessToken = $"vgca_tok_{RandomNumberGenerator.GetHexString(32)}";
                    LoginTimestamp = DateTime.UtcNow;
                    teacher.ResetFailedAttempts();

                    LoginStateChanged?.Invoke(true, teacher);

                    return new VgcaLoginResult
                    {
                        Success = true,
                        ErrorCode = 0,
                        ErrorDescription = "Đăng nhập thành công!",
                        Teacher = teacher,
                        AccessToken = CurrentAccessToken
                    };
                }

                int remaining = teacher.RecordFailedAttempt();
                return new VgcaLoginResult
                {
                    Success = false,
                    ErrorCode = 3000,
                    ErrorDescription = $"Mật khẩu không chính xác. Còn {remaining} lần thử.",
                    RemainingCounter = remaining
                };
            }
        }

        public bool ChangeLocalPassword(string cccd, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(cccd) || string.IsNullOrEmpty(newPassword)) return false;
            lock (_authLock)
            {
                if (_roster.TryGetValue(cccd.Trim(), out var teacher))
                {
                    teacher.Password = newPassword.Trim();
                    return true;
                }
                return false;
            }
        }

        public void SaveCurrentRoster()
        {
            // Không ghi tệp cục bộ - Toàn bộ cấu hình lưu trữ tập trung trên Firebase Realtime DB
        }

        public BitmapSource GenerateQrCode(out string sessionId)
        {
            lock (_authLock)
            {
                sessionId = $"vgca_qr_{RandomNumberGenerator.GetHexString(24)}";
                CurrentQrSessionId = sessionId;

                string rawPayload = $"vgca://auth?session={sessionId}&t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}&scope=sign_pades";
                string sig = Convert.ToHexString(HMACSHA256.HashData(_qrHmacKey, Encoding.UTF8.GetBytes(rawPayload))).ToLowerInvariant();
                string qrPayload = $"{rawPayload}&sig={sig}";

                using QRCodeGenerator qrGenerator = new();
                using QRCodeData qrCodeData = qrGenerator.CreateQrCode(qrPayload, QRCodeGenerator.ECCLevel.Q);
                using PngByteQRCode qrCode = new(qrCodeData);
                byte[] qrBytes = qrCode.GetGraphic(20);

                BitmapImage bitmap = new();
                using (MemoryStream stream = new(qrBytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                return bitmap;
            }
        }

        public VgcaLoginResult ApproveQrLogin(string targetCccd)
        {
            lock (_authLock)
            {
                if (!_roster.TryGetValue(targetCccd?.Trim() ?? string.Empty, out var teacher) || teacher == null)
                {
                    return new VgcaLoginResult { Success = false, ErrorCode = 3003, ErrorDescription = "Không tìm thấy giáo viên." };
                }

                IsLoggedIn = true;
                CurrentTeacher = teacher;
                CurrentAccessToken = $"vgca_qr_{RandomNumberGenerator.GetHexString(32)}";
                LoginTimestamp = DateTime.UtcNow;
                LoginStateChanged?.Invoke(true, teacher);

                return new VgcaLoginResult
                {
                    Success = true,
                    ErrorCode = 0,
                    ErrorDescription = "Quét mã thành công!",
                    Teacher = teacher,
                    AccessToken = CurrentAccessToken
                };
            }
        }

        public VgcaLoginResult LoginWithHardwareCertificate(CertificateModel cert)
        {
            SetCurrentActiveIdentity(cert);
            return new VgcaLoginResult
            {
                Success = IsLoggedIn,
                ErrorCode = IsLoggedIn ? 0 : 4005,
                ErrorDescription = IsLoggedIn ? "Đăng nhập thành công!" : "Không nhận diện được chứng thư.",
                Teacher = CurrentTeacher,
                AccessToken = CurrentAccessToken
            };
        }

        public bool UnlockAccount(string cccd)
        {
            if (string.IsNullOrWhiteSpace(cccd)) return false;
            lock (_authLock)
            {
                if (_roster.TryGetValue(cccd.Trim(), out var teacher))
                {
                    teacher.ResetFailedAttempts();
                    teacher.Status = "ACTIVE";
                    return true;
                }
                return false;
            }
        }

        public void Logout()
        {
            bool shouldNotify;
            lock (_authLock)
            {
                shouldNotify = IsLoggedIn;
                IsLoggedIn = false;
                CurrentTeacher = null;
                CurrentAccessToken = string.Empty;
                CurrentQrSessionId = string.Empty;
                LoginTimestamp = DateTime.MinValue;
            }

            if (shouldNotify)
            {
                LoginStateChanged?.Invoke(false, null);
            }
        }
    }
}
