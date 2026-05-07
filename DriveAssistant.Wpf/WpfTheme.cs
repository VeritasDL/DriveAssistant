using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace FATXTools.Wpf;

public static class WpfTheme
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["AppBg"] = "#111522",
        ["ToolbarBg"] = "#171D2B",
        ["PanelBg"] = "#181C2A",
        ["PanelBg2"] = "#202637",
        ["PanelBg3"] = "#283145",
        ["Line"] = "#43506A",
        ["LineSoft"] = "#303A50",
        ["Text"] = "#F8FAFC",
        ["Muted"] = "#CDD6E5",
        ["Accent"] = "#4A9EFF",
        ["AccentSoft"] = "#203A5E",
        ["Success"] = "#38D47B",
        ["TitleBarBg"] = "#0F1420",
        ["TitleBarText"] = "#F8FAFC",
        ["RootGradientStart"] = "#101421",
        ["RootGradientMid"] = "#151927",
        ["RootGradientEnd"] = "#0F1624",
        ["SectionText"] = "#DCE7F7",
        ["DisabledText"] = "#9AA8BC",
        ["SecondaryButtonBg"] = "#162033",
        ["SecondaryButtonBorder"] = "#3A4964",
        ["InputBg"] = "#111725",
        ["TreeBg"] = "#151A28",
        ["TreeItemText"] = "#E6EEFB",
        ["DataGridRowBg"] = "#171D2C",
        ["DataGridAltRowBg"] = "#151B29",
        ["DataGridHeaderBg"] = "#222A3B",
        ["DataGridHeaderHoverBg"] = "#273047",
        ["DataGridHeaderText"] = "#D8E3F5",
        ["DirectoryRowBg"] = "#1A2435",
        ["ScrollBarBg"] = "#0F1420",
        ["ScrollBarTrackBg"] = "#0A101C",
        ["ScrollBarHostBg"] = "#0C1220",
        ["AccentHover"] = "#73B7FF",
        ["ProgressTrackBg"] = "#101725",
        ["StatusContiguousBg"] = "#1B2F2A",
        ["StatusFragmentedBg"] = "#342B1F",
        ["StatusFullyFragmentedBg"] = "#3A2226",
        ["StatusUnrecoverableBg"] = "#4A1F25",
        ["StatusEmptyBg"] = "#1C2635",
        ["SystemHighlightText"] = "#F8FAFC",
    };

    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["AppBg"] = "#EEF2F7",
        ["ToolbarBg"] = "#F8FAFC",
        ["PanelBg"] = "#FFFFFF",
        ["PanelBg2"] = "#F1F5F9",
        ["PanelBg3"] = "#E7EDF5",
        ["Line"] = "#B9C6D6",
        ["LineSoft"] = "#D6DEE9",
        ["Text"] = "#182233",
        ["Muted"] = "#475569",
        ["Accent"] = "#2F72E5",
        ["AccentSoft"] = "#D9E9FF",
        ["Success"] = "#16834A",
        ["TitleBarBg"] = "#F8FAFC",
        ["TitleBarText"] = "#182233",
        ["RootGradientStart"] = "#EEF2F7",
        ["RootGradientMid"] = "#F7F9FC",
        ["RootGradientEnd"] = "#E8EEF5",
        ["SectionText"] = "#334155",
        ["DisabledText"] = "#7A8696",
        ["SecondaryButtonBg"] = "#F7FAFD",
        ["SecondaryButtonBorder"] = "#B9C6D6",
        ["InputBg"] = "#FFFFFF",
        ["TreeBg"] = "#FFFFFF",
        ["TreeItemText"] = "#243044",
        ["DataGridRowBg"] = "#FFFFFF",
        ["DataGridAltRowBg"] = "#F7FAFD",
        ["DataGridHeaderBg"] = "#E9EFF7",
        ["DataGridHeaderHoverBg"] = "#DDE8F5",
        ["DataGridHeaderText"] = "#253348",
        ["DirectoryRowBg"] = "#EFF6FF",
        ["ScrollBarBg"] = "#E8EEF6",
        ["ScrollBarTrackBg"] = "#D9E2EE",
        ["ScrollBarHostBg"] = "#EEF3F8",
        ["AccentHover"] = "#5A93F0",
        ["ProgressTrackBg"] = "#DDE6F0",
        ["StatusContiguousBg"] = "#E3F7E9",
        ["StatusFragmentedBg"] = "#FFF4D6",
        ["StatusFullyFragmentedBg"] = "#FDE7EA",
        ["StatusUnrecoverableBg"] = "#F8D7DD",
        ["StatusEmptyBg"] = "#E9EFF8",
        ["SystemHighlightText"] = "#FFFFFF",
    };

    public static string NormalizeName(string? theme)
    {
        return string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
    }

    public static void Apply(string? theme)
    {
        var palette = NormalizeName(theme) == Light ? LightPalette : DarkPalette;
        var resources = Application.Current.Resources;

        foreach (var (name, hex) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            resources[name] = color;
            SetBrush(resources, $"{name}Brush", color);
        }

        SetBrush(resources, SystemColors.HighlightBrushKey, Parse(palette["AccentSoft"]));
        SetBrush(resources, SystemColors.HighlightTextBrushKey, Parse(palette["SystemHighlightText"]));
        SetBrush(resources, SystemColors.InactiveSelectionHighlightBrushKey, Parse(palette["AccentSoft"]));
        SetBrush(resources, SystemColors.InactiveSelectionHighlightTextBrushKey, Parse(palette["SystemHighlightText"]));
    }

    private static Color Parse(string hex)
    {
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    private static void SetBrush(ResourceDictionary resources, object key, Color color)
    {
        if (resources[key] is SolidColorBrush { IsFrozen: false } brush)
        {
            brush.Color = color;
        }
        else
        {
            resources[key] = new SolidColorBrush(color);
        }
    }
}
