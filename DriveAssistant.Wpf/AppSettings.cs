using FATX.Analyzers;
using System;
using System.IO;
using System.Text.Json;

namespace FATXTools.Wpf;

public enum ScanProfile
{
    Fast,
    Balanced,
    Exhaustive
}

public sealed class AppSettings
{
    public static readonly string DefaultCustomCarversFile = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "custom_carvers.json");

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Drive Assistant",
        "settings.json");

    public FileCarverInterval FileCarverInterval { get; set; } = FileCarverInterval.Sector;

    public ScanProfile ScanProfile { get; set; } = ScanProfile.Balanced;

    public int MetadataIntervalClusters { get; set; } = 1;

    public int MetadataParallelWorkers { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);

    public string LogFile { get; set; } = "log.txt";

    public bool EnableFileLogging { get; set; } = true;

    public string CustomCarversFile { get; set; } = DefaultCustomCarversFile;

    public string PlayStationMountToolPath { get; set; } = string.Empty;

    public string Theme { get; set; } = WpfTheme.Dark;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            settings.CustomCarversFile = NormalizeCustomCarversFile(settings.CustomCarversFile);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, options));
    }

    public AppSettings Clone()
    {
        return new AppSettings
        {
            FileCarverInterval = FileCarverInterval,
            ScanProfile = ScanProfile,
            MetadataIntervalClusters = MetadataIntervalClusters,
            MetadataParallelWorkers = MetadataParallelWorkers,
            LogFile = LogFile,
            EnableFileLogging = EnableFileLogging,
            CustomCarversFile = NormalizeCustomCarversFile(CustomCarversFile),
            PlayStationMountToolPath = PlayStationMountToolPath,
            Theme = WpfTheme.NormalizeName(Theme)
        };
    }

    public static string NormalizeCustomCarversFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            string.Equals(path.Trim(), "custom_carvers.json", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultCustomCarversFile;
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
    }
}
