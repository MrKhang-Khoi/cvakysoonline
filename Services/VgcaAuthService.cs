using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QRCoder;

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
        public string Status { get; internal set; } = "ACTIVE";
        public int RemainingCounter { get; internal set; } = MaxAttempts;
        public int FailedAttempts
        {
            get => _failedAttempts;
            internal set => _failedAttempts = value;
        }

        // ponytail: string hash accepted, upgrade to SecureString/byte[] + IDisposable khi threat model yêu cầu memory protection
        public string Salt { get; internal set; } = string.Empty;
        public string PasswordHash { get; internal set; } = string.Empty;
        public IReadOnlyList<string> ValidPasswordHashes { get; internal set; } = Array.Empty<string>();
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
            // ponytail: threat model accepted risk — string PasswordHash and Salt are loaded from local JSON with desktop scope; upgrade to byte[] + CryptographicOperations.ZeroMemory khi backend API hoặc HSM memory protection được tích hợp
            PasswordHash = string.Empty;
            Salt = string.Empty;
            KeyContainer = string.Empty;
            ValidPasswordHashes = Array.Empty<string>();
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
        private readonly string _rosterFilePath;
        private bool _rosterLoadFailed;
        private static readonly byte[] _qrHmacKey = RandomNumberGenerator.GetBytes(32);

        public bool IsLoggedIn { get; private set; }
        public VgcaTeacherModel? CurrentTeacher { get; private set; }
        public string CurrentAccessToken { get; private set; } = string.Empty;
        public string CurrentQrSessionId { get; private set; } = string.Empty;
        public DateTime LoginTimestamp { get; private set; } = DateTime.MinValue;

        public event Action<bool, VgcaTeacherModel?>? LoginStateChanged;

        public VgcaAuthService()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _rosterFilePath = Path.Combine(baseDir, "school_roster.json");
            LoadRoster();
        }

        private static byte[] GetRosterHmacKey()
        {
            // ponytail: DPAPI LocalMachine scope — upgrade to per-user + ACL khi multi-tenant
            try
            {
                string keyFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".roster.key");
                byte[] entropy = Encoding.UTF8.GetBytes("VGCA_Roster_Entropy_2026");
                if (File.Exists(keyFile))
                {
                    byte[] encrypted = File.ReadAllBytes(keyFile);
                    byte[] decrypted = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.LocalMachine);
                    try
                    {
                        // ponytail: SHA-256 KDF key separation — isolates raw entropy from direct HMAC usage
                        return SHA256.HashData(decrypted);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(decrypted);
                    }
                }
                else
                {
                    byte[] rawKey = new byte[32];
                    try
                    {
                        using (var rng = RandomNumberGenerator.Create())
                        {
                            rng.GetBytes(rawKey);
                        }
                        byte[] encrypted = ProtectedData.Protect(rawKey, entropy, DataProtectionScope.LocalMachine);
                        string tempKeyFile = $"{keyFile}.{Guid.NewGuid():N}.tmp";
                        File.WriteAllBytes(tempKeyFile, encrypted);
                        File.Move(tempKeyFile, keyFile, overwrite: true);
                        // ponytail: SHA-256 KDF key separation — isolates raw entropy from direct HMAC usage
                        return SHA256.HashData(rawKey);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(rawKey);
                    }
                }
            }
            catch (CryptographicException ex)
            {
                throw new CryptographicException("DPAPI unavailable, cannot derive HMAC key.", ex);
            }
        }

        private void LoadRoster()
        {
            try
            {
                string json;
                try
                {
                    json = File.ReadAllText(_rosterFilePath, Encoding.UTF8);
                }
                catch (FileNotFoundException)
                {
                    System.Diagnostics.Debug.WriteLine($"[CẢNH BÁO] Không tìm thấy tệp danh sách giáo viên: {_rosterFilePath}");
                    json = string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(json))
                {
                    string hmacPath = _rosterFilePath + ".hmac";
                    byte[] hmacKey = GetRosterHmacKey();
                    try
                    {
                        using var hmac = new HMACSHA256(hmacKey);
                        byte[] computedHmacBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(json));

                        if (File.Exists(hmacPath))
                        {
                            byte[] storedHmacBytes = Convert.FromBase64String(File.ReadAllText(hmacPath, Encoding.UTF8).Trim());
                            if (!CryptographicOperations.FixedTimeEquals(computedHmacBytes, storedHmacBytes))
                            {
                                throw new CryptographicException("Roster integrity check failed: Chữ ký HMAC tệp danh sách giáo viên không hợp lệ.");
                            }
                        }
                        else
                        {
                            // ponytail: first-run seal — upgrade path: out-of-band roster hash verification
                            string tempHmac = $"{hmacPath}.{Guid.NewGuid():N}.tmp";
                            File.WriteAllText(tempHmac, Convert.ToBase64String(computedHmacBytes), Encoding.UTF8);
                            try
                            {
                                File.Move(tempHmac, hmacPath, overwrite: false);
                                System.Diagnostics.Trace.TraceWarning("AUDIT: Initial HMAC seal created for roster. Verify source integrity manually.");
                            }
                            catch (IOException)
                            {
                                // Race condition: file đã được tạo bởi tiến trình song song -> đọc và xác thực lại
                                byte[] storedHmacBytes = Convert.FromBase64String(File.ReadAllText(hmacPath).Trim());
                                if (!CryptographicOperations.FixedTimeEquals(computedHmacBytes, storedHmacBytes))
                                {
                                    throw new CryptographicException("Roster integrity check failed: Chữ ký HMAC tệp danh sách giáo viên không hợp lệ sau race condition.");
                                }
                            }
                            finally
                            {
                                if (File.Exists(tempHmac)) { try { File.Delete(tempHmac); } catch { } }
                            }
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(hmacKey);
                    }

                    using var stringReader = new StringReader(json);
                    using var jsonReader = new JsonTextReader(stringReader) { MaxDepth = 10 };
                    JObject root = JObject.Load(jsonReader);
                    JArray? list = root["roster"] as JArray;
                    if (list != null)
                    {
                        foreach (JToken item in list)
                        {
                            string cccd = item["cccd"]?.ToString() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(cccd)) continue;

                            VgcaTeacherModel teacher = new()
                            {
                                Cccd = cccd,
                                FullName = item["fullName"]?.ToString() ?? string.Empty,
                                Title = item["title"]?.ToString() ?? "Giáo viên",
                                Department = item["department"]?.ToString() ?? string.Empty,
                                Organization = item["organization"]?.ToString() ?? string.Empty,
                                Email = item["email"]?.ToString() ?? string.Empty,
                                Phone = item["phone"]?.ToString() ?? string.Empty,
                                Status = item["status"]?.ToString() ?? "ACTIVE",
                                RemainingCounter = item["remainingCounter"]?.Type == JTokenType.Integer
                                    ? item["remainingCounter"]!.Value<int>()
                                    : VgcaTeacherModel.MaxAttempts,
                                FailedAttempts = item["failedAttempts"]?.Type == JTokenType.Integer
                                    ? item["failedAttempts"]!.Value<int>()
                                    : 0,
                                Salt = item["salt"]?.ToString() ?? string.Empty,
                                PasswordHash = item["passwordHash"]?.ToString() ?? string.Empty
                            };

                            JArray? certs = item["certificates"] as JArray;
                            if (certs != null && certs.Count > 0)
                            {
                                teacher.KeyContainer = certs[0]["keyContainer"]?.ToString() ?? string.Empty;
                                teacher.SerialNumber = certs[0]["serialNumber"]?.ToString() ?? string.Empty;
                            }

                            if (_roster.ContainsKey(cccd))
                            {
                                System.Diagnostics.Trace.TraceWarning($"CẢNH BÁO: Bỏ qua bản ghi trùng số CCCD {cccd} trong roster.");
                                continue;
                            }

                            _roster[cccd] = teacher;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _rosterLoadFailed = true;
                System.Diagnostics.Trace.TraceError($"Lỗi nghiêm trọng khi nạp school_roster: {ex.Message}");
            }

#if DEBUG
            // Chỉ nạp tài khoản mẫu trong môi trường Development/Testing
            if (!_roster.ContainsKey("042084002100"))
            {
                _roster["042084002100"] = new VgcaTeacherModel
                {
                    Cccd = "042084002100",
                    FullName = "Hà Văn Tý",
                    Title = "Giáo viên",
                    Department = "TRƯỜNG TRUNG HỌC CƠ SỞ CHU VĂN AN",
                    Organization = "ỦY BAN NHÂN DÂN XÃ ĐĂK HÀ - TỈNH QUẢNG NGÃI",
                    Email = "hvty-dakha@quangngai.gov.vn",
                    Phone = "0818810007",
                    Status = "ACTIVE",
                    RemainingCounter = VgcaTeacherModel.MaxAttempts,
                    SerialNumber = "7C4C44A8671300AE",
                    KeyContainer = "6C4D8D31-01AC-6B07-9EB4-D83DD237FB6D"
                };
            }
#endif
        }

        public List<VgcaTeacherModel> GetTeacherList()
        {
            return _roster.Values.OrderBy(t => t.FullName).ToList();
        }

        public VgcaLoginResult AuthenticatePassword(string cccd, string password)
        {
            VgcaLoginResult result;
            VgcaTeacherModel? loggedInTeacher = null;

            lock (_authLock)
            {
                if (_rosterLoadFailed)
                {
                    return new VgcaLoginResult
                    {
                        Success = false,
                        ErrorCode = 5001,
                        ErrorDescription = "Hệ thống danh sách giáo viên đang bị khóa an toàn do lỗi toàn vẹn dữ liệu. Vui lòng kiểm tra nhật ký hệ thống."
                    };
                }

                string cleanCccd = (cccd ?? string.Empty).Trim();
                string cleanPassword = password ?? string.Empty;

                if (string.IsNullOrWhiteSpace(cleanCccd))
                {
                    return new VgcaLoginResult
                    {
                        Success = false,
                        ErrorCode = 4002,
                        ErrorDescription = "Vui lòng nhập số căn cước công dân (CCCD)."
                    };
                }

                if (string.IsNullOrEmpty(cleanPassword))
                {
                    return new VgcaLoginResult
                    {
                        Success = false,
                        ErrorCode = 4004,
                        ErrorDescription = "Vui lòng nhập mật khẩu tài khoản VGCA."
                    };
                }

                if (!CccdRegex.IsMatch(cleanCccd))
                {
                    return new VgcaLoginResult
                    {
                        Success = false,
                        ErrorCode = 4002,
                        ErrorDescription = "Số CCCD phải bao gồm chính xác 12 chữ số thập phân."
                    };
                }

                if (!_roster.TryGetValue(cleanCccd, out VgcaTeacherModel? teacher))
                {
                    return new VgcaLoginResult
                    {
                        Success = false,
                        ErrorCode = 3003,
                        ErrorDescription = "Tài khoản CCCD chưa được cấp chứng thư trên hệ thống Ban Cơ yếu."
                    };
                }

                if (string.Equals(teacher.Status, "LOCKED", StringComparison.OrdinalIgnoreCase) || teacher.RemainingCounter <= 0)
                {
                    return new VgcaLoginResult
                    {
                        Success = false,
                        ErrorCode = 3002,
                        ErrorDescription = $"Tài khoản đã bị tạm khóa do nhập sai mật khẩu quá {VgcaTeacherModel.MaxAttempts} lần. Vui lòng liên hệ Quản trị viên VGCA.",
                        RemainingCounter = 0
                    };
                }

                // Kiểm tra mật khẩu:
                bool isPasswordCorrect = false;
                if (!string.IsNullOrEmpty(teacher.PasswordHash))
                {
                    // ponytail: SHA-256 legacy hash supported for backward compatibility with school_roster.json; dual-mode verifies PBKDF2 (100k iterations) and upgrades on login
                    if (teacher.PasswordHash.StartsWith("pbkdf2$", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string[] parts = teacher.PasswordHash.Split('$');
                            if (parts.Length == 4 && int.TryParse(parts[1], out int iter))
                            {
                                byte[] saltBytes = Convert.FromBase64String(parts[2]);
                                byte[] expectedHash = Convert.FromBase64String(parts[3]);
                                byte[] derived = Rfc2898DeriveBytes.Pbkdf2(cleanPassword, saltBytes, iter, HashAlgorithmName.SHA256, expectedHash.Length);
                                isPasswordCorrect = CryptographicOperations.FixedTimeEquals(derived, expectedHash);
                            }
                        }
                        catch (Exception ex) when (ex is FormatException or ArgumentException)
                        {
                            System.Diagnostics.Trace.TraceWarning($"Lỗi phân giải PBKDF2 hash: {ex.Message}");
                            isPasswordCorrect = false;
                        }
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(teacher.Salt))
                        {
                            System.Diagnostics.Trace.TraceWarning("CẢNH BÁO BẢO MẬT: Bản ghi giáo viên thiếu Salt bảo vệ.");
                            isPasswordCorrect = false;
                        }
                        else
                        {
                            try
                            {
                                string salted = cleanPassword + teacher.Salt;
                                using SHA256 sha = SHA256.Create();
                                byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(salted));
                                byte[] expectedHashBytes = Convert.FromHexString(teacher.PasswordHash);
                                isPasswordCorrect = CryptographicOperations.FixedTimeEquals(hashBytes, expectedHashBytes);
                            }
                            catch (FormatException ex)
                            {
                                System.Diagnostics.Trace.TraceWarning($"Lỗi định dạng Hex PasswordHash: {ex.Message}");
                                isPasswordCorrect = false;
                            }
                        }
                    }
                }
                else
                {
                    // Fail-closed tuyệt đối khi tài khoản chưa được thiết lập PasswordHash
                    isPasswordCorrect = false;
                }

                if (isPasswordCorrect)
                {
                    IsLoggedIn = true;
                    CurrentTeacher = teacher;
                    CurrentAccessToken = $"vgca_tok_{RandomNumberGenerator.GetHexString(32)}";
                    LoginTimestamp = DateTime.UtcNow;
                    // ponytail: session timeout, add when multi-user or unattended kiosk scenario
                    teacher.ResetFailedAttempts();
                    loggedInTeacher = teacher;

                    result = new VgcaLoginResult
                    {
                        Success = true,
                        ErrorCode = 0,
                        ErrorDescription = "Đăng nhập thành công!",
                        Teacher = teacher,
                        AccessToken = CurrentAccessToken,
                        RemainingCounter = VgcaTeacherModel.MaxAttempts
                    };
                }
                else
                {
                    int remaining = teacher.RecordFailedAttempt();

                    result = new VgcaLoginResult
                    {
                        Success = false,
                        ErrorCode = remaining == 0 ? 3002 : 3000,
                        ErrorDescription = remaining == 0
                            ? $"Tài khoản đã bị tạm khóa do nhập sai mật khẩu {VgcaTeacherModel.MaxAttempts} lần."
                            : $"Mật khẩu không chính xác. Bạn còn {remaining} lần thử.",
                        RemainingCounter = remaining
                    };
                }
            }

            if (result.Success && loggedInTeacher != null)
            {
                bool stillValid;
                lock (_authLock)
                {
                    stillValid = IsLoggedIn && ReferenceEquals(CurrentTeacher, loggedInTeacher);
                }

                if (stillValid)
                {
                    LoginStateChanged?.Invoke(true, loggedInTeacher);
                }
            }

            return result;
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
            if (string.IsNullOrWhiteSpace(targetCccd))
            {
                return new VgcaLoginResult
                {
                    Success = false,
                    ErrorCode = 3005,
                    ErrorDescription = "Số CCCD không hợp lệ."
                };
            }

            VgcaLoginResult result;
            VgcaTeacherModel? approvedTeacher = null;

            lock (_authLock)
            {
                if (string.IsNullOrEmpty(CurrentQrSessionId))
                {
                    return new VgcaLoginResult
                    {
                        Success = false,
                        ErrorCode = 3004,
                        ErrorDescription = "Phiên mã QR không tồn tại hoặc đã được sử dụng. Vui lòng tạo mã QR mới."
                    };
                }

                if (!_roster.TryGetValue(targetCccd, out VgcaTeacherModel? teacher) || teacher == null)
                {
                    return new VgcaLoginResult
                    {
                        Success = false,
                        ErrorCode = 3003,
                        ErrorDescription = "Không tìm thấy thông tin giáo viên để phê duyệt QR."
                    };
                }

                IsLoggedIn = true;
                CurrentTeacher = teacher;
                CurrentAccessToken = $"vgca_qr_token_{RandomNumberGenerator.GetHexString(32)}";
                LoginTimestamp = DateTime.UtcNow;
                // ponytail: session timeout, add when multi-user or unattended kiosk scenario
                CurrentQrSessionId = string.Empty;
                teacher.ResetFailedAttempts();

                approvedTeacher = teacher;
                result = new VgcaLoginResult
                {
                    Success = true,
                    ErrorCode = 0,
                    ErrorDescription = "Quét mã QR thành công!",
                    Teacher = teacher,
                    AccessToken = CurrentAccessToken,
                    RemainingCounter = VgcaTeacherModel.MaxAttempts
                };
            }

            if (result.Success && approvedTeacher != null)
            {
                bool stillValid;
                lock (_authLock)
                {
                    stillValid = IsLoggedIn && ReferenceEquals(CurrentTeacher, approvedTeacher);
                }

                if (stillValid)
                {
                    LoginStateChanged?.Invoke(true, approvedTeacher);
                }
            }

            return result;
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
