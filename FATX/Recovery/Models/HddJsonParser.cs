using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FATX.Recovery.Models
{
    /// <summary>
    /// JSON models for parsing HDD metadata from Drive Assistant.
    /// </summary>
    public static class HddJsonParser
    {
        [Serializable]
        public class HddJsonData
        {
            [JsonPropertyName("version")]
            public int Version { get; set; }

            [JsonPropertyName("application")]
            public string Application { get; set; } = string.Empty;

            [JsonPropertyName("savedAtUtc")]
            public DateTime SavedAtUtc { get; set; }

            [JsonPropertyName("sourceImage")]
            public string SourceImage { get; set; } = string.Empty;

            [JsonPropertyName("activePartitionName")]
            public string ActivePartitionName { get; set; } = string.Empty;

            [JsonPropertyName("partitions")]
            public List<PartitionJsonData> Partitions { get; set; } = new();
        }

        [Serializable]
        public class PartitionJsonData
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("offset")]
            public long Offset { get; set; }

            [JsonPropertyName("length")]
            public long Length { get; set; }

            [JsonPropertyName("family")]
            public string Family { get; set; } = string.Empty;

            [JsonPropertyName("status")]
            public string Status { get; set; } = string.Empty;

            [JsonPropertyName("usedSpace")]
            public long UsedSpace { get; set; }

            [JsonPropertyName("freeSpace")]
            public long FreeSpace { get; set; }

            [JsonPropertyName("totalSpace")]
            public long TotalSpace { get; set; }

            [JsonPropertyName("originalFilesystem")]
            public List<SnapshotFileEntry> OriginalFilesystem { get; set; } = new();

            [JsonPropertyName("analysis")]
            public PartitionAnalysisSnapshot Analysis { get; set; } = new();
        }

        [Serializable]
        public class PartitionAnalysisSnapshot
        {
            [JsonPropertyName("metadataAnalyzer")]
            public List<SnapshotFileEntry> MetadataAnalyzer { get; set; } = new();

            [JsonPropertyName("fileCarver")]
            public List<CarvedFileSnapshot> FileCarver { get; set; } = new();
        }

        [Serializable]
        public class SnapshotFileEntry
        {
            [JsonPropertyName("path")]
            public string Path { get; set; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("kind")]
            public string Kind { get; set; } = string.Empty;

            [JsonPropertyName("isDirectory")]
            public bool IsDirectory { get; set; }

            [JsonPropertyName("size")]
            public long Size { get; set; }

            [JsonPropertyName("created")]
            public DateTime Created { get; set; }

            [JsonPropertyName("modified")]
            public DateTime Modified { get; set; }

            [JsonPropertyName("accessed")]
            public DateTime Accessed { get; set; }

            [JsonPropertyName("offset")]
            public long Offset { get; set; }

            [JsonPropertyName("cluster")]
            public long Cluster { get; set; }

            [JsonPropertyName("isDeleted")]
            public bool IsDeleted { get; set; }

            [JsonPropertyName("attributes")]
            public string Attributes { get; set; } = string.Empty;

            [JsonPropertyName("firstCluster")]
            public long FirstCluster { get; set; }

            [JsonPropertyName("fragmentation")]
            public string Fragmentation { get; set; } = string.Empty;

            [JsonPropertyName("metadataStatus")]
            public string MetadataStatus { get; set; } = string.Empty;

            [JsonPropertyName("children")]
            public List<SnapshotFileEntry> Children { get; set; } = new();
        }

        [Serializable]
        public class CarvedFileSnapshot
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("kind")]
            public string Kind { get; set; } = string.Empty;

            [JsonPropertyName("size")]
            public long Size { get; set; }

            [JsonPropertyName("offset")]
            public long Offset { get; set; }
        }
    }
}
