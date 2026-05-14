using FATX.Analyzers;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
        ZeroFillOverwrittenRecoveryClustersCheckBox.IsChecked = _settings.ZeroFillOverwrittenRecoveryClusters;
        EnableFileLoggingCheckBox.IsChecked = _settings.EnableFileLogging;
        LogFileTextBox.Text = _settings.LogFile;
        CustomCarversTextBox.Text = AppSettings.NormalizeCustomCarversFile(_settings.CustomCarversFile);
        ResetShortcutRows(ShortcutCatalog.Normalize(_settings.Shortcuts));
    }

    public AppSettings Result => _settings;

    public ObservableCollection<ShortcutEditorRow> ShortcutRows { get; } = new();

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
        _settings.ZeroFillOverwrittenRecoveryClusters = ZeroFillOverwrittenRecoveryClustersCheckBox.IsChecked == true;
        _settings.EnableFileLogging = EnableFileLoggingCheckBox.IsChecked == true;
        _settings.LogFile = LogFileTextBox.Text.Trim();
        _settings.CustomCarversFile = AppSettings.NormalizeCustomCarversFile(CustomCarversTextBox.Text);
        if (!TrySaveShortcuts())
        {
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

    private void RestoreDefaultShortcuts_Click(object sender, RoutedEventArgs e)
    {
        ResetShortcutRows(ShortcutCatalog.DefaultMap());
    }

    private void ShortcutTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key is Key.Back or Key.Delete)
        {
            textBox.Text = string.Empty;
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        try
        {
            var gesture = new KeyGesture(key, modifiers);
            textBox.Text = ShortcutCatalog.FormatGesture(gesture);
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
        catch (NotSupportedException)
        {
            e.Handled = true;
        }
    }

    private void ResetShortcutRows(System.Collections.Generic.IDictionary<string, string> shortcuts)
    {
        ShortcutRows.Clear();
        foreach (var definition in ShortcutCatalog.All)
        {
            ShortcutRows.Add(new ShortcutEditorRow(
                definition.Id,
                definition.Category,
                definition.Name,
                ShortcutCatalog.GetGestureText(shortcuts, definition.Id)));
        }
    }

    private bool TrySaveShortcuts()
    {
        foreach (var row in ShortcutRows)
        {
            if (!ShortcutCatalog.TryParseGesture(row.Gesture, out var gesture))
            {
                MessageBox.Show(this, $"Shortcut '{row.Gesture}' for '{row.Name}' is not valid.", "Settings",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            row.Gesture = gesture == null ? string.Empty : ShortcutCatalog.FormatGesture(gesture);
        }

        var duplicates = ShortcutRows
            .Where(row => !string.IsNullOrWhiteSpace(row.Gesture))
            .GroupBy(row => row.Gesture.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates != null)
        {
            MessageBox.Show(this, $"Shortcut '{duplicates.Key}' is assigned to more than one action.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _settings.Shortcuts = ShortcutRows.ToDictionary(row => row.Id, row => row.Gesture.Trim(), StringComparer.OrdinalIgnoreCase);
        return true;
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

public sealed class ShortcutEditorRow : INotifyPropertyChanged
{
    private string _gesture;

    public ShortcutEditorRow(string id, string category, string name, string gesture)
    {
        Id = id;
        Category = category;
        Name = name;
        _gesture = gesture;
    }

    public string Id { get; }

    public string Category { get; }

    public string Name { get; }

    public string Gesture
    {
        get => _gesture;
        set
        {
            if (string.Equals(_gesture, value, StringComparison.Ordinal))
            {
                return;
            }

            _gesture = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
