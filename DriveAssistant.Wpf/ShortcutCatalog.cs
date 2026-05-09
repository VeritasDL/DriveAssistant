using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace FATXTools.Wpf;

internal sealed record ShortcutDefinition(string Id, string Category, string Name, string DefaultGesture);

internal static class ShortcutCatalog
{
    public const string OpenImage = "open_image";
    public const string LoadDatabase = "load_database";
    public const string SaveDatabase = "save_database";
    public const string SaveSelected = "save_selected";
    public const string Search = "search";
    public const string MetadataScan = "metadata_scan";
    public const string FileCarver = "file_carver";
    public const string AddPartition = "add_partition";
    public const string UnmountPartition = "unmount_partition";
    public const string TogglePartitions = "toggle_partitions";
    public const string ToggleInspector = "toggle_inspector";
    public const string ToggleClusterViewer = "toggle_cluster_viewer";
    public const string ToggleResultsWindow = "toggle_results_window";
    public const string Settings = "settings";
    public const string About = "about";

    public static readonly IReadOnlyList<ShortcutDefinition> All =
    [
        new(OpenImage, "File", "Open image", "Ctrl+O"),
        new(LoadDatabase, "File", "Load database", "Ctrl+Shift+O"),
        new(SaveDatabase, "File", "Save database", "Ctrl+S"),
        new(SaveSelected, "File", "Save selected", "Ctrl+E"),
        new(Search, "Navigate", "Search results", "Ctrl+F"),
        new(MetadataScan, "Analysis", "Metadata scan", "F5"),
        new(FileCarver, "Analysis", "File carver", "Ctrl+F5"),
        new(AddPartition, "Tools", "Add custom partition", "Ctrl+Shift+A"),
        new(UnmountPartition, "Tools", "Unmount partition", "Ctrl+U"),
        new(TogglePartitions, "View", "Show or hide partitions", "Ctrl+1"),
        new(ToggleInspector, "View", "Show or hide inspector", "Ctrl+2"),
        new(ToggleClusterViewer, "View", "Toggle cluster viewer", "Ctrl+3"),
        new(ToggleResultsWindow, "View", "Pop out or dock results", "Ctrl+4"),
        new(Settings, "Tools", "Settings", "Ctrl+,"),
        new(About, "Help", "About", "F1")
    ];

    private static readonly KeyGestureConverter GestureConverter = new();

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
        return map.TryGetValue(id, out var gesture) ? HumanizeGestureText(gesture) : string.Empty;
    }

    public static bool TryParseGesture(string? text, out KeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        try
        {
            if (GestureConverter.ConvertFromInvariantString(DehumanizeGestureText(text.Trim())) is KeyGesture parsed)
            {
                gesture = parsed;
                return true;
            }
        }
        catch (NotSupportedException)
        {
        }

        return false;
    }

    public static string FormatGesture(KeyGesture gesture)
    {
        var invariant = GestureConverter.ConvertToInvariantString(gesture) ?? gesture.GetDisplayStringForCulture(System.Globalization.CultureInfo.CurrentCulture);
        return HumanizeGestureText(invariant);
    }

    private static string DehumanizeGestureText(string text)
    {
        return text
            .Replace("+,", "+OemComma", StringComparison.OrdinalIgnoreCase)
            .Replace("+.", "+OemPeriod", StringComparison.OrdinalIgnoreCase)
            .Replace("+-", "+OemMinus", StringComparison.OrdinalIgnoreCase)
            .Replace("++", "+OemPlus", StringComparison.OrdinalIgnoreCase)
            .Replace("+/", "+OemQuestion", StringComparison.OrdinalIgnoreCase)
            .Replace("+;", "+OemSemicolon", StringComparison.OrdinalIgnoreCase)
            .Replace("+`", "+OemTilde", StringComparison.OrdinalIgnoreCase)
            .Replace("+[", "+OemOpenBrackets", StringComparison.OrdinalIgnoreCase)
            .Replace("+]", "+OemCloseBrackets", StringComparison.OrdinalIgnoreCase)
            .Replace("+\\", "+OemPipe", StringComparison.OrdinalIgnoreCase)
            .Replace("+\"", "+OemQuotes", StringComparison.OrdinalIgnoreCase);
    }

    private static string HumanizeGestureText(string text)
    {
        return text
            .Replace("+OemComma", "+,", StringComparison.OrdinalIgnoreCase)
            .Replace("+OemPeriod", "+.", StringComparison.OrdinalIgnoreCase)
            .Replace("+OemMinus", "+-", StringComparison.OrdinalIgnoreCase)
            .Replace("+OemPlus", "++", StringComparison.OrdinalIgnoreCase)
            .Replace("+OemQuestion", "+/", StringComparison.OrdinalIgnoreCase)
            .Replace("+OemSemicolon", "+;", StringComparison.OrdinalIgnoreCase)
            .Replace("+OemTilde", "+`", StringComparison.OrdinalIgnoreCase)
            .Replace("+OemOpenBrackets", "+[", StringComparison.OrdinalIgnoreCase)
            .Replace("+OemCloseBrackets", "+]", StringComparison.OrdinalIgnoreCase)
            .Replace("+OemPipe", "+\\", StringComparison.OrdinalIgnoreCase)
            .Replace("+OemQuotes", "+\"", StringComparison.OrdinalIgnoreCase);
    }
}
