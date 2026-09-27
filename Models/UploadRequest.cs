using System;
using System.Collections.Generic;

namespace VGCASignToolPro.Models
{
    public class UploadRequest
    {
        public string FilePath { get; set; } = string.Empty;
        public string UploadUrl { get; set; } = string.Empty;
        public string? AuthToken { get; set; }
        public string? DocumentTitle { get; set; }
        public string? SignerName { get; set; }
        public string? Department { get; set; }
        public string? OriginalFileName { get; set; }
        public Dictionary<string, string> ExtraHeaders { get; set; } = new();
        public Dictionary<string, string> ExtraFormData { get; set; } = new();
    }

    public class UploadResult
    {
        public bool IsSuccess { get; set; }
        public int StatusCode { get; set; }
        public string ResponseBody { get; set; } = string.Empty;
        public string? ServerMessage { get; set; }
        public long ResponseTimeMs { get; set; }
    }
}
