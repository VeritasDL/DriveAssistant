using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace FATXTools.Wpf;

public partial class ConsoleImageOpenWindow : Window
{
    private readonly IReadOnlyList<ConsoleImageKindOption> _options =
    [
        new("Auto detect", ConsoleDriveImageKind.Auto, false),
        new("Xbox / Xbox 360 FATX", ConsoleDriveImageKind.XboxFatx, false),
        new("Xbox One / Xbox Series GPT + NTFS", ConsoleDriveImageKind.XboxGptNtfs, false),
        new("Generic NTFS / FAT32 / exFAT", ConsoleDriveImageKind.GenericFileSystem, false),
        new("PlayStation 3 / PlayStation 4 HDD", ConsoleDriveImageKind.PlayStationHdd, true)
    ];

    public ConsoleImageOpenWindow(string? imagePath = null, ConsoleDriveImageKind initialKind = ConsoleDriveImageKind.Auto)
    {
        InitializeComponent();
        ImageKindComboBox.ItemsSource = _options;
        ImageKindComboBox.SelectedItem = _options.FirstOrDefault(option => option.Kind == initialKind) ?? _options[0];
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            ImagePathTextBox.Text = imagePath;
        }

        UpdateKeyVisibility();
        UpdateStatus();
    }

    public string ImagePath => ImagePathTextBox.Text.Trim();

    public string KeyPath => KeyPathTextBox.Text.Trim();

    public ConsoleDriveImageKind ImageKind => (ImageKindComboBox.SelectedItem as ConsoleImageKindOption)?.Kind
        ?? ConsoleDriveImageKind.Auto;

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Disk Images (*.img;*.bin;*.raw;*.imgc)|*.img;*.bin;*.raw;*.imgc|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            ImagePathTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Key files (*.bin;*.key;*.dat;eid_root_key)|*.bin;*.key;*.dat;eid_root_key|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            KeyPathTextBox.Text = dialog.FileName;
        }
    }

    private void ImageKindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateKeyVisibility();
        UpdateStatus();
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateStatus();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(ImagePath))
        {
            StatusTextBlock.Text = "Select an existing disk image.";
            return;
        }

        if (RequiresKey(ImageKind) && !File.Exists(KeyPath))
        {
            StatusTextBlock.Text = "Select the required PlayStation HDD key file.";
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void UpdateKeyVisibility()
    {
        var visibility = RequiresKey(ImageKind) ? Visibility.Visible : Visibility.Collapsed;
        KeyPathTextBox.Visibility = visibility;
        BrowseKeyButton.Visibility = visibility;
    }

    private void UpdateStatus()
    {
        if (!string.IsNullOrWhiteSpace(ImagePath) && !File.Exists(ImagePath))
        {
            StatusTextBlock.Text = "Image path does not exist.";
            return;
        }

        if (RequiresKey(ImageKind))
        {
            StatusTextBlock.Text = "PlayStation HDD images require the matching PS3 EID root key or PS4 EAP HDD key.";
            return;
        }

        StatusTextBlock.Text = ImageKind == ConsoleDriveImageKind.Auto
            ? "Auto detect tries FATX, Xbox GPT/NTFS, generic NTFS/FAT32/exFAT, then asks for PlayStation keys if needed."
            : "This image type does not require a key file.";
    }

    private static bool RequiresKey(ConsoleDriveImageKind kind)
    {
        return kind == ConsoleDriveImageKind.PlayStationHdd;
    }
}

public enum ConsoleDriveImageKind
{
    Auto,
    XboxFatx,
    XboxGptNtfs,
    GenericFileSystem,
    PlayStationHdd
}

public sealed record ConsoleImageKindOption(string Name, ConsoleDriveImageKind Kind, bool RequiresKey)
{
    public override string ToString()
    {
        return Name;
    }
}
