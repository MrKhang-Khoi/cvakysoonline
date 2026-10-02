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
        /// Lấy chứng thư số chuyên dùng Ban Cơ yếu Chính phủ đang hoạt động của cá nhân BGH/Giáo viên
        /// Hỗ trợ đối soát chính xác theo Họ tên, CCCD và phương thức ký (AUTO / USB_TOKEN / VGCA_CLOUD)
        /// </summary>
        public CertificateModel? GetActiveVGCACertificate(string? fullName = null, string? cccd = null, string? signingMethod = "AUTO")
        {
            var certs = LoadCertificates(onlyWithPrivateKey: true);

            // Lọc danh sách: bắt buộc là chứng thư cá nhân VGCA có khóa riêng và còn hạn (loại bỏ hoàn toàn con dấu cơ quan)
            var personalCerts = certs
                .Where(c => c.IsVGCACertificate && !c.IsOrganizationCertificate && c.HasPrivateKey && c.IsValidNow)
                .ToList();

            string method = (signingMethod ?? "AUTO").ToUpperInvariant();

            // 1. Nếu có thông tin người dùng cụ thể (BGH / Giáo viên đăng nhập): BẮT BUỘC ĐỐI SOÁT CHÍNH XÁC DANH TÍNH
            if (!string.IsNullOrWhiteSpace(fullName) || !string.IsNullOrWhiteSpace(cccd))
            {
                var matchedCerts = personalCerts.Where(c => c.MatchesPerson(fullName ?? string.Empty, cccd)).ToList();

                // Nếu không tìm thấy chứng thư nào của người này -> Trả về null ngay (tuyệt đối không lấy nhầm người khác!)
                if (matchedCerts.Count == 0)
                {
                    return null;
                }

                if (method == "USB_TOKEN")
                {
                    // Yêu cầu USB Token: Chỉ trả về nếu là Token phần cứng của người này
                    return matchedCerts.FirstOrDefault(c => c.IsHardwareToken);
                }
                else if (method == "VGCA_CLOUD")
                {
                    // Yêu cầu Ký số từ xa: Chỉ trả về nếu là Virtual CSP của người này
                    return matchedCerts.FirstOrDefault(c => c.IsVirtualCspCertificate);
                }
                else // AUTO: Ưu tiên USB Token phần cứng nếu cắm, ngược lại dùng Cloud của người này
                {
                    return matchedCerts.FirstOrDefault(c => c.IsHardwareToken)
                        ?? matchedCerts.FirstOrDefault(c => c.IsVirtualCspCertificate)
                        ?? matchedCerts[0];
                }
            }

            // 2. Không chỉ định người dùng hoặc không khớp người cụ thể: Lọc theo phương thức ký
            if (method == "USB_TOKEN")
            {
                var tokenCert = personalCerts.FirstOrDefault(c => c.IsHardwareToken);
                if (tokenCert != null) return tokenCert;
            }
            else if (method == "VGCA_CLOUD")
            {
                var cloudCert = personalCerts.FirstOrDefault(c => c.IsVirtualCspCertificate);
                if (cloudCert != null) return cloudCert;
            }

            // Fallback: Ưu tiên Token cắm máy -> Cloud -> chứng thư cá nhân bất kỳ
            return personalCerts.FirstOrDefault(c => c.IsHardwareToken)
                ?? personalCerts.FirstOrDefault(c => c.IsVirtualCspCertificate)
                ?? personalCerts.FirstOrDefault()
                ?? certs.FirstOrDefault(c => c.IsVGCACertificate && !c.IsOrganizationCertificate && c.HasPrivateKey);
        }

        /// <summary>
        /// Quét tìm chứng thư số tổ chức / nhà trường trong thiết bị USB Token Ban Cơ yếu (Dùng để ĐÓNG DẤU PHÁP NHÂN)
        /// Bắt buộc chứng thư phải là Con Dấu Cơ Quan (có MST/MNS hoặc Tên trường) và có Khóa riêng
        /// </summary>
        public CertificateModel? GetOrganizationTokenCertificate()
        {
            var certs = LoadCertificates(onlyWithPrivateKey: true);

            // 1. Ưu tiên chứng thư VGCA con dấu tổ chức có khóa riêng trên USB Token phần cứng và còn hạn
            var orgCert = certs.FirstOrDefault(c => c.IsVGCACertificate && c.IsValidNow && c.IsOrganizationCertificate && c.IsHardwareToken);
            if (orgCert != null) return orgCert;

            // 2. Chứng thư VGCA tổ chức có khóa riêng còn hạn
            orgCert = certs.FirstOrDefault(c => c.IsVGCACertificate && c.IsValidNow && c.IsOrganizationCertificate);
            if (orgCert != null) return orgCert;

            // 3. Chứng thư VGCA tổ chức có khóa riêng
            orgCert = certs.FirstOrDefault(c => c.IsVGCACertificate && c.IsOrganizationCertificate && c.HasPrivateKey);
            if (orgCert != null) return orgCert;

            // 4. Fallback: chứng thư tổ chức bất kỳ có khóa riêng
            return certs.FirstOrDefault(c => c.IsOrganizationCertificate && c.HasPrivateKey);
        }

        /// <summary>
        /// Kiểm tra xem thiết bị USB Token phần cứng có đang cắm vào máy hay không
        /// - checkPersonal == true: Kiểm tra USB Token cá nhân (có thể đối soát theo fullName / cccd của BGH)
        /// - checkPersonal == false: Kiểm tra USB Token con dấu pháp nhân nhà trường
        /// </summary>
        public bool IsUsbTokenPluggedIn(bool checkPersonal = false, string? fullName = null, string? cccd = null)
        {
            if (checkPersonal)
            {
                var certs = LoadCertificates(onlyWithPrivateKey: true);
                if (!string.IsNullOrWhiteSpace(fullName) || !string.IsNullOrWhiteSpace(cccd))
                {
                    return certs.Any(c => c.IsVGCACertificate && !c.IsOrganizationCertificate && c.IsHardwareToken && c.MatchesPerson(fullName ?? string.Empty, cccd));
                }
                return certs.Any(c => c.IsVGCACertificate && !c.IsOrganizationCertificate && c.IsHardwareToken);
            }

            var orgCert = GetOrganizationTokenCertificate();
            return orgCert != null && orgCert.HasPrivateKey;
        }

        /// <summary>
        /// Kiểm tra xem người dùng đã sẵn sàng ký số Ban Cơ Yếu hay chưa
        /// - Nếu dùng USB Token phần cứng: chỉ cần thiết bị cắm sẵn và có khóa riêng (PIN sẽ hỏi lúc ký)
        /// - Nếu dùng VGCA Cloud (Virtual CSP): bắt buộc ứng dụng Virtual CSP v2.0 đang chạy và đã đăng nhập
        /// </summary>
        public bool IsVGCALoggedIn(CertificateModel? specificCert = null)
        {
            var cert = specificCert ?? GetActiveVGCACertificate();
            if (cert == null || !cert.HasPrivateKey) return false;

            // Nếu là thiết bị phần cứng (USB Token) -> Sẵn sàng ký trực tiếp, PIN nhập trên giao diện CSP của Token
            if (cert.IsHardwareToken)
            {
                return true;
            }

            // Nếu là ký số từ xa Virtual CSP v2.0 -> Kiểm tra trạng thái KSP của dịch vụ đám mây
            if (cert.IsVirtualCspCertificate)
            {
                var vcspStatus = VcspStatusService.CheckStatus();
                return vcspStatus.IsReadyToSign;
            }

            return true;
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
