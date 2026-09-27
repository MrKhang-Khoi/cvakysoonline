using System;
using System.Collections.Generic;
using System.IO;
using iText.Kernel.Pdf;
using iText.Signatures;

namespace VGCASignToolPro.Services
{
    public class VerifiedSignatureInfo
    {
        public string FieldName { get; set; } = string.Empty;
        public string SignerName { get; set; } = string.Empty;
        public DateTime SignDate { get; set; }
        public DateTime? TimestampDate { get; set; }
        public bool IsValid { get; set; }
        public bool CoversWholeDocument { get; set; }
        public int Revision { get; set; }
        public int TotalRevisions { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    public class PdfVerifierService
    {
        public List<VerifiedSignatureInfo> VerifyPdf(string pdfPath)
        {
            var results = new List<VerifiedSignatureInfo>();
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                return results;

            using var reader = new PdfReader(pdfPath);
            using var pdfDoc = new PdfDocument(reader);

            var signUtil = new SignatureUtil(pdfDoc);
            var sigNames = signUtil.GetSignatureNames();

            int totalRevisions = signUtil.GetTotalRevisions();

            foreach (var name in sigNames)
            {
                try
                {
                    var pkcs7 = signUtil.ReadSignatureData(name);
                    bool isValid = pkcs7.VerifySignatureIntegrityAndAuthenticity();
                    bool coversWhole = signUtil.SignatureCoversWholeDocument(name);
                    int revision = signUtil.GetRevision(name);

                    DateTime signDate = pkcs7.GetSignDate();
                    DateTime? timestampDate = null;
                    try
                    {
                        var ts = pkcs7.GetTimeStampDate();
                        if (ts != DateTime.MinValue)
                            timestampDate = ts;
                    }
                    catch { }

                    string signerName = pkcs7.GetSignName() ?? "Không rõ";
                    var signingCert = pkcs7.GetSigningCertificate();
                    string certSubject = signingCert?.GetSubjectDN()?.ToString() ?? string.Empty;

                    results.Add(new VerifiedSignatureInfo
                    {
                        FieldName = name,
                        SignerName = signerName,
                        SignDate = signDate,
                        TimestampDate = timestampDate,
                        IsValid = isValid,
                        CoversWholeDocument = coversWhole,
                        Revision = revision,
                        TotalRevisions = totalRevisions,
                        Reason = pkcs7.GetReason() ?? string.Empty,
                        Location = pkcs7.GetLocation() ?? string.Empty,
                        Details = $"Chứng thư: {certSubject} | Phiên bản sửa đổi: {revision}/{totalRevisions} | Toàn vẹn: {(isValid ? "HỢP LỆ" : "BỊ THAY ĐỔI")}"
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new VerifiedSignatureInfo
                    {
                        FieldName = name,
                        SignerName = "Lỗi đọc chữ ký",
                        IsValid = false,
                        Details = $"Lỗi phân tích: {ex.Message}"
                    });
                }
            }

            return results;
        }
    }
}
