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

            // Tự động nhận diện thuật toán băm phù hợp với kích thước khóa qua Public Key (KHÔNG chạm vào Private Key để tránh đánh thức/giải phóng nhầm KSP handle)
            if (string.IsNullOrWhiteSpace(digestAlgorithm))
            {
                using var ecdsaPub = _certificate.GetECDsaPublicKey();
                if (ecdsaPub != null && ecdsaPub.KeySize == 384)
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
            // Kiểm tra OID public key của chứng thư hoàn toàn trên RAM, không gọi KSP driver
            string oidVal = _certificate.PublicKey.Oid.Value ?? "";
            string oidName = _certificate.PublicKey.Oid.FriendlyName ?? "";

            if (oidVal == "1.2.840.10045.2.1" ||
                oidName.Contains("ECC", StringComparison.OrdinalIgnoreCase) ||
                oidName.Contains("ECDSA", StringComparison.OrdinalIgnoreCase))
            {
                return "ECDSA";
            }

            return "RSA";
        }

        public ISignatureMechanismParams? GetSignatureMechanismParameters() => null;

        public byte[] Sign(byte[] message)
        {
            string sigAlg = GetSignatureAlgorithmName();

            // 1. Xử lý chứng thư ECDSA (Chuẩn mới của Ban Cơ yếu Chính phủ Curve P-384)
            if (string.Equals(sigAlg, "ECDSA", StringComparison.OrdinalIgnoreCase))
            {
                using var ecdsa = _certificate.GetECDsaPrivateKey();
                if (ecdsa == null)
                {
                    throw new InvalidOperationException("Không thể truy cập khóa riêng tư ECDSA của chứng thư này. Vui lòng kiểm tra lại ứng dụng VGCA Virtual CSP.");
                }

                var hashAlg = _digestAlgorithm.ToUpperInvariant() switch
                {
                    "SHA256" => HashAlgorithmName.SHA256,
                    "SHA384" => HashAlgorithmName.SHA384,
                    "SHA512" => HashAlgorithmName.SHA512,
                    _ => HashAlgorithmName.SHA384
                };

                try
                {
                    // Xuất chữ ký theo định dạng RFC 3279 DER Sequence (cấu trúc ASN.1 chuẩn cho CMS / PAdES)
                    return ecdsa.SignData(message, hashAlg, DSASignatureFormat.Rfc3279DerSequence);
                }
                finally
                {
                    GC.KeepAlive(ecdsa);
                    GC.KeepAlive(_certificate);
                }
            }

            // 2. Xử lý chứng thư RSA (USB Token phần cứng / PKCS#11)
            using var rsa = _certificate.GetRSAPrivateKey();
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

                try
                {
                    return rsa.SignData(message, hashAlg, RSASignaturePadding.Pkcs1);
                }
                finally
                {
                    GC.KeepAlive(rsa);
                    GC.KeepAlive(_certificate);
                }
            }

            throw new InvalidOperationException("Không thể truy cập khóa riêng tư (Private Key) của chứng thư này. Vui lòng kiểm tra lại thiết bị USB Token hoặc chứng thư số.");
        }
    }
}
