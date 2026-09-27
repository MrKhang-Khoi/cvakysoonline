using System;
using Newtonsoft.Json;

namespace VGCASignToolPro.Models
{
    public class SignatureProfile
    {
        [JsonProperty("Name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("ProfileType")]
        public int ProfileType { get; set; } = 3; // 1: Số, 2: Ngày, 3: Ký cá nhân, 4: Ký cơ quan

        [JsonProperty("Mode")]
        public int Mode { get; set; } = 1; // 0: Text, 1: Image, 2: Text + Image

        [JsonProperty("SignatureWidth")]
        public int SignatureWidth { get; set; } = 140;

        [JsonProperty("SignatureHeight")]
        public int SignatureHeight { get; set; } = 70;

        [JsonProperty("FontSize")]
        public int FontSize { get; set; } = 10;

        [JsonProperty("Image")]
        public string? Image { get; set; }

        [JsonProperty("ImagePath")]
        public string? ImagePath { get; set; }

        [JsonProperty("ShowDate")]
        public bool ShowDate { get; set; } = true;

        [JsonProperty("ShowOrg")]
        public bool ShowOrg { get; set; } = true;

        [JsonProperty("ShowEmail")]
        public bool ShowEmail { get; set; } = false;

        [JsonProperty("ShowLabel")]
        public bool ShowLabel { get; set; } = true;

        [JsonProperty("SignerName")]
        public string? SignerName { get; set; }

        [JsonProperty("AdditionalInfo")]
        public string? AdditionalInfo { get; set; }

        public string Description
        {
            get
            {
                string typeDesc = ProfileType switch
                {
                    1 => "Chèn số văn bản",
                    2 => "Chèn ngày tháng",
                    3 => "Ký cá nhân / Thủ trưởng",
                    4 => "Ký phát hành cơ quan",
                    _ => "Chữ ký số"
                };
                return $"{Name} ({typeDesc} - {SignatureWidth}x{SignatureHeight}px)";
            }
        }

        public override string ToString() => Description;
    }
}
