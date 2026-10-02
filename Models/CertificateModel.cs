using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace VGCASignToolPro.Models
{
    public class CertificateModel
    {
        public X509Certificate2 Certificate { get; }
        public string Subject { get; }
        public string CommonName { get; }
        public string Email { get; }
        public string OrganizationUnit { get; }
        public string Organization { get; }
        public string Issuer { get; }
        public string IssuerCommonName { get; }
        public string SerialNumber { get; }
        public DateTime NotBefore { get; }
        public DateTime NotAfter { get; }
        public string Thumbprint { get; }
        public bool HasPrivateKey { get; }
        public bool IsValidNow { get; }
        public bool IsVGCACertificate { get; }
        public string ProviderName { get; private set; } = string.Empty;
        public string KeyContainerName { get; private set; } = string.Empty;

        public bool IsOrganizationCertificate
        {
            get
            {
                string cnUpper = (CommonName ?? string.Empty).ToUpperInvariant();
                string subjectUpper = (Subject ?? string.Empty).ToUpperInvariant();

                // Chuẩn chứng thư con dấu pháp nhân cơ quan theo quy chuẩn Ban Cơ yếu:
                // Bắt buộc có MST (Mã số thuế), MNS (Mã ngân sách) hoặc OID cơ quan, HOẶC tên cơ quan ở CN
                bool hasOrgIdentifier = subjectUpper.Contains("MST:") ||
                                        subjectUpper.Contains("MNS:") ||
                                        subjectUpper.Contains("OID.0.9.2342.19200300.100.1.1");
                bool hasOrgCn = cnUpper.StartsWith("TRƯỜNG ") ||
                                cnUpper.StartsWith("TRUONG ") ||
                                cnUpper.StartsWith("ỦY BAN ") ||
                                cnUpper.StartsWith("UY BAN ") ||
                                cnUpper.StartsWith("PHÒNG ") ||
                                cnUpper.StartsWith("PHONG ") ||
                                cnUpper.StartsWith("SỞ ") ||
                                cnUpper.StartsWith("SO ");
                return hasOrgIdentifier || hasOrgCn;
            }
        }

        public bool IsVirtualCspCertificate
        {
            get
            {
                if (!IsVGCACertificate) return false;
                string provUpper = (ProviderName ?? string.Empty).ToUpperInvariant();
                if (provUpper.Contains("RSSP") || provUpper.Contains("VIRTUAL"))
                    return true;
                // Virtual CSP v2.0 của Ban Cơ Yếu Chính Phủ sử dụng ECDSA P-384
                if (KeyAlgorithm.StartsWith("ECDSA", StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }
        }

        public bool IsHardwareToken
        {
            get
            {
                if (!HasPrivateKey) return false;
                string provUpper = (ProviderName ?? string.Empty).ToUpperInvariant();
                string contUpper = (KeyContainerName ?? string.Empty).ToUpperInvariant();

                // Nhận diện USB Token phần cứng qua Smart Card KSP / bit4id CSP
                if (provUpper.Contains("SMART CARD") ||
                    provUpper.Contains("BIT4ID") ||
                    contUpper.Contains("BIT4ID"))
                {
                    return true;
                }

                // Nếu có private key, không phải Virtual CSP và dùng RSA (đặc thù Token vật lý VGCA)
                if (!IsVirtualCspCertificate &&
                    !provUpper.Contains("RSSP") &&
                    KeyAlgorithm.StartsWith("RSA", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;
            }
        }

        public string KeyAlgorithm { get; }
        public string DisplayText { get; }

        public CertificateModel(X509Certificate2 cert)
        {
            Certificate = cert ?? throw new ArgumentNullException(nameof(cert));
            Subject = cert.Subject;
            Issuer = cert.Issuer;
            SerialNumber = cert.SerialNumber;
            NotBefore = cert.NotBefore;
            NotAfter = cert.NotAfter;
            Thumbprint = cert.Thumbprint;
            HasPrivateKey = cert.HasPrivateKey;
            IsValidNow = DateTime.Now >= NotBefore && DateTime.Now <= NotAfter;

            // Phân tích các trường trong Subject
            var parts = ParseDistinguishedName(cert.Subject);
            CommonName = parts.GetValueOrDefault("CN", cert.GetNameInfo(X509NameType.SimpleName, false) ?? "N/A");
            Email = parts.GetValueOrDefault("E", parts.GetValueOrDefault("EMAILADDRESS", ""));
            OrganizationUnit = parts.GetValueOrDefault("OU", "");
            Organization = parts.GetValueOrDefault("O", "");

            var issuerParts = ParseDistinguishedName(cert.Issuer);
            IssuerCommonName = issuerParts.GetValueOrDefault("CN", cert.GetNameInfo(X509NameType.SimpleName, true) ?? cert.Issuer);

            // Kiểm tra xem có phải chứng thư số chuyên dùng của Ban Cơ yếu Chính phủ không
            string issuerUpper = cert.Issuer.ToUpperInvariant();
            IsVGCACertificate = issuerUpper.Contains("BAN CƠ YẾU") ||
                                issuerUpper.Contains("CHINH PHU") ||
                                issuerUpper.Contains("CHÍNH PHỦ") ||
                                issuerUpper.Contains("VGCA") ||
                                issuerUpper.Contains("CA PHỤC VỤ CÁC CƠ QUAN NHÀ NƯỚC");

            // Xác định loại khóa từ Public Key (Tuyệt đối không gọi PrivateKey để tránh đánh thức KSP driver / USB Token)
            string oidVal = cert.PublicKey.Oid.Value ?? "";
            string oidName = cert.PublicKey.Oid.FriendlyName ?? "";
            if (oidVal == "1.2.840.10045.2.1" || oidName.Contains("ECC", StringComparison.OrdinalIgnoreCase) || oidName.Contains("ECDSA", StringComparison.OrdinalIgnoreCase))
            {
                using var ecdsaPub = cert.GetECDsaPublicKey();
                int keySize = ecdsaPub?.KeySize ?? 384;
                KeyAlgorithm = $"ECDSA {keySize}-bit (ECC P-{keySize})";
            }
            else if (oidVal == "1.2.840.113549.1.1.1" || oidName.Contains("RSA", StringComparison.OrdinalIgnoreCase))
            {
                using var rsaPub = cert.GetRSAPublicKey();
                int keySize = rsaPub?.KeySize ?? 2048;
                KeyAlgorithm = $"RSA {keySize}-bit";
            }
            else
            {
                KeyAlgorithm = !string.IsNullOrEmpty(oidName) ? oidName : "Khóa công khai";
            }

            // Trích xuất metadata Win32 Provider & Container Name (Không đánh thức PIN dialog)
            ExtractKeyProvInfo(cert);

            string vgcaTag = IsVGCACertificate ? "[VGCA Ban Cơ yếu] " : "";
            string tokenTypeTag = IsHardwareToken ? " [USB Token]" : (IsVirtualCspCertificate ? " [VGCA Cloud]" : "");
            string status = IsValidNow ? "Còn hạn" : "Hết hạn";
            DisplayText = $"{vgcaTag}{CommonName}{tokenTypeTag} | {IssuerCommonName} | Hạn: {NotAfter:dd/MM/yyyy} ({status})";
        }

        public bool MatchesPerson(string targetFullName, string? targetCccd = null)
        {
            if (string.IsNullOrWhiteSpace(targetFullName)) return false;

            // Chứng thư con dấu cơ quan không được đối soát là cá nhân BGH
            if (IsOrganizationCertificate) return false;

            string normCn = RemoveDiacritics(CommonName).ToUpperInvariant();
            string normTarget = RemoveDiacritics(targetFullName).ToUpperInvariant();

            // 1. Kiểm tra đối soát Họ và Tên (chuẩn hóa tiếng Việt)
            bool nameMatches = string.Equals(normCn, normTarget, StringComparison.OrdinalIgnoreCase) ||
                               normCn.Contains(normTarget, StringComparison.OrdinalIgnoreCase) ||
                               normTarget.Contains(normCn, StringComparison.OrdinalIgnoreCase);

            if (!nameMatches) return false;

            // 2. Nếu có số CCCD mục tiêu (12 số hoặc 9 số)
            if (!string.IsNullOrWhiteSpace(targetCccd) && targetCccd.Length >= 9)
            {
                string subjectUpper = (Subject ?? string.Empty).ToUpperInvariant();
                if (subjectUpper.Contains(targetCccd))
                {
                    return true; // Khớp cả Tên và CCCD
                }

                // Nếu Subject có chứa một chuỗi số CCCD khác với targetCccd thì từ chối để chống ký lẫn lộn BGH
                var match = Regex.Match(subjectUpper, @"\b\d{9,12}\b");
                if (match.Success && !match.Value.Equals(targetCccd, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return nameMatches;
        }

        private static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder();
            foreach (char c in normalizedString)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }
            return stringBuilder.ToString().Normalize(NormalizationForm.FormC).Replace('đ', 'd').Replace('Đ', 'D').Trim();
        }

        private static Dictionary<string, string> ParseDistinguishedName(string dn)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(dn)) return dict;

            var parts = dn.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
            var ouList = new List<string>();

            foreach (var part in parts)
            {
                int eqIndex = part.IndexOf('=');
                if (eqIndex > 0)
                {
                    string key = part.Substring(0, eqIndex).Trim();
                    string val = part.Substring(eqIndex + 1).Trim();

                    if (key.Equals("OU", StringComparison.OrdinalIgnoreCase))
                    {
                        ouList.Add(val);
                    }
                    else if (!dict.ContainsKey(key))
                    {
                        dict[key] = val;
                    }
                }
            }

            if (ouList.Count > 0)
            {
                dict["OU"] = string.Join(" - ", ouList);
            }

            return dict;
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CertGetCertificateContextProperty(
            IntPtr pCertContext,
            uint dwPropId,
            IntPtr pvData,
            ref uint pcbData);

        private const uint CERT_KEY_PROV_INFO_PROP_ID = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CRYPT_KEY_PROV_INFO
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pwszContainerName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pwszProvName;
            public uint dwProvType;
            public uint dwFlags;
            public uint cProvParam;
            public IntPtr rgProvParam;
            public uint dwKeySpec;
        }

        private void ExtractKeyProvInfo(X509Certificate2 cert)
        {
            IntPtr pData = IntPtr.Zero;
            try
            {
                uint cbData = 0;
                if (CertGetCertificateContextProperty(cert.Handle, CERT_KEY_PROV_INFO_PROP_ID, IntPtr.Zero, ref cbData) && cbData > 0)
                {
                    pData = Marshal.AllocHGlobal((int)cbData);
                    if (CertGetCertificateContextProperty(cert.Handle, CERT_KEY_PROV_INFO_PROP_ID, pData, ref cbData))
                    {
                        var provInfo = Marshal.PtrToStructure<CRYPT_KEY_PROV_INFO>(pData);
                        KeyContainerName = provInfo.pwszContainerName ?? string.Empty;
                        ProviderName = provInfo.pwszProvName ?? string.Empty;
                    }
                }
            }
            catch
            {
                // Fallback an toàn
            }
            finally
            {
                if (pData != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pData);
                }
            }
        }

        public override string ToString() => DisplayText;
    }
}
