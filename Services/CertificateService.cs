using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using VGCASignToolPro.Models;

namespace VGCASignToolPro.Services
{
    public class CertificateService
    {
        private static readonly string VGCAManagerExePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "VGCA", "Virtual CSP v2.0", "vgca_vcsp_v2_mgr.exe");

        public List<CertificateModel> LoadCertificates(bool onlyWithPrivateKey = true)
        {
            var results = new List<CertificateModel>();
            var seenThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Quét kho CurrentUser\My (Kho người dùng đăng nhập hiện tại - chứa chứng thư VGCA, Virtual CSP, USB Token)
            ScanStore(StoreName.My, StoreLocation.CurrentUser, onlyWithPrivateKey, results, seenThumbprints);

            // 2. Quét kho LocalMachine\My
            ScanStore(StoreName.My, StoreLocation.LocalMachine, onlyWithPrivateKey, results, seenThumbprints);

            // Lọc bỏ các chứng thư thiết bị hệ thống không dùng để ký số văn bản (như MS-Organization-Access, localhost)
            var filtered = results.Where(c =>
            {
                if (c.CommonName.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (c.Issuer.Contains("MS-Organization-Access", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (c.CommonName.StartsWith("WMSvc-", StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            }).ToList();

            // Ưu tiên chứng thư Ban Cơ yếu Chính phủ lên đầu danh sách
            return filtered
                .OrderByDescending(c => c.IsVGCACertificate)
                .ThenByDescending(c => c.IsValidNow)
                .ThenBy(c => c.CommonName)
                .ToList();
        }

        private static void ScanStore(StoreName storeName, StoreLocation storeLocation, bool onlyWithPrivateKey, List<CertificateModel> results, HashSet<string> seenThumbprints)
        {
            try
            {
                using var store = new X509Store(storeName, storeLocation);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

                foreach (var cert in store.Certificates)
                {
                    try
                    {
                        if (seenThumbprints.Contains(cert.Thumbprint))
                            continue;

                        if (onlyWithPrivateKey && !cert.HasPrivateKey)
                            continue;

                        seenThumbprints.Add(cert.Thumbprint);
                        results.Add(new CertificateModel(cert));
                    }
                    catch
                    {
                        // Bỏ qua chứng thư bị lỗi định dạng
                    }
                }
            }
            catch
            {
                // Bỏ qua nếu không có quyền truy cập kho
            }
        }

        /// <summary>
        /// Lấy chứng thư số chuyên dùng Ban Cơ yếu Chính phủ đang hoạt động của người dùng
        /// </summary>
        public CertificateModel? GetActiveVGCACertificate(string? preferredNameOrEmail = null)
        {
            var certs = LoadCertificates(onlyWithPrivateKey: true);

            if (!string.IsNullOrWhiteSpace(preferredNameOrEmail))
            {
                var matched = certs.FirstOrDefault(c => c.IsVGCACertificate && (
                    string.Equals(c.CommonName, preferredNameOrEmail, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Email, preferredNameOrEmail, StringComparison.OrdinalIgnoreCase) ||
                    c.Subject.Contains(preferredNameOrEmail, StringComparison.OrdinalIgnoreCase)));
                if (matched != null) return matched;
            }

            // Ưu tiên chứng thư cá nhân giáo viên (không phải con dấu pháp nhân cơ quan)
            var personalCert = certs.FirstOrDefault(c => c.IsVGCACertificate && c.IsValidNow && !c.IsOrganizationCertificate);
            if (personalCert != null) return personalCert;

            return certs.FirstOrDefault(c => c.IsVGCACertificate && c.IsValidNow)
                ?? certs.FirstOrDefault(c => c.IsVGCACertificate);
        }

        /// <summary>
        /// Quét tìm chứng thư số tổ chức / nhà trường trong thiết bị USB Token Ban Cơ yếu
        /// </summary>
        public CertificateModel? GetOrganizationTokenCertificate()
        {
            var certs = LoadCertificates(onlyWithPrivateKey: true);
            // 1. Ưu tiên chứng thư VGCA có khóa riêng, còn hạn và là chứng thư tổ chức
            var orgCert = certs.FirstOrDefault(c => c.IsVGCACertificate && c.IsValidNow && c.IsOrganizationCertificate);
            if (orgCert != null) return orgCert;

            // 2. Chứng thư VGCA tổ chức
            orgCert = certs.FirstOrDefault(c => c.IsVGCACertificate && c.IsOrganizationCertificate);
            if (orgCert != null) return orgCert;

            // 3. Fallback: chứng thư tổ chức bất kỳ có khóa riêng
            return certs.FirstOrDefault(c => c.IsOrganizationCertificate && c.HasPrivateKey);
        }

        /// <summary>
        /// Kiểm tra xem thiết bị USB Token chứa chứng thư con dấu tổ chức có đang cắm vào máy hay không
        /// </summary>
        public bool IsUsbTokenPluggedIn()
        {
            var cert = GetOrganizationTokenCertificate();
            return cert != null && cert.HasPrivateKey;
        }

        /// <summary>
        /// Kiểm tra xem người dùng đã đăng nhập vào hệ thống VGCA / Virtual CSP hay chưa
        /// </summary>
        public bool IsVGCALoggedIn()
        {
            var activeCert = GetActiveVGCACertificate();
            return activeCert != null && activeCert.HasPrivateKey;
        }

        /// <summary>
        /// Khởi chạy ứng dụng quản lý Virtual CSP của Ban Cơ yếu để người dùng đăng nhập tài khoản
        /// </summary>
        public bool LaunchVGCALoginApp()
        {
            var candidatePaths = new List<string>
            {
                VGCAManagerExePath,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VGCA", "Virtual CSP v2.0", "vgca_vcsp_v2_mgr.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "VGCA Virtual CSP v2.0.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "VGCA Virtual CSP v2.0.lnk"),
                @"C:\Users\Public\Desktop\VGCA Virtual CSP v2.0.lnk"
            };

            foreach (string path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = path,
                            UseShellExecute = true
                        });
                        return true;
                    }
                    catch
                    {
                        // Tiếp tục thử đường dẫn thay thế
                    }
                }
            }
            return false;
        }
    }
}
