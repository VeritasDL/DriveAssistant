using FATX.Analyzers;
using System;
using System.IO;
using System.Text.Json;

namespace FATXTools.Wpf;

public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Drive Assistant",
        "settings.json");

    public FileCarverInterval FileCarverInterval { get; set; } = FileCarverInterval.Sector;

    public int MetadataIntervalClusters { get; set; } = 1;

    public int MetadataParallelWorkers { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);

    public string LogFile { get; set; } = "log.txt";

    public bool EnableFileLogging { get; set; } = true;

    public string CustomCarversFile { get; set; } = "custom_carvers.json";

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

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
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
            MetadataIntervalClusters = MetadataIntervalClusters,
            MetadataParallelWorkers = MetadataParallelWorkers,
            LogFile = LogFile,
            EnableFileLogging = EnableFileLogging,
            CustomCarversFile = CustomCarversFile,
            PlayStationMountToolPath = PlayStationMountToolPath,
            Theme = WpfTheme.NormalizeName(Theme)
        };
    }
}
