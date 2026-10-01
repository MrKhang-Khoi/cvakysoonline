using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace VGCASignToolPro.Services
{
    public class SavedLoginCredentials
    {
        public bool RememberMe { get; set; } = true;
        public string Cccd { get; set; } = string.Empty;
        public string EncryptedPassword { get; set; } = string.Empty;
    }

    /// <summary>
    /// Dịch vụ lưu trữ an toàn thông tin đăng nhập tài khoản giáo viên (CCCD & Mật khẩu mã hóa AES-256)
    /// </summary>
    public static class CredentialStorageService
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VGCA_CREDENTIAL_STORAGE_KEY_2026_CVA_PROTECT");

        private static string GetStoragePath()
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VGCASignToolPro");
            if (!Directory.Exists(appData))
            {
                Directory.CreateDirectory(appData);
            }
            return Path.Combine(appData, "saved_account.json");
        }

        public static void SaveCredentials(string cccd, string password)
        {
            try
            {
                string encrypted = EncryptString(password);
                var model = new SavedLoginCredentials
                {
                    RememberMe = true,
                    Cccd = cccd,
                    EncryptedPassword = encrypted
                };
                string json = JsonConvert.SerializeObject(model, Formatting.Indented);
                File.WriteAllText(GetStoragePath(), json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Lỗi lưu thông tin đăng nhập: {ex.Message}");
            }
        }

        public static (bool HasSaved, string Cccd, string Password) LoadCredentials()
        {
            try
            {
                string path = GetStoragePath();
                if (!File.Exists(path)) return (false, string.Empty, string.Empty);

                string json = File.ReadAllText(path, Encoding.UTF8);
                var model = JsonConvert.DeserializeObject<SavedLoginCredentials>(json);
                if (model == null || !model.RememberMe || string.IsNullOrEmpty(model.Cccd))
                {
                    return (false, string.Empty, string.Empty);
                }

                string password = DecryptString(model.EncryptedPassword);
                return (true, model.Cccd, password);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Lỗi nạp thông tin đăng nhập: {ex.Message}");
                return (false, string.Empty, string.Empty);
            }
        }

        public static void ClearCredentials()
        {
            try
            {
                string path = GetStoragePath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Lỗi xóa thông tin đăng nhập: {ex.Message}");
            }
        }

        private static string EncryptString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;
            byte[] key = SHA256.HashData(Entropy);
            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();
            using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length);
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs, Encoding.UTF8))
            {
                sw.Write(plainText);
            }
            return Convert.ToBase64String(ms.ToArray());
        }

        private static string DecryptString(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;
            byte[] fullBytes = Convert.FromBase64String(cipherText);
            byte[] key = SHA256.HashData(Entropy);
            using var aes = Aes.Create();
            aes.Key = key;
            byte[] iv = new byte[aes.BlockSize / 8];
            Array.Copy(fullBytes, 0, iv, 0, iv.Length);
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream(fullBytes, iv.Length, fullBytes.Length - iv.Length);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cs, Encoding.UTF8);
            return sr.ReadToEnd();
        }
    }
}
