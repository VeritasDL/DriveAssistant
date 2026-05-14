using System;
using System.IO;

namespace FATX.Recovery.Models
{
    /// <summary>
    /// Configuration for Xbox 360 HDD recovery operations.
    /// </summary>
    public class Xbox360HddRecoveryConfig
    {
        public string HddJsonPath { get; set; } = string.Empty;
        public string RecoveredFilesPath { get; set; } = string.Empty;
        public string DeletedFilesPath { get; set; } = string.Empty;
        public string OutputImagePath { get; set; } = string.Empty;
        public bool IncludeDeletedFiles { get; set; } = true;
        public bool EnableFuzzyMatching { get; set; } = true;
        public uint SerialNumber { get; set; } = 0;

        public bool Validate()
        {
            if (string.IsNullOrWhiteSpace(HddJsonPath) || !File.Exists(HddJsonPath))
                throw new FileNotFoundException($"HDD JSON file not found: {HddJsonPath}");

            if (string.IsNullOrWhiteSpace(RecoveredFilesPath) || !Directory.Exists(RecoveredFilesPath))
                throw new DirectoryNotFoundException($"Recovered files directory not found: {RecoveredFilesPath}");

            if (string.IsNullOrWhiteSpace(DeletedFilesPath) || !Directory.Exists(DeletedFilesPath))
                throw new DirectoryNotFoundException($"Deleted files directory not found: {DeletedFilesPath}");

            if (string.IsNullOrWhiteSpace(OutputImagePath))
                throw new ArgumentException("Output image path must be specified");

            var outputDir = Path.GetDirectoryName(OutputImagePath);
            if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
                throw new DirectoryNotFoundException($"Output directory does not exist: {outputDir}");

            return true;
        }
    }
}
