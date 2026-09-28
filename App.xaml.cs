using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace VGCASignToolPro;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Contains("--test-interactive-signing"))
        {
            string logPath = @"C:\Users\HPZBook\Desktop\VGCASignToolPro\test_run.log";
            File.WriteAllText(logPath, $"[TEST] Started at {DateTime.Now}\n");
            try
            {
                File.AppendAllText(logPath, "[TEST] Bắt đầu kiểm thử tính năng Chèn Chữ Ký Tương Tác & Ký Số Thực Tế...\n");
                var certService = new Services.CertificateService();
                var signer = new Services.PdfSigningService();

                var cert = certService.GetActiveVGCACertificate() ?? certService.LoadCertificates(true).FirstOrDefault();
                if (cert == null)
                {
                    File.AppendAllText(logPath, "[TEST INFO] Chưa đăng nhập Token VGCA, tự động tạo chứng thư kiểm thử trong bộ nhớ để test tọa độ và luồng ký...\n");
                    using var rsa = System.Security.Cryptography.RSA.Create(2048);
                    var certReq = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=KIEM THU TOA DO CHU KY TUONG TAC", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                    var selfCert = certReq.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
                    cert = new Models.CertificateModel(selfCert);
                }
                File.AppendAllText(logPath, $"[TEST] Chứng thư sử dụng: {cert.CommonName} (Serial: {cert.SerialNumber})\n");

                string sourcePdf = @"C:\Users\HPZBook\Desktop\Soan_GV\Tin 9 bài 1_converted.pdf";
                string stampPath = @"C:\Users\HPZBook\Desktop\quyen.png";
                string outputPdf = Path.Combine(Path.GetDirectoryName(sourcePdf)!, "Tin9_Test_Interactive_Stamp.pdf");

                if (File.Exists(outputPdf)) File.Delete(outputPdf);

                float testX = 350.5f;
                float testY = 150.2f;
                float testW = 140f;
                float testH = 140f;
                int testPage = 1;

                var request = new Models.SigningRequest
                {
                    SourcePdfPath = sourcePdf,
                    OutputPdfPath = outputPdf,
                    Certificate = cert,
                    PageNumber = testPage,
                    X = testX,
                    Y = testY,
                    Width = testW,
                    Height = testH,
                    StampImagePath = stampPath,
                    Reason = "Phê duyệt giáo án điện tử",
                    SignerDisplayName = cert.CommonName,
                    UseTSA = false // Offline test
                };

                File.AppendAllText(logPath, $"[TEST] Đang thực hiện ký tại Trang {testPage} (X={testX}, Y={testY}, W={testW}, H={testH})...\n");
                string resultPath = signer.SignPdfAsync(request).GetAwaiter().GetResult();
                File.AppendAllText(logPath, $"[TEST SUCCESS] Đã tạo file ký: {resultPath}\n");

                // Kiểm định lại bằng iText
                using var reader = new iText.Kernel.Pdf.PdfReader(resultPath);
                using var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader);
                var form = iText.Forms.PdfAcroForm.GetAcroForm(pdfDoc, false);
                var fields = form.GetAllFormFields();
                File.AppendAllText(logPath, $"[TEST] Tổng số trường chữ ký trong file: {fields.Count}\n");

                bool foundMatch = false;
                foreach (var kvp in fields)
                {
                    var widget = kvp.Value.GetWidgets().FirstOrDefault();
                    if (widget != null)
                    {
                        var rect = widget.GetRectangle().ToRectangle();
                        File.AppendAllText(logPath, $"[TEST] Trường '{kvp.Key}': X={rect.GetX():F1}, Y={rect.GetY():F1}, W={rect.GetWidth():F1}, H={rect.GetHeight():F1}\n");
                        if (Math.Abs(rect.GetX() - testX) < 1.0 && Math.Abs(rect.GetY() - testY) < 1.0)
                        {
                            foundMatch = true;
                        }
                    }
                }

                if (!foundMatch)
                {
                    File.AppendAllText(logPath, "[TEST FAILED] Tọa độ chữ ký không khớp với yêu cầu chèn tương tác!\n");
                    Environment.Exit(2);
                }
                else
                {
                    File.AppendAllText(logPath, "[TEST PASS] Tọa độ chữ ký khớp 100% với vị trí chèn tương tác!\n");
                    Environment.Exit(0);
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(logPath, $"[TEST EXCEPTION] {ex}\n");
                Environment.Exit(3);
            }
            return;
        }

        base.OnStartup(e);
    }
}

