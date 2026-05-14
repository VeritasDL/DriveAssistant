using FATX.Analyzers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public bool ZeroFillOverwrittenRecoveryClusters { get; set; } = false;

    public string LogFile { get; set; } = "log.txt";

    public bool EnableFileLogging { get; set; } = true;

    public string CustomCarversFile { get; set; } = DefaultCustomCarversFile;

    public string Theme { get; set; } = WpfTheme.Dark;

    public List<string> RecentImages { get; set; } = [];

    public Dictionary<string, string> Shortcuts { get; set; } = ShortcutCatalog.DefaultMap();

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
            settings.RecentImages = NormalizeRecentImages(settings.RecentImages);
            settings.Shortcuts = ShortcutCatalog.Normalize(settings.Shortcuts);
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
            ZeroFillOverwrittenRecoveryClusters = ZeroFillOverwrittenRecoveryClusters,
            LogFile = LogFile,
            EnableFileLogging = EnableFileLogging,
            CustomCarversFile = NormalizeCustomCarversFile(CustomCarversFile),
            Theme = WpfTheme.NormalizeName(Theme),
            RecentImages = NormalizeRecentImages(RecentImages),
            Shortcuts = ShortcutCatalog.Normalize(Shortcuts).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
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

    public static List<string> NormalizeRecentImages(IEnumerable<string>? paths)
    {
        var rows = new List<string>();
        if (paths == null)
        {
            return rows;
        }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var trimmed = path.Trim();
            if (!rows.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(trimmed);
            }

            if (rows.Count >= 12)
            {
                break;
            }
        }

        return rows;
    }
}
