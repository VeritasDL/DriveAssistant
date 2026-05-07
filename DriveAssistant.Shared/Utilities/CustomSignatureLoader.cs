using FATX.Analyzers.Signatures;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FATXTools.Utilities
{
    public static class CustomSignatureLoader
    {
        public static List<CustomSignatureDefinition> Load(string path)
        {
            var signatures = new List<CustomSignatureDefinition>();
            path = ResolvePath(path);

            if (!File.Exists(path))
            {
                return signatures;
            }

            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<List<CustomSignatureDefinition>>(json);
            if (parsed != null)
            {
                signatures.AddRange(parsed);
            }

            return signatures;
        }

        public static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                string.Equals(path.Trim(), "custom_carvers.json", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "custom_carvers.json");
            }

            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }
    }
}
