﻿using System;
using System.Text.Json.Serialization;

namespace Bloxstrap.Models
{
    public class ClientVersion
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = null!;

        [JsonPropertyName("clientVersionUpload")]
        public string VersionGuid { get; set; } = null!;

        [JsonPropertyName("bootstrapperVersion")]
        public string BootstrapperVersion { get; set; } = null!;

        public DateTime? Timestamp { get; set; }
    }
}
