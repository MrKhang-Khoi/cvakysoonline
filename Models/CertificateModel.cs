using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;

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
        public bool IsOrganizationCertificate
        {
            get
            {
                string cnUpper = (CommonName ?? string.Empty).ToUpperInvariant();
                string subjectUpper = (Subject ?? string.Empty).ToUpperInvariant();
                return cnUpper.Contains("TRƯỜNG") ||
                       cnUpper.Contains("TRUONG") ||
                       cnUpper.Contains("ỦY BAN") ||
                       cnUpper.Contains("UY BAN") ||
                       cnUpper.Contains("PHÒNG") ||
                       cnUpper.Contains("PHONG") ||
                       cnUpper.Contains("SỞ") ||
                       cnUpper.Contains("SO ") ||
                       cnUpper.Contains("BAN ") ||
                       subjectUpper.Contains("MST:") ||
                       subjectUpper.Contains("MNS:") ||
                       subjectUpper.Contains("OID.0.9.2342.19200300.100.1.1");
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

            // Xác định loại khóa bí mật (RSA hay ECDSA)
            using var rsa = cert.GetRSAPrivateKey();
            if (rsa != null)
            {
                KeyAlgorithm = $"RSA {rsa.KeySize}-bit";
            }
            else
            {
                using var ecdsa = cert.GetECDsaPrivateKey();
                if (ecdsa != null)
                {
                    KeyAlgorithm = $"ECDSA {ecdsa.KeySize}-bit (ECC P-{ecdsa.KeySize})";
                }
                else
                {
                    KeyAlgorithm = cert.PublicKey.Oid.FriendlyName ?? "Khóa công khai";
                }
            }

            string vgcaTag = IsVGCACertificate ? "[VGCA Ban Cơ yếu] " : "";
            string status = IsValidNow ? "Còn hạn" : "Hết hạn";
            DisplayText = $"{vgcaTag}{CommonName} | {IssuerCommonName} | Hạn: {NotAfter:dd/MM/yyyy} ({status})";
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

        public override string ToString() => DisplayText;
    }
}
