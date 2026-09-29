using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using VGCASignToolPro.Models;

namespace VGCASignToolPro.Services
{
    public class ProfileManager
    {
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
    }
}
