using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace VGCASignToolPro.Models
{
    public class AppReleaseModel
    {
        [JsonProperty("version")]
        public string Version { get; set; } = "2.0.0";

        [JsonProperty("versionCode")]
        public int VersionCode { get; set; } = 200;

        [JsonProperty("releaseDate")]
        public string ReleaseDate { get; set; } = string.Empty;

        [JsonProperty("title")]
        public string Title { get; set; } = string.Empty;

        [JsonProperty("status")]
        public string Status { get; set; } = "ACTIVE"; // ACTIVE, DISABLED, REVOKED

        [JsonProperty("mandatory")]
        public bool Mandatory { get; set; } = false;

        [JsonProperty("minSupportedVersion")]
        public string MinSupportedVersion { get; set; } = "2.0.0";

        [JsonProperty("downloadUrl")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonProperty("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonProperty("packageSizeMb")]
        public double PackageSizeMb { get; set; } = 0.0;

        [JsonProperty("changelog")]
        public List<string> Changelog { get; set; } = new();
    }

    public class UpdateCheckResult
    {
        public bool Success { get; set; }
        public bool HasUpdate { get; set; }
        public bool IsMandatory { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public AppReleaseModel? Release { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class UpdateDownloadProgress
    {
        public long BytesReceived { get; set; }
        public long? TotalBytesToReceive { get; set; }
        public int ProgressPercentage { get; set; }
        public double SpeedKbs { get; set; }
    }
}
