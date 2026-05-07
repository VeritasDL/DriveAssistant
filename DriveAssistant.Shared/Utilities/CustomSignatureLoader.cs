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
            if (string.IsNullOrWhiteSpace(path))
            {
                return signatures;
            }

            if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            }

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
    }
}
