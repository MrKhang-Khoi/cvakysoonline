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

        public List<SignatureProfile> LoadProfiles()
        {
            try
            {
                // 1. Ưu tiên đọc cấu hình lưu riêng của VGCASignToolPro
                if (File.Exists(ProfilesFilePath))
                {
                    string json = File.ReadAllText(ProfilesFilePath);
                    var list = JsonConvert.DeserializeObject<List<SignatureProfile>>(json);
                    if (list != null && list.Count > 0) return list;
                }

                // 2. Kế thừa cấu hình từ VGCA Sign Tool gốc nếu có
                if (File.Exists(OriginalVgcaProfilesPath))
                {
                    string json = File.ReadAllText(OriginalVgcaProfilesPath);
                    var list = JsonConvert.DeserializeObject<List<SignatureProfile>>(json);
                    if (list != null && list.Count > 0)
                    {
                        SaveProfiles(list);
                        return list;
                    }
                }
            }
            catch { }

            // 3. Khởi tạo danh sách mẫu mặc định chuẩn Ban Cơ yếu & Văn bản hành chính
            var defaults = GetDefaultProfiles();
            SaveProfiles(defaults);
            return defaults;
        }

        public void SaveProfiles(List<SignatureProfile> profiles)
        {
            try
            {
                if (!Directory.Exists(AppDataFolder))
                {
                    Directory.CreateDirectory(AppDataFolder);
                }
                string json = JsonConvert.SerializeObject(profiles, Formatting.Indented);
                File.WriteAllText(ProfilesFilePath, json);
            }
            catch { }
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
