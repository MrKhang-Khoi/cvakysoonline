using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using VGCASignToolPro.Models;

namespace VGCASignToolPro.Services
{
    public class CertificateService
    {
        public List<CertificateModel> LoadCertificates(bool onlyWithPrivateKey = true)
        {
            var results = new List<CertificateModel>();
            var seenThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Quét kho CurrentUser\My (chứa chứng thư cá nhân, USB Token cắm vào, Virtual CSP)
            ScanStore(StoreName.My, StoreLocation.CurrentUser, onlyWithPrivateKey, results, seenThumbprints);

            // 2. Quét kho LocalMachine\My
            ScanStore(StoreName.My, StoreLocation.LocalMachine, onlyWithPrivateKey, results, seenThumbprints);

            // Sắp xếp chứng thư còn hạn lên đầu
            return results
                .OrderByDescending(c => DateTime.Now >= c.NotBefore && DateTime.Now <= c.NotAfter)
                .ThenBy(c => c.CommonName)
                .ToList();
        }

        private void ScanStore(StoreName storeName, StoreLocation storeLocation, bool onlyWithPrivateKey, List<CertificateModel> results, HashSet<string> seenThumbprints)
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
    }
}
