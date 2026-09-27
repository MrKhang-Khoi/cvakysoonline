using System;

namespace VGCASignToolPro.Models
{
    public class SigningRequest
    {
        public string SourcePdfPath { get; set; } = string.Empty;
        public string OutputPdfPath { get; set; } = string.Empty;
        public CertificateModel Certificate { get; set; } = null!;
        public int PageNumber { get; set; } = 1;
        public float X { get; set; } = 380;
        public float Y { get; set; } = 120;
        public float Width { get; set; } = 160;
        public float Height { get; set; } = 80;
        public string Reason { get; set; } = "Ký số văn bản điện tử";
        public string Location { get; set; } = "Việt Nam";
        public bool UseTSA { get; set; } = true;
        public string TSAUrl { get; set; } = "http://ca.gov.vn/tsa";
        public string? StampImagePath { get; set; }
        public SignatureProfile? Profile { get; set; }
        public string? SignerDisplayName { get; set; }
    }
}
