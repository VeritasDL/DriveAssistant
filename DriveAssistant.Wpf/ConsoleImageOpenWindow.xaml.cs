using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FATXTools.Wpf;

public partial class ConsoleImageOpenWindow : Window
{
    private const string SwitchKeyStatus =
        "Switch encrypted BIS partitions need a plain text key file. Include BIS KEY 0-3 crypt/tweak pairs, or prod.keys names like bis_key_03_crypt and bis_key_03_tweak. Hover the key field for an example.";

    private const string SwitchKeyHelp =
        "Switch key file example:\n" +
        "BIS KEY 0 (crypt): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 0 (tweak): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 1 (crypt): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 1 (tweak): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 2 (crypt): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 2 (tweak): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 3 (crypt): 00112233445566778899AABBCCDDEEFF\n" +
        "BIS KEY 3 (tweak): 00112233445566778899AABBCCDDEEFF\n\n" +
        "prod.keys names are also accepted, for example:\n" +
        "bis_key_03_crypt = 00112233445566778899AABBCCDDEEFF\n" +
        "bis_key_03_tweak = 00112233445566778899AABBCCDDEEFF\n\n" +
        "BIS 0 = PRODINFO/PRODINFOF, BIS 1 = SAFE, BIS 2 = SYSTEM, BIS 3 = USER.";

