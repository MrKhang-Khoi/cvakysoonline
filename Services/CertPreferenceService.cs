using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace VGCASignToolPro.Services
{
    /// <summary>
    /// Ghi nhớ lựa chọn chứng thư số của người dùng:
    ///   Mode = "VGCA_CLOUD" | "USB_PERSONAL" | "USB_SEAL"
    ///   Thumbprint = thumbprint của cert đã chọn (để khớp lại sau khi quét)
    /// </summary>
    public class CertPreferenceModel
    {
        public string Thumbprint { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty; // "VGCA_CLOUD" | "USB_PERSONAL" | "USB_SEAL"
        public string DisplayName { get; set; } = string.Empty;
        public DateTime SavedAt { get; set; } = DateTime.UtcNow;
    }

    public static class CertPreferenceService
    {
        private static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VGCASignToolPro", "cert_preference.json");

        public static void Save(CertPreferenceModel model)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                File.WriteAllText(StorePath, JsonConvert.SerializeObject(model, Formatting.Indented), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Lỗi lưu cert preference: {ex.Message}");
            }
        }

        public static CertPreferenceModel? Load()
        {
            try
            {
                if (!File.Exists(StorePath)) return null;
                string json = File.ReadAllText(StorePath, Encoding.UTF8);
                return JsonConvert.DeserializeObject<CertPreferenceModel>(json);
            }
            catch
            {
                return null;
            }
        }

        public static void Clear()
        {
            try { if (File.Exists(StorePath)) File.Delete(StorePath); }
            catch { }
        }

        /// <summary>
        /// Xác định Mode từ CertificateModel
        /// </summary>
        public static string DetectMode(Models.CertificateModel cert)
        {
            if (cert.IsOrganizationCertificate) return "USB_SEAL";
            if (cert.IsHardwareToken) return "USB_PERSONAL";
            return "VGCA_CLOUD";
        }
    }
}
