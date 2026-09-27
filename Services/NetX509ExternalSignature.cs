using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using iText.Signatures;

namespace VGCASignToolPro.Services
{
    /// <summary>
    /// Cầu nối mật mã liên kết trực tiếp giữa khóa riêng tư (Private Key trong USB Token / KSP / Windows Store)
    /// với engine ký số iText PAdES chuẩn quốc tế.
    /// </summary>
    public class NetX509ExternalSignature : IExternalSignature
    {
        private readonly X509Certificate2 _certificate;
        private readonly string _digestAlgorithm;

        public NetX509ExternalSignature(X509Certificate2 certificate, string digestAlgorithm = "SHA256")
        {
            _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
            _digestAlgorithm = digestAlgorithm;
        }

        public string GetDigestAlgorithmName() => _digestAlgorithm;

        public string GetSignatureAlgorithmName()
        {
            using var rsa = _certificate.GetRSAPrivateKey();
            if (rsa != null) return "RSA";

            using var ecdsa = _certificate.GetECDsaPrivateKey();
            if (ecdsa != null) return "ECDSA";

            return "RSA";
        }

        public ISignatureMechanismParams? GetSignatureMechanismParameters() => null;

        public byte[] Sign(byte[] message)
        {
            // 1. Thử lấy RSA Private Key (phần lớn USB Token & Chứng thư chuyên dùng Chính phủ dùng RSA)
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

                // Windows CryptoAPI / CNG sẽ tự động gửi mã băm vào USB Token (yêu cầu mã PIN nếu cần)
                // và trả về chữ ký mật mã an toàn tuyệt đối.
                return rsa.SignData(message, hashAlg, RSASignaturePadding.Pkcs1);
            }

            // 2. Thử lấy ECDSA Private Key
            var ecdsa = _certificate.GetECDsaPrivateKey();
            if (ecdsa != null)
            {
                var hashAlg = _digestAlgorithm.ToUpperInvariant() switch
                {
                    "SHA256" => HashAlgorithmName.SHA256,
                    "SHA384" => HashAlgorithmName.SHA384,
                    "SHA512" => HashAlgorithmName.SHA512,
                    _ => HashAlgorithmName.SHA256
                };
                return ecdsa.SignData(message, hashAlg);
            }

            throw new InvalidOperationException("Không thể truy cập khóa riêng tư (Private Key) của chứng thư này. Vui lòng kiểm tra lại thiết bị USB Token hoặc chứng thư số.");
        }
    }
}
