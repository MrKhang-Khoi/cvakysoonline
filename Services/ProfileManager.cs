using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VGCASignToolPro.Models;

namespace VGCASignToolPro.Services
{
    public class ProfileManager
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        private static readonly byte[] SystemSalt = Encoding.UTF8.GetBytes("VGCA_CVA_SIGN_PROFILES_VAULT_2026");

        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VGCASignToolPro");

        private static readonly string ProfilesFilePath = Path.Combine(AppDataFolder, "signerprofiles.json");

        private static readonly string OriginalVgcaProfilesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VGCA", "SignTool", "signerprofiles.json");
        private static readonly string SignaturesVaultFolder = Path.Combine(AppDataFolder, "Signatures");

        public List<SignatureProfile> LoadProfiles()
        {
            List<SignatureProfile>? list = null;
            try
            {
                // 1. Ưu tiên đọc cấu hình lưu riêng của VGCASignToolPro
                if (File.Exists(ProfilesFilePath))
                {
                    string json = File.ReadAllText(ProfilesFilePath);
                    list = JsonConvert.DeserializeObject<List<SignatureProfile>>(json);
                }

                // 2. Kế thừa cấu hình từ VGCA Sign Tool gốc nếu có
                if ((list == null || list.Count == 0) && File.Exists(OriginalVgcaProfilesPath))
                {
                    string json = File.ReadAllText(OriginalVgcaProfilesPath);
                    list = JsonConvert.DeserializeObject<List<SignatureProfile>>(json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi nạp cấu hình chữ ký từ file: {ex}");
                try
                {
                    if (File.Exists(ProfilesFilePath))
                    {
                        string backupPath = ProfilesFilePath + ".bak";
                        File.Copy(ProfilesFilePath, backupPath, overwrite: true);
                    }
                }
                catch (Exception backupEx)
                {
                    System.Diagnostics.Trace.TraceWarning($"Không thể sao lưu file profiles hỏng: {backupEx.Message}");
                }
            }

            if (list == null || list.Count == 0)
            {
                // 3. Khởi tạo danh sách mẫu mặc định chuẩn Ban Cơ yếu & Văn bản hành chính
                list = GetDefaultProfiles();
            }

            // Tự động khôi phục ảnh con dấu/chữ ký từ Base64 hoặc đồng bộ sang App Vault
            EnsureProfileImagesRestored(list);
            SaveProfiles(list);
            return list;
        }

        public void SaveProfiles(List<SignatureProfile> profiles)
        {
            try
            {
                if (!Directory.Exists(AppDataFolder))
                {
                    Directory.CreateDirectory(AppDataFolder);
                }
                if (!Directory.Exists(SignaturesVaultFolder))
                {
                    Directory.CreateDirectory(SignaturesVaultFolder);
                }

                // Đảm bảo tất cả các profile có ảnh đều đã được mã hóa Base64 vào JSON
                foreach (var p in profiles)
                {
                    if (!string.IsNullOrWhiteSpace(p.ImagePath) && File.Exists(p.ImagePath))
                    {
                        if (string.IsNullOrWhiteSpace(p.Image))
                        {
                            try
                            {
                                byte[] bytes = File.ReadAllBytes(p.ImagePath);
                                p.Image = Convert.ToBase64String(bytes);
                            }
                            catch (Exception imgEx)
                            {
                                System.Diagnostics.Trace.TraceWarning($"Không thể mã hóa Base64 ảnh con dấu cho mẫu {p.Name}: {imgEx.Message}");
                            }
                        }
                    }
                }

                string json = JsonConvert.SerializeObject(profiles, Formatting.Indented);
                File.WriteAllText(ProfilesFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi lưu cấu hình chữ ký vào file: {ex}");
            }
        }

        /// <summary>
        /// Tự động phục hồi ảnh con dấu / chữ ký từ Base64 nếu file ảnh vật lý trên ổ đĩa bị mất hoặc xóa nhầm
        /// </summary>
        public void EnsureProfileImagesRestored(List<SignatureProfile> profiles)
        {
            try
            {
                if (!Directory.Exists(SignaturesVaultFolder))
                {
                    Directory.CreateDirectory(SignaturesVaultFolder);
                }

                foreach (var p in profiles)
                {
                    string safeName = SanitizeFileName(string.IsNullOrWhiteSpace(p.Name) ? "Signature" : p.Name);
                    string vaultPath = Path.Combine(SignaturesVaultFolder, $"{safeName}.png");

                    // Trường hợp 1: Có Base64 nhưng file ảnh trên máy bị mất
                    if (!string.IsNullOrWhiteSpace(p.Image) && (string.IsNullOrWhiteSpace(p.ImagePath) || !File.Exists(p.ImagePath)))
                    {
                        try
                        {
                            string base64Data = p.Image;
                            if (base64Data.Contains(","))
                            {
                                base64Data = base64Data.Substring(base64Data.IndexOf(',') + 1);
                            }
                            byte[] bytes = Convert.FromBase64String(base64Data.Trim());
                            File.WriteAllBytes(vaultPath, bytes);
                            p.ImagePath = vaultPath;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Trace.TraceWarning($"Lỗi khôi phục ảnh từ Base64 cho mẫu {p.Name}: {ex.Message}");
                        }
                    }
                    // Trường hợp 2: Có file ảnh hợp lệ nhưng chưa lưu Base64 hoặc chưa copy vào Vault
                    else if (!string.IsNullOrWhiteSpace(p.ImagePath) && File.Exists(p.ImagePath))
                    {
                        if (string.IsNullOrWhiteSpace(p.Image))
                        {
                            try
                            {
                                byte[] bytes = File.ReadAllBytes(p.ImagePath);
                                p.Image = Convert.ToBase64String(bytes);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Trace.TraceWarning($"Lỗi đọc file ảnh mẫu {p.Name}: {ex.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi trong EnsureProfileImagesRestored: {ex.Message}");
            }
        }

        /// <summary>
        /// Lưu trữ an toàn hình ảnh con dấu / chữ ký vào kho App Vault và nhúng Base64 vào Profile
        /// </summary>
        public string PersistStampImageToVault(SignatureProfile profile, string sourceFilePath)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            {
                return string.Empty;
            }

            if (!Directory.Exists(SignaturesVaultFolder))
            {
                Directory.CreateDirectory(SignaturesVaultFolder);
            }

            string safeName = SanitizeFileName(string.IsNullOrWhiteSpace(profile.Name) ? "Stamp" : profile.Name);
            string ext = Path.GetExtension(sourceFilePath);
            if (string.IsNullOrEmpty(ext)) ext = ".png";
            string targetPath = Path.Combine(SignaturesVaultFolder, $"{safeName}{ext}");

            try
            {
                File.Copy(sourceFilePath, targetPath, overwrite: true);
                byte[] bytes = File.ReadAllBytes(targetPath);
                profile.Image = Convert.ToBase64String(bytes);
                profile.ImagePath = targetPath;
                return targetPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi sao chép ảnh vào App Vault: {ex.Message}");
                return sourceFilePath;
            }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name.Trim();
        }

        public static List<SignatureProfile> GetDefaultProfiles()
        {
            return new List<SignatureProfile>
            {
                new SignatureProfile
                {
                    Name = "Ký Cá Nhân / Thủ Trưởng",
                    ProfileType = 3,
                    Mode = 1,
                    SignatureWidth = 140,
                    SignatureHeight = 70,
                    FontSize = 10,
                    ShowDate = true,
                    ShowOrg = true,
                    ShowLabel = true
                },
                new SignatureProfile
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
                },
                new SignatureProfile
                {
                    Name = "Ký Nháy / Ký Duyệt",
                    ProfileType = 3,
                    Mode = 1,
                    SignatureWidth = 80,
                    SignatureHeight = 40,
                    FontSize = 8,
                    ShowDate = false,
                    ShowOrg = false,
                    ShowLabel = false
                },
                new SignatureProfile
                {
                    Name = "Chèn Số Văn Bản",
                    ProfileType = 1,
                    Mode = 0,
                    SignatureWidth = 100,
                    SignatureHeight = 40,
                    FontSize = 11,
                    ShowDate = false,
                    ShowOrg = false,
                    ShowLabel = false
                },
                new SignatureProfile
                {
                    Name = "Chèn Ngày Tháng",
                    ProfileType = 2,
                    Mode = 0,
                    SignatureWidth = 100,
                    SignatureHeight = 40,
                    FontSize = 11,
                    ShowDate = true,
                    ShowOrg = false,
                    ShowLabel = false
                }
            };
        }

        private static (string Url, string Secret) GetFirebaseConfig()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workflow_config.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var obj = JObject.Parse(json);
                    string url = obj["firebaseUrl"]?.ToString() ?? "https://cva-kyso-lienthong-default-rtdb.asia-southeast1.firebasedatabase.app";
                    string sec = obj["databaseSecret"]?.ToString() ?? "TOdsGaw1iflR0dCDtHqQ8pBii9UgiqLaq8nfF250";
                    return (url.TrimEnd('/'), sec.Trim());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Lỗi đọc cấu hình Firebase trong ProfileManager: {ex.Message}");
            }
            return ("https://cva-kyso-lienthong-default-rtdb.asia-southeast1.firebasedatabase.app", "TOdsGaw1iflR0dCDtHqQ8pBii9UgiqLaq8nfF250");
        }

        private static byte[] DeriveAesKey(string cccd)
        {
            string cleanId = new string(cccd.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrEmpty(cleanId)) cleanId = "DEFAULT_SIGNER_ID";
            using var kdf = new Rfc2898DeriveBytes(cleanId, SystemSalt, 10000, HashAlgorithmName.SHA256);
            return kdf.GetBytes(32); // AES-256 (256-bit key)
        }

        /// <summary>
        /// Mã hóa AES-256 GCM và sao lưu toàn bộ mẫu chữ ký kèm ảnh con dấu lên Firebase Realtime Database
        /// </summary>
        public async Task<bool> BackupProfilesToCloudAsync(string cccd, List<SignatureProfile> profiles)
        {
            if (string.IsNullOrWhiteSpace(cccd) || profiles == null || profiles.Count == 0)
                return false;

            try
            {
                // Đảm bảo tất cả các profile có ảnh đều đã được mã hóa Base64 vào model
                foreach (var p in profiles)
                {
                    if (!string.IsNullOrEmpty(p.ImagePath) && File.Exists(p.ImagePath) && string.IsNullOrEmpty(p.Image))
                    {
                        try
                        {
                            p.Image = Convert.ToBase64String(File.ReadAllBytes(p.ImagePath));
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Trace.TraceWarning($"Không thể đọc ảnh profile {p.Name}: {ex.Message}");
                        }
                    }
                }

                string rawJson = JsonConvert.SerializeObject(profiles, Formatting.None);
                byte[] rawBytes = Encoding.UTF8.GetBytes(rawJson);

                // Tính mã băm toàn vẹn SHA-256
                string sha256Hash;
                using (var sha = SHA256.Create())
                {
                    sha256Hash = BitConverter.ToString(sha.ComputeHash(rawBytes)).Replace("-", "").ToUpperInvariant();
                }

                byte[] key = DeriveAesKey(cccd);
                byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
                RandomNumberGenerator.Fill(nonce);

                byte[] cipherBytes = new byte[rawBytes.Length];
                byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes

                using (var aesGcm = new AesGcm(key, AesGcm.TagByteSizes.MaxSize))
                {
                    aesGcm.Encrypt(nonce, rawBytes, cipherBytes, tag);
                }

                string cleanId = new string(cccd.Where(char.IsLetterOrDigit).ToArray());
                var payload = new
                {
                    version = 2,
                    cccd = cleanId,
                    updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    iv = Convert.ToBase64String(nonce),
                    tag = Convert.ToBase64String(tag),
                    ciphertext = Convert.ToBase64String(cipherBytes),
                    hash = sha256Hash
                };

                var (firebaseUrl, secret) = GetFirebaseConfig();
                string endpoint = $"{firebaseUrl}/user_profiles/{cleanId}/signature_backup.json?auth={secret}";

                string payloadJson = JsonConvert.SerializeObject(payload);
                using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                var response = await _httpClient.PutAsync(endpoint, content);
                if (response.IsSuccessStatusCode)
                {
                    VGCASignToolPro.Services.AppLogger.Info($"Đã sao lưu an toàn {profiles.Count} mẫu chữ ký (AES-256 GCM) lên Cloud cho CCCD: {cleanId}", "ProfileManager");
                    return true;
                }
                else
                {
                    System.Diagnostics.Trace.TraceWarning($"Lỗi HTTP khi sao lưu profile lên Cloud: {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi sao lưu profiles lên đám mây: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Tải về và giải mã AES-256 GCM mẫu chữ ký kèm ảnh con dấu từ Firebase Realtime Database
        /// </summary>
        public async Task<List<SignatureProfile>?> RestoreProfilesFromCloudAsync(string cccd)
        {
            if (string.IsNullOrWhiteSpace(cccd)) return null;

            try
            {
                string cleanId = new string(cccd.Where(char.IsLetterOrDigit).ToArray());
                var (firebaseUrl, secret) = GetFirebaseConfig();
                string endpoint = $"{firebaseUrl}/user_profiles/{cleanId}/signature_backup.json?auth={secret}";

                var response = await _httpClient.GetAsync(endpoint);
                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null") return null;

                var jObj = JObject.Parse(json);
                string? cipherBase64 = jObj["ciphertext"]?.ToString();
                string? ivBase64 = jObj["iv"]?.ToString();
                string? tagBase64 = jObj["tag"]?.ToString();
                string? expectedHash = jObj["hash"]?.ToString();

                if (string.IsNullOrEmpty(cipherBase64) || string.IsNullOrEmpty(ivBase64) || string.IsNullOrEmpty(tagBase64))
                {
                    System.Diagnostics.Trace.TraceWarning("Dữ liệu sao lưu trên Cloud thiếu thành phần mật mã bắt buộc.");
                    return null;
                }

                // Kiểm tra ràng buộc mã băm: nếu expectedHash rỗng -> FAIL (Logic Bug Pattern 5 & 7)
                if (string.IsNullOrEmpty(expectedHash))
                {
                    System.Diagnostics.Trace.TraceError("Thiếu mã băm SHA-256 kiểm tra toàn vẹn trên Cloud!");
                    return null;
                }

                byte[] cipherBytes = Convert.FromBase64String(cipherBase64);
                byte[] nonce = Convert.FromBase64String(ivBase64);
                byte[] tag = Convert.FromBase64String(tagBase64);
                byte[] key = DeriveAesKey(cleanId);

                byte[] plainBytes = new byte[cipherBytes.Length];
                using (var aesGcm = new AesGcm(key, tag.Length))
                {
                    aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
                }

                // Xác thực mã băm SHA-256
                using (var sha = SHA256.Create())
                {
                    string actualHash = BitConverter.ToString(sha.ComputeHash(plainBytes)).Replace("-", "").ToUpperInvariant();
                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        System.Diagnostics.Trace.TraceError("Dữ liệu sao lưu trên đám mây bị sai lệch tính toàn vẹn SHA-256!");
                        return null;
                    }
                }

                string rawJson = Encoding.UTF8.GetString(plainBytes);
                var list = JsonConvert.DeserializeObject<List<SignatureProfile>>(rawJson);
                if (list != null && list.Count > 0)
                {
                    // Tự động khôi phục ảnh từ Base64 vào App Vault cục bộ
                    EnsureProfileImagesRestored(list);
                    SaveProfiles(list);
                    VGCASignToolPro.Services.AppLogger.Info($"Đã khôi phục thành công {list.Count} mẫu chữ ký từ Cloud cho CCCD: {cleanId}", "ProfileManager");
                    return list;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"Lỗi khôi phục profiles từ đám mây: {ex}");
            }
            return null;
        }
    }
}
