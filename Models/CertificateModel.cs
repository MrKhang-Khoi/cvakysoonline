using System;
using System.Security.Cryptography.X509Certificates;

namespace VGCASignToolPro.Models
{
    public class CertificateModel
    {
        public X509Certificate2 Certificate { get; }
        public string Subject { get; }
        public string CommonName { get; }
        public string Issuer { get; }
        public string SerialNumber { get; }
        public DateTime NotBefore { get; }
        public DateTime NotAfter { get; }
        public string Thumbprint { get; }
        public bool HasPrivateKey { get; }
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

            CommonName = ExtractCommonName(cert.Subject);
            var issuerCn = ExtractCommonName(cert.Issuer);

            string status = (DateTime.Now >= NotBefore && DateTime.Now <= NotAfter) ? "Còn hạn" : "Hết hạn";
            DisplayText = $"{CommonName} | Cấp bởi: {issuerCn} | Hạn: {NotAfter:dd/MM/yyyy} ({status})";
        }

        private static string ExtractCommonName(string dn)
        {
            if (string.IsNullOrWhiteSpace(dn)) return "N/A";
            foreach (var part in dn.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring(3).Trim();
                }
            }
            return dn;
        }

        public override string ToString() => DisplayText;
    }
}
