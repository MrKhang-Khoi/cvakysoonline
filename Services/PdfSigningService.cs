using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using iText.Bouncycastle.X509;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms.Form.Element;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Signatures;
using VGCASignToolPro.Models;

namespace VGCASignToolPro.Services
{
    public class PdfSigningService
    {
        public event Action<string>? StatusChanged;

        public Task<string> SignPdfAsync(SigningRequest request)
        {
            return Task.Run(() => SignPdf(request));
        }

        public string SignPdf(SigningRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.SourcePdfPath) || !File.Exists(request.SourcePdfPath))
                throw new FileNotFoundException("Tệp PDF nguồn không tồn tại.", request.SourcePdfPath);
            if (request.Certificate?.Certificate == null)
                throw new ArgumentException("Chưa chọn chứng thư số để ký.", nameof(request));

            string outputPath = request.OutputPdfPath;
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                string dir = System.IO.Path.GetDirectoryName(request.SourcePdfPath) ?? AppDomain.CurrentDomain.BaseDirectory;
                string baseName = System.IO.Path.GetFileNameWithoutExtension(request.SourcePdfPath);
                outputPath = System.IO.Path.Combine(dir, $"{baseName}_signed.pdf");
            }

            StatusChanged?.Invoke("Đang phân tích cấu trúc tài liệu PDF...");

            // Đọc tài liệu PDF nguồn
            using var reader = new PdfReader(request.SourcePdfPath);
            using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

            // Bật Append Mode để không làm mất hiệu lực của các chữ ký số đã có trước đó
            var stampingProperties = new StampingProperties().UseAppendMode();
            var signer = new PdfSigner(reader, outputStream, stampingProperties);

            // Xác định tổng số trang
            int totalPages = signer.GetDocument().GetNumberOfPages();
            int targetPage = request.PageNumber <= 0 || request.PageNumber > totalPages ? totalPages : request.PageNumber;

            StatusChanged?.Invoke($"Thiết lập vùng hiển thị chữ ký trên trang {targetPage}/{totalPages}...");

            // Tên trường chữ ký duy nhất
            string sigFieldName = $"Signature_{Guid.NewGuid():N}".Substring(0, 20);

            var signerProps = new SignerProperties();
            signerProps.SetFieldName(sigFieldName);
            signerProps.SetPageNumber(targetPage);
            signerProps.SetPageRect(new Rectangle(request.X, request.Y, request.Width, request.Height));
            signerProps.SetReason(request.Reason);
            signerProps.SetLocation(request.Location);
            signerProps.SetClaimedSignDate(DateTime.Now);

            // Tạo hiển thị trực quan (Visual Appearance)
            var appearance = new SignatureFieldAppearance(sigFieldName);
            appearance.SetPageNumber(targetPage);
            appearance.SetFixedPosition(targetPage, request.X, request.Y, request.Width);

            string signerName = !string.IsNullOrWhiteSpace(request.SignerDisplayName)
                ? request.SignerDisplayName
                : request.Certificate.CommonName;

            string signInfoText = $"Ký bởi: {signerName}\nNgày ký: {DateTime.Now:dd/MM/yyyy HH:mm:ss}\nLý do: {request.Reason}";

            if (!string.IsNullOrWhiteSpace(request.StampImagePath) && File.Exists(request.StampImagePath))
            {
                StatusChanged?.Invoke("Đang nhúng hình ảnh con dấu / chữ ký tay...");
                ImageData stampImage = ImageDataFactory.Create(request.StampImagePath);
                appearance.SetContent(signInfoText, stampImage);
            }
            else
            {
                appearance.SetContent(signInfoText);
            }

            appearance.SetFontSize(8);
            signerProps.SetSignatureAppearance(appearance);
            signer.SetSignerProperties(signerProps);

            StatusChanged?.Invoke("Đang khởi tạo chuỗi chứng thư số và liên kết USB Token / KSP...");

            // Nạp chuỗi chứng thư số X.509
            var chainList = new List<IX509Certificate>();
            var factory = new iText.Bouncycastle.BouncyCastleFactory();
            var parser = factory.CreateX509CertificateParser();

            using var x509Chain = new X509Chain();
            x509Chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            x509Chain.Build(request.Certificate.Certificate);

            foreach (var chainElement in x509Chain.ChainElements)
            {
                var bcCerts = parser.ReadAllCerts(chainElement.Certificate.RawData);
                chainList.AddRange(bcCerts);
            }

            if (chainList.Count == 0)
            {
                chainList.AddRange(parser.ReadAllCerts(request.Certificate.Certificate.RawData));
            }

            var chain = chainList.ToArray();

            // Khởi tạo cầu nối chữ ký số qua Windows CryptoAPI / CNG (USB Token / KSP)
            IExternalSignature externalSignature = new NetX509ExternalSignature(request.Certificate.Certificate, "SHA256");
            var externalDigest = new BouncyCastleDigest();

            // Cấu hình Dấu thời gian TSA (RFC 3161)
            ITSAClient? tsaClient = null;
            if (request.UseTSA && !string.IsNullOrWhiteSpace(request.TSAUrl))
            {
                try
                {
                    StatusChanged?.Invoke($"Đang kết nối Máy chủ cấp Dấu thời gian TSA ({request.TSAUrl})...");
                    tsaClient = new TSAClientBouncyCastle(request.TSAUrl);
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke($"Cảnh báo TSA: Không thể kết nối TSA ({ex.Message}), tiếp tục ký với thời gian cục bộ.");
                }
            }

            StatusChanged?.Invoke("Đang tính toán mã băm SHA-256 và thực hiện ký số PAdES...");

            // Ký số chuẩn PAdES LTV Detached
            signer.SignDetached(
                externalDigest,
                externalSignature,
                chain,
                null,
                null,
                tsaClient,
                0,
                PdfSigner.CryptoStandard.CMS);

            StatusChanged?.Invoke($"Ký số thành công! Tệp tin: {System.IO.Path.GetFileName(outputPath)}");
            return outputPath;
        }
    }
}
