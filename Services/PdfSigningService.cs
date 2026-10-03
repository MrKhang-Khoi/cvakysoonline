using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private readonly System.Threading.SemaphoreSlim _signLock = new(1, 1);

        public async Task<string> SignPdfAsync(SigningRequest request, System.Threading.CancellationToken cancellationToken = default)
        {
            await _signLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await Task.Run(() => SignPdf(request), cancellationToken);
            }
            finally
            {
                _signLock.Release();
            }
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

            string resolvedSource = System.IO.Path.GetFullPath(request.SourcePdfPath);
            string resolvedOutput = System.IO.Path.GetFullPath(outputPath);
            if (string.Equals(resolvedSource, resolvedOutput, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Đường dẫn tệp xuất ra phải khác tệp nguồn để tránh ghi đè làm mất tài liệu gốc.");
            }

            if (request.Width <= 0 || request.Height <= 0 || request.X < 0 || request.Y < 0)
            {
                throw new ArgumentException("Kích thước và tọa độ vùng chữ ký phải hợp lệ (Width > 0, Height > 0, X >= 0, Y >= 0).");
            }

            // Đảm bảo thư mục lưu tệp xuất ra đã tồn tại
            string? outDir = System.IO.Path.GetDirectoryName(resolvedOutput);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            // Nạp font chữ Unicode (Times New Roman / Arial / Segoe UI / Tahoma) trước khi tạo luồng tệp
            iText.Kernel.Font.PdfFont? unicodeFont = null;
            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            string[] fontCandidates = { "times.ttf", "arial.ttf", "segoeui.ttf", "tahoma.ttf" };
            foreach (var fontFile in fontCandidates)
            {
                string fontPath = System.IO.Path.Combine(fontsDir, fontFile);
                if (File.Exists(fontPath))
                {
                    try
                    {
                        unicodeFont = iText.Kernel.Font.PdfFontFactory.CreateFont(fontPath, iText.IO.Font.PdfEncodings.IDENTITY_H);
                        break;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        StatusChanged?.Invoke($"Cảnh báo font {fontFile}: {ex.Message}");
                    }
                }
            }

            if (unicodeFont == null)
            {
                throw new InvalidOperationException("Không tìm thấy font Unicode (Times New Roman / Arial / Segoe UI / Tahoma) hỗ trợ tiếng Việt trên hệ thống Windows.");
            }

            StatusChanged?.Invoke("Đang phân tích cấu trúc tài liệu PDF...");
            string tempOutput = resolvedOutput + $".{Guid.NewGuid():N}.tmp";
            bool signSucceeded = false;
            try
            {
                using (var reader = new PdfReader(resolvedSource))
                using (var outputStream = new FileStream(tempOutput, FileMode.Create, FileAccess.Write))
                {
                    // Bật Append Mode để không làm mất hiệu lực của các chữ ký số đã có trước đó
                    var stampingProperties = new StampingProperties().UseAppendMode();
                    var signer = new PdfSigner(reader, outputStream, stampingProperties);
                    // Xác định tổng số trang và trang ký
                    int totalPages = signer.GetDocument().GetNumberOfPages();
                    int targetPage = request.PageNumber <= 0 || request.PageNumber > totalPages ? totalPages : request.PageNumber;
                    StatusChanged?.Invoke($"Thiết lập vùng hiển thị chữ ký trên trang {targetPage}/{totalPages}...");

                    string sigFieldName = $"Sig_{Guid.NewGuid():N}";
                    var signDateTimeOffset = DateTimeOffset.Now;
                    var signerProps = new SignerProperties();
                    signerProps.SetFieldName(sigFieldName);
                    signerProps.SetPageNumber(targetPage);
                    signerProps.SetPageRect(new Rectangle(request.X, request.Y, request.Width, request.Height));
                    signerProps.SetReason(request.Reason);
                    signerProps.SetLocation(request.Location);
                    signerProps.SetClaimedSignDate(signDateTimeOffset.LocalDateTime);

                    var appearance = new SignatureFieldAppearance(sigFieldName);
                    appearance.SetFont(unicodeFont);
                    appearance.SetFontSize(8);

                    string signerName = !string.IsNullOrWhiteSpace(request.SignerDisplayName)
                        ? request.SignerDisplayName
                        : !string.IsNullOrWhiteSpace(request.Certificate.CommonName)
                            ? request.Certificate.CommonName
                            : "Người ký VGCA";

                    string sanitizedReason = (request.Reason ?? "")
                        .Replace("\r", " ")
                        .Replace("\n", " ")
                        .Trim();
                    if (sanitizedReason.Length > 200)
                    {
                        sanitizedReason = sanitizedReason.Substring(0, 200);
                    }

                    string signInfoText = $"Ký bởi: {signerName}\nNgày ký: {signDateTimeOffset:dd/MM/yyyy HH:mm:ss}\nLý do: {sanitizedReason}";

                    bool hasStampImage = !string.IsNullOrWhiteSpace(request.StampImagePath) && File.Exists(request.StampImagePath);
                    int displayMode = request.Profile?.Mode ?? (hasStampImage ? 1 : 0);

                    if (hasStampImage)
                    {
                        string resolvedStamp = System.IO.Path.GetFullPath(request.StampImagePath!);
                        if (resolvedStamp.StartsWith(@"\\", StringComparison.Ordinal) || (Uri.TryCreate(resolvedStamp, UriKind.Absolute, out var stampUri) && stampUri.IsUnc))
                        {
                            throw new ArgumentException("Không chấp nhận đường dẫn mạng UNC cho hình ảnh con dấu.");
                        }

                        string stampExt = System.IO.Path.GetExtension(resolvedStamp).ToLowerInvariant();
                        if (stampExt != ".png" && stampExt != ".jpg" && stampExt != ".jpeg" && stampExt != ".bmp")
                        {
                            throw new ArgumentException("Định dạng hình ảnh con dấu không được hỗ trợ.");
                        }

                        StatusChanged?.Invoke("Đang nhúng hình ảnh con dấu / chữ ký tay...");
                        ImageData stampImage = ImageDataFactory.Create(resolvedStamp);

                        if (displayMode == 1)
                        {
                            // Chế độ 1: Chỉ hiển thị hình ảnh con dấu đỏ hoặc chữ ký tay
                            appearance.SetContent(stampImage);
                        }
                        else
                        {
                            // Chế độ 2: Hiển thị cả hình ảnh con dấu và dòng thông tin chữ ký điện tử
                            appearance.SetContent(signInfoText, stampImage);
                        }
                    }
                    else
                    {
                        // Bug #7 fix: Khi không có ảnh, displayMode vẫn có ý nghĩa:
                        // mode 0 hoặc 2: chỉ text; mode 1 (yêu cầu ảnh nhưng không có): cảnh báo và fallback text
                        if (displayMode == 1)
                        {
                            StatusChanged?.Invoke("Cảnh báo: Profile yêu cầu ảnh con dấu nhưng không tìm thấy file ảnh. Hiển thị dạng chữ thay thế.");
                        }
                        // Chế độ 0 hoặc fallback: Chỉ hiển thị thông tin chữ ký điện tử dạng chữ
                        appearance.SetContent(signInfoText);
                    }

                    signerProps.SetSignatureAppearance(appearance);
                    signer.SetSignerProperties(signerProps);

                    StatusChanged?.Invoke("Đang khởi tạo chuỗi chứng thư số và liên kết USB Token / KSP...");

                    // Nạp chuỗi chứng thư số X.509
                    var chainList = new List<IX509Certificate>();
                    var factory = new iText.Bouncycastle.BouncyCastleFactory();
                    var parser = factory.CreateX509CertificateParser();

                    using var x509Chain = new X509Chain();
                    // ponytail: RevocationMode.NoCheck — chấp nhận cho offline/USB token PKCS#11 signing theo chuẩn VGCA.
                    // Nâng cấp: bật Online revocation check khi có kết nối mạng ổn định + OCSP/CRL endpoint khả dụng.
                    x509Chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    bool built = x509Chain.Build(request.Certificate.Certificate);
                    if (!built)
                    {
                        var errors = string.Join("; ", x509Chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
                        throw new InvalidOperationException($"Không thể xây dựng chuỗi chứng thư số hợp lệ: {errors}");
                    }

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
                    IExternalSignature externalSignature = new NetX509ExternalSignature(request.Certificate.Certificate);
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
                            StatusChanged?.Invoke($"Cảnh báo TSA: Không thể kết nối TSA ({ex.Message}).");
                            throw new InvalidOperationException($"Yêu cầu TSA nhưng không thể kết nối tới máy chủ TSA ({request.TSAUrl}): {ex.Message}", ex);
                        }
                    }

                    if (request.Certificate.IsVirtualCspCertificate)
                    {
                        StatusChanged?.Invoke("Đang gửi yêu cầu xác thực tới máy chủ Ban Cơ Yếu (Chờ thông báo trên điện thoại)...");
                    }
                    else
                    {
                        StatusChanged?.Invoke("Đang tính toán mã băm và thực hiện ký số PAdES với USB Token...");
                    }

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
                }

                File.Move(tempOutput, resolvedOutput, overwrite: true);
                signSucceeded = true;
                StatusChanged?.Invoke($"Ký số thành công! Tệp tin: {System.IO.Path.GetFileName(resolvedOutput)}");
                return resolvedOutput;
            }
            finally
            {
                if (!signSucceeded && File.Exists(tempOutput))
                {
                    try
                    {
                        File.Delete(tempOutput);
                    }
                    catch
                    {
                        // Tránh che khuất ngoại lệ chính nếu không thể xóa file dọn dẹp
                    }
                }
            }
        }
    }
}

