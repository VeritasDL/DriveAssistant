using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace FATXTools.Utilities
{
    public static class BuildInfo
    {
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        public static DateTime BuildDate
        {
            get
            {
                try
                {
                    var location = Assembly.GetExecutingAssembly().Location;
                    return File.GetLastWriteTime(location);
                }
                catch
                {
                    return DateTime.MinValue;
                }
            }
        }

        public static string CommitHash
        {
            get
            {
                var infoVersion = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(infoVersion))
                {
                    var plusIndex = infoVersion.IndexOf('+');
                    if (plusIndex >= 0 && plusIndex + 1 < infoVersion.Length)
                    {
                        return infoVersion.Substring(plusIndex + 1);
                    }
                }

                try
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "git",
                            Arguments = "rev-parse --short HEAD",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                        }
                    };
                    process.Start();
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(1000);
                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        return output;
                    }
                }
                catch
                {
                    // ignore
                }

                return "unknown";
            }
        }
    }
}
