using FATX.Analyzers;
using Microsoft.Win32;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FATXTools.Wpf;

public partial class SettingsWindow : Window
{
    private static readonly IntervalOption[] IntervalOptions =
    [
        new("Byte", "0x1", FileCarverInterval.Byte),
        new("Align", "0x10", FileCarverInterval.Align),
        new("Sector", "0x200", FileCarverInterval.Sector),
        new("Page", "0x1000", FileCarverInterval.Page),
        new("Cluster", "0x4000", FileCarverInterval.Cluster),
    ];

    private readonly AppSettings _settings;
    private readonly WorkerOption[] _workerOptions;
    private readonly ThemeOption[] _themeOptions = [new(WpfTheme.Dark), new(WpfTheme.Light)];
    private readonly ScanProfileOption[] _scanProfileOptions =
    [
        new(ScanProfile.Balanced, "Balanced", "custom signatures and bounded XVD/XVC nested scan"),
        new(ScanProfile.Fast, "Fast", "built-in signatures only; no nested container scan"),
        new(ScanProfile.Exhaustive, "Exhaustive", "custom signatures and deeper XVD/XVC nested scan")
    ];

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings.Clone();
        _workerOptions = Enumerable.Range(1, Math.Max(1, Environment.ProcessorCount))
            .Select(count => new WorkerOption(count))
            .ToArray();

        ScanProfileCombo.ItemsSource = _scanProfileOptions;
        ScanProfileCombo.SelectedItem = _scanProfileOptions.FirstOrDefault(option => option.Value == _settings.ScanProfile)
            ?? _scanProfileOptions.First(option => option.Value == ScanProfile.Balanced);
        FileCarverIntervalCombo.ItemsSource = IntervalOptions;
        FileCarverIntervalCombo.SelectedItem = IntervalOptions.FirstOrDefault(option => option.Value == _settings.FileCarverInterval)
            ?? IntervalOptions.First(option => option.Value == FileCarverInterval.Sector);
        MetadataIntervalTextBox.Text = Math.Max(1, _settings.MetadataIntervalClusters).ToString();
        MetadataWorkersCombo.ItemsSource = _workerOptions;
        MetadataWorkersCombo.SelectedItem = _workerOptions.FirstOrDefault(option => option.Count == ClampWorkerCount(_settings.MetadataParallelWorkers))
            ?? _workerOptions[0];
        ThemeCombo.ItemsSource = _themeOptions;
        ThemeCombo.SelectedItem = _themeOptions.First(option => option.Name == WpfTheme.NormalizeName(_settings.Theme));
        EnableFileLoggingCheckBox.IsChecked = _settings.EnableFileLogging;
        LogFileTextBox.Text = _settings.LogFile;
        CustomCarversTextBox.Text = AppSettings.NormalizeCustomCarversFile(_settings.CustomCarversFile);
    }

    public AppSettings Result => _settings;

    private void BrowseLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Log files (*.txt;*.log)|*.txt;*.log|All files (*.*)|*.*",
            FileName = LogFileTextBox.Text
        };

        if (dialog.ShowDialog(this) == true)
        {
            LogFileTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseCustomCarvers_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = CustomCarversTextBox.Text
        };

        if (dialog.ShowDialog(this) == true)
        {
            CustomCarversTextBox.Text = dialog.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MetadataIntervalTextBox.Text, out var metadataInterval) || metadataInterval < 1)
        {
            MessageBox.Show(this, "Metadata interval must be a positive whole number.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.FileCarverInterval = FileCarverIntervalCombo.SelectedItem is IntervalOption option
            ? option.Value
            : FileCarverInterval.Sector;
        _settings.ScanProfile = ScanProfileCombo.SelectedItem is ScanProfileOption profileOption
            ? profileOption.Value
            : ScanProfile.Balanced;
        _settings.MetadataIntervalClusters = metadataInterval;
        _settings.MetadataParallelWorkers = MetadataWorkersCombo.SelectedItem is WorkerOption workerOption
            ? ClampWorkerCount(workerOption.Count)
            : ClampWorkerCount(_settings.MetadataParallelWorkers);
        _settings.Theme = ThemeCombo.SelectedItem is ThemeOption themeOption
            ? WpfTheme.NormalizeName(themeOption.Name)
            : WpfTheme.Dark;
        _settings.EnableFileLogging = EnableFileLoggingCheckBox.IsChecked == true;
        _settings.LogFile = LogFileTextBox.Text.Trim();
        _settings.CustomCarversFile = AppSettings.NormalizeCustomCarversFile(CustomCarversTextBox.Text);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static int ClampWorkerCount(int count)
    {
        return Math.Clamp(count, 1, Math.Max(1, Environment.ProcessorCount));
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
                // DragMove can throw if the mouse state changes during the drag.
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
        Close();
    }

    private void ToggleWindowMaximized()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}

public sealed record IntervalOption(string Name, string SizeText, FileCarverInterval Value)
{
    public override string ToString()
    {
        return $"{Name} ({SizeText})";
    }
}

public sealed record ScanProfileOption(ScanProfile Value, string Name, string Detail)
{
    public override string ToString()
    {
        return Name;
    }
}

public sealed record WorkerOption(int Count)
{
    public string Name => Count == 1 ? "1 worker" : $"{Count} workers";

    public string Detail => Count == Environment.ProcessorCount
        ? "uses all logical processors"
        : $"leaves {Environment.ProcessorCount - Count} processor(s) free";

    public override string ToString()
    {
        return Name;
    }
}

public sealed record ThemeOption(string Name)
{
    public override string ToString()
    {
        return Name;
    }
}