    private readonly IReadOnlyList<ConsoleImageKindOption> _options =
    [
        new("Auto detect", ConsoleDriveImageKind.Auto, false),
        new("Xbox / Xbox 360 FATX", ConsoleDriveImageKind.XboxFatx, false),
        new("Xbox One / Xbox Series GPT + NTFS", ConsoleDriveImageKind.XboxGptNtfs, false),
        new("Nintendo Switch NAND / eMMC", ConsoleDriveImageKind.NintendoSwitchNand, false),
        new("Nintendo Wii / Wii U / DS / 3DS", ConsoleDriveImageKind.NintendoWiiWiiU, false),
        new("Generic NTFS / FAT32 / exFAT", ConsoleDriveImageKind.GenericFileSystem, false),
        new("Legacy devkit / ROM / save media", ConsoleDriveImageKind.LegacyDevkitMedia, false),
        new("PlayStation 2 HDD", ConsoleDriveImageKind.PlayStation2Hdd, false),
        new("PlayStation 3 HDD", ConsoleDriveImageKind.PlayStation3Hdd, false),
        new("PlayStation 4 / PlayStation 4 Pro HDD", ConsoleDriveImageKind.PlayStation4Hdd, false)
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
            Filter = "Console Images (*.img;*.bin;*.raw;*.imgc;*.iso;*.wbfs;*.zip;*.wud;*.wux;*.gcm;*.gdi;*.cdi;*.vmu;*.vms;*.dci;*.mcr;*.mcd;*.psx;*.ps2;*.z64;*.n64;*.v64;*.sra;*.eep;*.fla;*.gb;*.gbc;*.gba;*.sav;*.nds;*.dsi;*.3ds;*.cci;*.cxi;*.cfa;*.csu;*.app)|*.img;*.bin;*.raw;*.imgc;*.iso;*.wbfs;*.zip;*.wud;*.wux;*.gcm;*.gdi;*.cdi;*.vmu;*.vms;*.dci;*.mcr;*.mcd;*.psx;*.ps2;*.z64;*.n64;*.v64;*.sra;*.eep;*.fla;*.gb;*.gbc;*.gba;*.sav;*.nds;*.dsi;*.3ds;*.cci;*.cxi;*.cfa;*.csu;*.app|Switch NAND (*.bin)|*.bin|All files (*.*)|*.*",
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
            Filter = "All files (*.*)|*.*|Key files (*.bin;*.key;*.dat;*.txt;*.gz;*.tgz;prod.keys;keys.txt;eid_root_key;otp.bin;seeprom.bin;common-key;sd-key;sd-iv)|*.bin;*.key;*.dat;*.txt;*.gz;*.tgz;prod.keys;keys.txt;eid_root_key;otp.bin;seeprom.bin;common-key;sd-key;sd-iv",
            FilterIndex = 1,
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

        if (!string.IsNullOrWhiteSpace(KeyPath) && !File.Exists(KeyPath) && !Directory.Exists(KeyPath))
        {
            StatusTextBlock.Text = "Selected key file does not exist.";
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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowMaximized();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove can throw if mouse capture changes during the drag.
            }
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowMaximized();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ToggleWindowMaximized()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void UpdateKeyVisibility()
    {
        var visibility = RequiresKey(ImageKind) ? Visibility.Visible : Visibility.Collapsed;
        KeyPathTextBox.Visibility = visibility;
        BrowseKeyButton.Visibility = visibility;
        KeyPathTextBox.ToolTip = ImageKind == ConsoleDriveImageKind.NintendoSwitchNand
            ? CreateWrappedToolTip(SwitchKeyHelp)
            : "Optional for already-decrypted PlayStation images.";
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
            StatusTextBlock.Text = ImageKind switch
            {
                ConsoleDriveImageKind.PlayStation3Hdd => "PlayStation 3 uses the managed PS3 reader. Select an EID root key for encrypted HDDs, or leave blank for already-decrypted images.",
                ConsoleDriveImageKind.NintendoSwitchNand => SwitchKeyStatus,
                ConsoleDriveImageKind.NintendoWiiWiiU => "Wii U WFS/dev HDD support accepts otp.bin plus seeprom.bin. Wii support also accepts a folder or .tar.gz containing common-key, sd-key, sd-iv, and md5-blanker.",
                _ => "PlayStation 4 keys are optional. Leave blank for already-decrypted PS4 images, or select the matching PS4 EAP HDD key for encrypted images."
            };
            return;
        }

        StatusTextBlock.Text = ImageKind switch
        {
            ConsoleDriveImageKind.Auto => "Auto detect tries FATX, Xbox GPT/NTFS, Switch NAND, Wii/Wii U/DS/3DS, PlayStation 2 APA, generic NTFS/FAT32/exFAT, and raw legacy/devkit media markers, then asks whether the image is PlayStation 3 or PlayStation 4 if needed.",
            ConsoleDriveImageKind.NintendoSwitchNand => "Switch NAND support reads the GPT and mounts readable FAT32 BIS partitions when BIS keys are supplied.",
            ConsoleDriveImageKind.NintendoWiiWiiU => "Nintendo support detects Wii RVT-H/NAND/disc/WBFS, Wii U WFS/dev storage, Nintendo DS NitroFS ROMs, DSi NAND layouts, and 3DS NCSD/NCCH containers for read-only inspection and carving.",
            ConsoleDriveImageKind.PlayStation2Hdd => "PlayStation 2 support detects APA/PFS and HDLoader partitions for read-only inspection and carving.",
            ConsoleDriveImageKind.LegacyDevkitMedia => "Legacy support recognizes PS1/PS2 memory-card images, Dreamcast Katana GD-ROM/VMU media, Nintendo 64 ROM/save media, and Game Boy-family ROM/save media for raw export and carving.",
            _ => "This image type does not require a key file."
        };
    }

    private static bool RequiresKey(ConsoleDriveImageKind kind)
    {
        return kind is ConsoleDriveImageKind.PlayStation3Hdd or ConsoleDriveImageKind.PlayStation4Hdd or ConsoleDriveImageKind.NintendoSwitchNand or ConsoleDriveImageKind.NintendoWiiWiiU;
    }

    private static ToolTip CreateWrappedToolTip(string text)
    {
        return new ToolTip
        {
            MaxWidth = 560,
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12
            }
        };
    }
}

public enum ConsoleDriveImageKind
{
    Auto,
    XboxFatx,
    XboxGptNtfs,
    NintendoSwitchNand,
    NintendoWiiWiiU,
    GenericFileSystem,
    PlayStation2Hdd,
    PlayStation3Hdd,
    PlayStation4Hdd,
    LegacyDevkitMedia
}

public sealed record ConsoleImageKindOption(string Name, ConsoleDriveImageKind Kind, bool RequiresKey)
{
    public override string ToString()
    {
        return Name;
    }
}
