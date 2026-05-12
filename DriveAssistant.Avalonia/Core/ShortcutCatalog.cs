using System.Text.Json;

namespace DriveAssistant.Avalonia.Core;

internal sealed record ShortcutDefinition(string Id, string Category, string Name, string DefaultGesture);

internal static class ShortcutCatalog
{
    public const string OpenImage = "open_image";
    public const string SaveSelected = "save_selected";
    public const string Search = "search";
    public const string MetadataScan = "metadata_scan";
    public const string FileCarver = "file_carver";
    public const string AddPartition = "add_partition";
    public const string UnmountPartition = "unmount_partition";
    public const string TogglePartitions = "toggle_partitions";
    public const string ToggleInspector = "toggle_inspector";
    public const string ToggleResultsWindow = "toggle_results_window";
    public const string Settings = "settings";
    public const string About = "about";

    public static readonly IReadOnlyList<ShortcutDefinition> All =
    [
        new(OpenImage, "File", "Open image", "Ctrl+O"),
        new(SaveSelected, "File", "Save selected", "Ctrl+E"),
        new(Search, "Navigate", "Search results", "Ctrl+F"),
        new(MetadataScan, "Analysis", "Metadata scan", "F5"),
        new(FileCarver, "Analysis", "File carver", "Ctrl+F5"),
        new(TogglePartitions, "View", "Show or hide partitions", "Ctrl+1"),
        new(ToggleInspector, "View", "Show or hide inspector", "Ctrl+2"),
        new(ToggleResultsWindow, "View", "Pop out or dock results", "Ctrl+4"),
        new(AddPartition, "Tools", "Add custom partition", "Ctrl+Shift+A"),
        new(UnmountPartition, "Tools", "Unmount partition", "Ctrl+U"),
        new(Settings, "Tools", "Settings", "Ctrl+,"),
        new(About, "Help", "About", "F1")
    ];

    public static Dictionary<string, string> DefaultMap()
    {
        return All.ToDictionary(definition => definition.Id, definition => definition.DefaultGesture, StringComparer.OrdinalIgnoreCase);
    }

    public static Dictionary<string, string> Normalize(IDictionary<string, string>? shortcuts)
    {
        var normalized = DefaultMap();
        if (shortcuts == null)
        {
            return normalized;
        }

        foreach (var definition in All)
        {
            if (shortcuts.TryGetValue(definition.Id, out var gesture))
            {
                normalized[definition.Id] = gesture?.Trim() ?? string.Empty;
            }
        }

        return normalized;
    }

    public static string GetGestureText(IDictionary<string, string>? shortcuts, string id)
    {
        var map = Normalize(shortcuts);
        return map.TryGetValue(id, out var gesture) ? gesture : string.Empty;
    }
}

public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Drive Assistant",
        "avalonia-settings.json");

    public string? KeyPath { get; set; }

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
        Shortcuts = ShortcutCatalog.Normalize(Shortcuts);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
