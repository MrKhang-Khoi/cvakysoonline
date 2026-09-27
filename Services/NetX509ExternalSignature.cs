using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using iText.Signatures;

namespace VGCASignToolPro.Services
{
    /// <summary>
    /// Cầu nối mật mã liên kết trực tiếp giữa khóa riêng tư (Private Key trong USB Token / KSP / Windows Store)
    /// với engine ký số iText PAdES chuẩn quốc tế.
    /// Hỗ trợ cả RSA (USB Token truyền thống) và ECDSA P-384 (Chứng thư thế hệ mới G2 của Ban Cơ yếu Chính phủ).
    /// </summary>
    public class NetX509ExternalSignature : IExternalSignature
    {
        private readonly X509Certificate2 _certificate;
        private readonly string _digestAlgorithm;

        public NetX509ExternalSignature(X509Certificate2 certificate, string? digestAlgorithm = null)
        {
            _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));

            // Tự động nhận diện thuật toán băm phù hợp với kích thước khóa
            if (string.IsNullOrWhiteSpace(digestAlgorithm))
            {
                using var ecdsa = _certificate.GetECDsaPrivateKey();
                if (ecdsa != null && ecdsa.KeySize == 384)
                {
                    _digestAlgorithm = "SHA384";
                }
                else
                {
                    _digestAlgorithm = "SHA256";
                }
            }
            else
            {
                _digestAlgorithm = digestAlgorithm;
            }
        }

        public string GetDigestAlgorithmName() => _digestAlgorithm;

        public string GetSignatureAlgorithmName()
        {
            using var ecdsa = _certificate.GetECDsaPrivateKey();
            if (ecdsa != null) return "ECDSA";

            using var rsa = _certificate.GetRSAPrivateKey();
            if (rsa != null) return "RSA";

            return "RSA";
        }

        public ISignatureMechanismParams? GetSignatureMechanismParameters() => null;

        public byte[] Sign(byte[] message)
        {
            // 1. Xử lý chứng thư ECDSA (Chuẩn mới của Ban Cơ yếu Chính phủ Curve P-384)
            var ecdsa = _certificate.GetECDsaPrivateKey();
            if (ecdsa != null)
            {
                var hashAlg = _digestAlgorithm.ToUpperInvariant() switch
                {
                    "SHA256" => HashAlgorithmName.SHA256,
                    "SHA384" => HashAlgorithmName.SHA384,
                    "SHA512" => HashAlgorithmName.SHA512,
                    _ => HashAlgorithmName.SHA384
                };

                // Xuất chữ ký theo định dạng RFC 3279 DER Sequence (cấu trúc ASN.1 chuẩn cho CMS / PAdES)
                return ecdsa.SignData(message, hashAlg, DSASignatureFormat.Rfc3279DerSequence);
            }

            // 2. Xử lý chứng thư RSA (USB Token phần cứng / PKCS#11)
            var rsa = _certificate.GetRSAPrivateKey();
            if (rsa != null)
            {
                var hashAlg = _digestAlgorithm.ToUpperInvariant() switch
                {
                    "SHA1" => HashAlgorithmName.SHA1,
                    "SHA256" => HashAlgorithmName.SHA256,
                    "SHA384" => HashAlgorithmName.SHA384,
                    "SHA512" => HashAlgorithmName.SHA512,
                    _ => HashAlgorithmName.SHA256
                };

                return rsa.SignData(message, hashAlg, RSASignaturePadding.Pkcs1);
            }

            throw new InvalidOperationException("Không thể truy cập khóa riêng tư (Private Key) của chứng thư này. Vui lòng kiểm tra lại thiết bị USB Token hoặc chứng thư số.");
        }
    }
}
