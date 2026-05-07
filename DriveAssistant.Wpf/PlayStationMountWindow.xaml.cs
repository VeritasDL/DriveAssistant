using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FATXTools.Wpf;

public partial class PlayStationMountWindow : Window, INotifyPropertyChanged
{
    private static readonly Regex PartitionLinePattern = new(
        @"^(?<name>\S+)\s+(?<start>[0-9a-fA-F]+)\s+(?<end>[0-9a-fA-F]+)\s+(?<length>[0-9a-fA-F]+)\s*$",
        RegexOptions.Compiled);

    private PlayStationPartitionRow? _selectedPartition;
    private PlayStationFileRow? _selectedFile;
    private string _statusText = "Choose a PS3/PS4 HDD image and key file. Native bridge is used when available.";
    private bool _isBridgeProgressVisible;
    private bool _isBridgeProgressIndeterminate;
    private double _bridgeProgressValue;
    private string _bridgeProgressText = "Idle";
    private bool _wasMounted;

    public PlayStationMountWindow(AppSettings settings, string? imagePath = null)
    {
        InitializeComponent();
        DataContext = this;
        MountToolPathTextBox.Text = ResolveMountToolPath(settings.PlayStationMountToolPath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            ImagePathTextBox.Text = imagePath;
        }

        StatusText = PlayStationNativeBridge.IsAvailable
            ? "Native PS HDD bridge loaded. Choose an image and key file."
            : "Native PS HDD bridge unavailable. Choose an image, key file, and mount-tool.exe fallback.";
        PlayStationNativeBridge.ProgressChanged += OnBridgeProgressChanged;
        Closed += (_, _) => PlayStationNativeBridge.ProgressChanged -= OnBridgeProgressChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PlayStationPartitionRow> Partitions { get; } = new();

    public ObservableCollection<PlayStationTreeNode> RootNodes { get; } = new();

    public ObservableCollection<PlayStationFileRow> FileRows { get; } = new();

    public PlayStationPartitionRow? SelectedPartition
    {
        get => _selectedPartition;
        set => SetField(ref _selectedPartition, value);
    }

    public PlayStationFileRow? SelectedFile
    {
        get => _selectedFile;
        set => SetField(ref _selectedFile, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public bool IsBridgeProgressVisible
    {
        get => _isBridgeProgressVisible;
        set => SetField(ref _isBridgeProgressVisible, value);
    }

    public bool IsBridgeProgressIndeterminate
    {
        get => _isBridgeProgressIndeterminate;
        set => SetField(ref _isBridgeProgressIndeterminate, value);
    }

    public double BridgeProgressValue
    {
        get => _bridgeProgressValue;
        set => SetField(ref _bridgeProgressValue, value);
    }

    public string BridgeProgressText
    {
        get => _bridgeProgressText;
        set => SetField(ref _bridgeProgressText, value);
    }

    public string ImagePath => ImagePathTextBox.Text.Trim();

    public string MountToolPath => MountToolPathTextBox.Text.Trim();

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Disk Images (*.img;*.bin;*.raw;*.zip)|*.img;*.bin;*.raw;*.zip|All files (*.*)|*.*",
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
            Filter = "Key files (*.bin;*.key;*.dat)|*.bin;*.key;*.dat|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            KeyPathTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseMountTool_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PS-HDD-Tools mount-tool (mount-tool.exe)|mount-tool.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            FileName = string.IsNullOrWhiteSpace(MountToolPathTextBox.Text) ? "mount-tool.exe" : MountToolPathTextBox.Text
        };

        if (dialog.ShowDialog(this) == true)
        {
            MountToolPathTextBox.Text = dialog.FileName;
        }
    }

    private void BeginBridgeProgress(string text)
    {
        IsBridgeProgressVisible = true;
        IsBridgeProgressIndeterminate = true;
        BridgeProgressValue = 0;
        BridgeProgressText = text;
    }

    private void CompleteBridgeProgress(string text)
    {
        IsBridgeProgressVisible = true;
        IsBridgeProgressIndeterminate = false;
        BridgeProgressValue = 100;
        BridgeProgressText = text;
    }

    private void OnBridgeProgressChanged(PlayStationBridgeProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnBridgeProgressChanged(progress));
            return;
        }

        IsBridgeProgressVisible = true;
        IsBridgeProgressIndeterminate = progress.Percent < 0;
        if (progress.Percent >= 0)
        {
            BridgeProgressValue = Math.Clamp(progress.Percent, 0, 100);
        }

        BridgeProgressText = string.IsNullOrWhiteSpace(progress.Message)
            ? progress.Stage
            : $"{progress.Stage}: {progress.Message}";
    }

    private async void ListPartitions_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(requirePartition: false))
        {
            return;
        }

        try
        {
            BeginBridgeProgress("Listing PlayStation partitions...");
            StatusText = "Listing PlayStation HDD partitions...";
            var result = await ListPlayStationPartitionsAsync();
            var partitions = ParsePartitions(result.Output);

            Partitions.Clear();
            RootNodes.Clear();
            FileRows.Clear();
            SelectedFile = null;
            foreach (var partition in partitions)
            {
                Partitions.Add(partition);
            }

            SelectedPartition = Partitions.FirstOrDefault();
            StatusText = Partitions.Count == 0
                ? "No partitions were reported by PS-HDD-Tools."
                : $"Found {Partitions.Count} PlayStation partition(s).";
            CompleteBridgeProgress("Partition list loaded.");
        }
        catch (Exception ex)
        {
            StatusText = "Failed to list PlayStation partitions.";
            MessageBox.Show(this, ex.Message, "PlayStation HDD", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MountSelected_Click(object sender, RoutedEventArgs e)
    {
        await MountSelectedPartitionAsync();
    }

    private async void ExportPartition_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(requirePartition: true) || SelectedPartition == null)
        {
            return;
        }

        if (!PlayStationNativeBridge.IsAvailable)
        {
            StatusText = "Native PS HDD bridge is required to export decrypted partitions.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Decrypted partition image (*.img)|*.img|All files (*.*)|*.*",
            FileName = $"{SelectedPartition.Name}.decrypted.img",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            BeginBridgeProgress($"Exporting decrypted {SelectedPartition.Name}...");
            StatusText = $"Exporting decrypted {SelectedPartition.Name}...";
            var message = await Task.Run(() => PlayStationNativeBridge.DecryptPartition(
                ImagePathTextBox.Text.Trim(),
                KeyPathTextBox.Text.Trim(),
                SelectedPartition.Name,
                dialog.FileName));
            StatusText = message;
            CompleteBridgeProgress(message);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to export {SelectedPartition.Name}.";
            MessageBox.Show(this, ex.Message, "PlayStation HDD", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportFile_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(requirePartition: true) || SelectedPartition == null)
        {
            return;
        }

        if (SelectedFile == null)
        {
            StatusText = "Select a native file row to export.";
            return;
        }

        if (SelectedFile.Type.Equals("Directory", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "Select a file, not a directory.";
            return;
        }

        if (!PlayStationNativeBridge.IsAvailable)
        {
            StatusText = "Native PS HDD bridge is required to export files.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "All files (*.*)|*.*",
            FileName = string.IsNullOrWhiteSpace(SelectedFile.Name) ? "ps-file.bin" : SelectedFile.Name,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            BeginBridgeProgress($"Exporting {SelectedFile.Path}...");
            StatusText = $"Exporting {SelectedFile.Path}...";
            var message = await Task.Run(() => PlayStationNativeBridge.ExportFile(
                ImagePathTextBox.Text.Trim(),
                KeyPathTextBox.Text.Trim(),
                SelectedPartition.Name,
                SelectedFile.Path,
                dialog.FileName));
            StatusText = message;
            CompleteBridgeProgress(message);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to export {SelectedFile.Path}.";
            MessageBox.Show(this, ex.Message, "PlayStation HDD", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RecoverFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(requirePartition: true) || SelectedPartition == null)
        {
            return;
        }

        if (!PlayStationNativeBridge.IsAvailable)
        {
            StatusText = "Native PS HDD bridge is required to recover PlayStation files.";
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Choose PlayStation recovery output folder",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            BeginBridgeProgress($"Recovering files from {SelectedPartition.Name}...");
            StatusText = $"Recovering files from {SelectedPartition.Name}...";
            var json = await Task.Run(() => PlayStationNativeBridge.RecoverFilesJson(
                ImagePathTextBox.Text.Trim(),
                KeyPathTextBox.Text.Trim(),
                SelectedPartition.Name,
                dialog.FolderName));
            var summary = PlayStationRecoverySummary.Parse(json);
            StatusText = summary.ToStatusText();
            CompleteBridgeProgress(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to recover files from {SelectedPartition.Name}.";
            MessageBox.Show(this, ex.Message, "PlayStation HDD Recovery", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void PartitionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await MountSelectedPartitionAsync();
    }

    private async Task MountSelectedPartitionAsync()
    {
        if (!ValidateInputs(requirePartition: true) || SelectedPartition == null)
        {
            return;
        }

        try
        {
            BeginBridgeProgress($"Mounting {SelectedPartition.Name}...");
            StatusText = $"Mounting {SelectedPartition.Name}...";
            RootNodes.Clear();
            FileRows.Clear();
            SelectedFile = null;

            if (PlayStationNativeBridge.IsAvailable)
            {
                var json = await Task.Run(() => PlayStationNativeBridge.ListFilesJson(
                    ImagePathTextBox.Text.Trim(),
                    KeyPathTextBox.Text.Trim(),
                    SelectedPartition.Name));

                var fileRows = ParseNativeFileRows(json);
                foreach (var row in fileRows)
                {
                    FileRows.Add(row);
                }

                foreach (var node in BuildTreeFromRows(fileRows))
                {
                    RootNodes.Add(node);
                }
            }
            else
            {
                var result = await DisplayPlayStationPartitionAsync(SelectedPartition.Name);
                foreach (var node in ParseTree(result.Output))
                {
                    RootNodes.Add(node);
                }
            }

            StatusText = $"Mounted {SelectedPartition.Name}: {RootNodes.Count:N0} root item(s), {FileRows.Count:N0} indexed entries.";
            CompleteBridgeProgress(StatusText);
            _wasMounted = true;
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to mount {SelectedPartition.Name}.";
            MessageBox.Show(this, ex.Message, "PlayStation HDD", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool ValidateInputs(bool requirePartition)
    {
        if (!File.Exists(ImagePathTextBox.Text.Trim()))
        {
            StatusText = "Select a PlayStation HDD image.";
            return false;
        }

        if (!File.Exists(KeyPathTextBox.Text.Trim()))
        {
            StatusText = "Select the required PS3 EID root key or PS4 EAP HDD key file.";
            return false;
        }

        if (!PlayStationNativeBridge.IsAvailable && !File.Exists(MountToolPathTextBox.Text.Trim()))
        {
            StatusText = "Native PS HDD bridge is unavailable; select PS-HDD-Tools mount-tool.exe fallback.";
            return false;
        }

        if (requirePartition && SelectedPartition == null)
        {
            StatusText = "Select a partition to mount.";
            return false;
        }

        return true;
    }

    private Task<PlayStationMountToolResult> ListPlayStationPartitionsAsync()
    {
        if (PlayStationNativeBridge.IsAvailable)
        {
            return Task.Run(() => new PlayStationMountToolResult(
                PlayStationNativeBridge.ListPartitions(ImagePathTextBox.Text.Trim(), KeyPathTextBox.Text.Trim()),
                string.Empty));
        }

        return PlayStationMountTool.RunAsync(MountToolPath, "list", ImagePathTextBox.Text.Trim(), KeyPathTextBox.Text.Trim());
    }

    private Task<PlayStationMountToolResult> DisplayPlayStationPartitionAsync(string partitionName)
    {
        if (PlayStationNativeBridge.IsAvailable)
        {
            return Task.Run(() => new PlayStationMountToolResult(
                PlayStationNativeBridge.DisplayPartition(ImagePathTextBox.Text.Trim(), KeyPathTextBox.Text.Trim(), partitionName),
                string.Empty));
        }

        return PlayStationMountTool.RunAsync(
            MountToolPath,
            "display",
            ImagePathTextBox.Text.Trim(),
            KeyPathTextBox.Text.Trim(),
            partitionName);
    }

    private static List<PlayStationPartitionRow> ParsePartitions(string output)
    {
        var rows = new List<PlayStationPartitionRow>();
        foreach (var line in ReadMeaningfulLines(output))
        {
            var match = PartitionLinePattern.Match(line);
            if (!match.Success ||
                line.StartsWith("Partition", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add(new PlayStationPartitionRow(
                match.Groups["name"].Value,
                match.Groups["start"].Value,
                match.Groups["end"].Value,
                match.Groups["length"].Value));
        }

        return rows;
    }

    private static List<PlayStationTreeNode> ParseTree(string output)
    {
        var roots = new List<PlayStationTreeNode>();
        var stack = new Stack<(int Indent, PlayStationTreeNode Node)>();

        foreach (var rawLine in ReadMeaningfulLines(output))
        {
            if (PartitionLinePattern.IsMatch(rawLine) ||
                rawLine.Contains("Successfully", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var indent = rawLine.TakeWhile(char.IsWhiteSpace).Count();
            var name = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var node = new PlayStationTreeNode(name);
            while (stack.Count > 0 && stack.Peek().Indent >= indent)
            {
                stack.Pop();
            }

            if (stack.Count == 0)
            {
                roots.Add(node);
            }
            else
            {
                stack.Peek().Node.Children.Add(node);
            }

            stack.Push((indent, node));
        }

        return roots;
    }

    private static List<PlayStationFileRow> ParseNativeFileRows(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rows = new List<PlayStationFileRow>();
        if (!document.RootElement.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var file in files.EnumerateArray())
        {
            rows.Add(new PlayStationFileRow(
                GetJsonString(file, "path"),
                GetJsonString(file, "name"),
                GetJsonString(file, "type"),
                GetJsonUInt64(file, "size"),
                GetJsonString(file, "created"),
                GetJsonString(file, "modified"),
                GetJsonString(file, "accessed"),
                GetJsonInt32(file, "dataOffsetCount"),
                GetJsonInt32(file, "dataRunCount"),
                GetJsonUInt64(file, "largestRunBytes"),
                GetJsonUInt64(file, "estimatedAllocationUnit"),
                GetJsonString(file, "fragmentationStatus"),
                GetJsonString(file, "dataRanges"),
                GetJsonString(file, "dataOffsets"),
                GetJsonString(file, "inodeOffsets"),
                GetJsonString(file, "direntOffsets"),
                GetJsonString(file, "blocktableOffsets")));
        }

        return rows;
    }

    private static List<PlayStationTreeNode> BuildTreeFromRows(IEnumerable<PlayStationFileRow> rows)
    {
        var roots = new List<PlayStationTreeNode>();
        foreach (var row in rows.OrderBy(row => row.Path, StringComparer.OrdinalIgnoreCase))
        {
            var parts = row.Path
                .Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            IList<PlayStationTreeNode> level = roots;
            PlayStationTreeNode? current = null;
            foreach (var part in parts)
            {
                current = level.FirstOrDefault(node => string.Equals(node.Name, part, StringComparison.OrdinalIgnoreCase));
                if (current == null)
                {
                    current = new PlayStationTreeNode(part);
                    level.Add(current);
                }

                level = current.Children;
            }
        }

        return roots;
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetJsonInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    private static ulong GetJsonUInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetUInt64(out var number)
            ? number
            : 0;
    }

    private static IEnumerable<string> ReadMeaningfulLines(string output)
    {
        return output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line) &&
                           !line.StartsWith("[", StringComparison.Ordinal));
    }

    private static string? ResolveMountToolPath(string configuredPath)
    {
        var candidates = new[]
        {
            configuredPath,
            Environment.GetEnvironmentVariable("PS_HDD_TOOLS_MOUNT_TOOL") ?? string.Empty,
            Path.Combine(AppContext.BaseDirectory, "mount-tool.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ps-hdd", "mount-tool.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ps-hdd", "mount-tool.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "Files", "ps-hdd-tools-src", "bin", "Release", "mount-tool.exe")
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = _wasMounted;
        Close();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record PlayStationPartitionRow(string Name, string Start, string End, string Length)
{
    public string StartText => $"0x{Start}";

    public string EndText => $"0x{End}";

    public string LengthText => $"0x{Length}";
}

public sealed class PlayStationTreeNode
{
    public PlayStationTreeNode(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public ObservableCollection<PlayStationTreeNode> Children { get; } = new();

    public string Glyph => Children.Count > 0 ? "\uE8B7" : "\uE8A5";

    public Brush GlyphColor => Children.Count > 0
        ? new SolidColorBrush(Color.FromRgb(115, 183, 255))
        : new SolidColorBrush(Color.FromRgb(202, 214, 232));
}

public sealed record PlayStationFileRow(
    string Path,
    string Name,
    string Type,
    ulong Size,
    string Created,
    string Modified,
    string Accessed,
    int DataOffsetCount,
    int DataRunCount,
    ulong LargestRunBytes,
    ulong EstimatedAllocationUnit,
    string FragmentationStatus,
    string DataRanges,
    string DataOffsets,
    string InodeOffsets,
    string DirentOffsets,
    string BlocktableOffsets)
{
    public string SizeText => Type.Equals("Directory", StringComparison.OrdinalIgnoreCase)
        ? string.Empty
        : $"{Size:N0}";

    public string RunsText => DataRunCount <= 0 ? string.Empty : DataRunCount.ToString("N0");

    public string OffsetCountText => DataOffsetCount <= 0 ? string.Empty : DataOffsetCount.ToString("N0");

    public string LargestRunText => LargestRunBytes <= 0 ? string.Empty : $"{LargestRunBytes:N0}";

    public string RecoveryDisplayStatus
    {
        get
        {
            if (Type.Equals("Directory", StringComparison.OrdinalIgnoreCase))
            {
                return "Directory";
            }

            if (Size == 0)
            {
                return "Empty";
            }

            if (DataOffsetCount <= 0)
            {
                return "Unrecoverable";
            }

            return FragmentationStatus;
        }
    }

    public string FragmentationTooltip => string.Join(Environment.NewLine, new[]
    {
        $"Recovery: {RecoveryDisplayStatus}",
        $"Fragmentation: {FragmentationStatus}",
        EstimatedAllocationUnit > 0 ? $"Estimated allocation unit: {EstimatedAllocationUnit:N0} bytes" : string.Empty,
        DataOffsetCount > 0 ? $"Data offsets: {DataOffsetCount:N0}" : string.Empty,
        DataRunCount > 0 ? $"Estimated runs: {DataRunCount:N0}" : string.Empty,
        LargestRunBytes > 0 ? $"Largest run: {LargestRunBytes:N0} bytes" : string.Empty,
        !string.IsNullOrWhiteSpace(DataRanges) ? $"Ranges: {DataRanges}" : string.Empty,
        !string.IsNullOrWhiteSpace(InodeOffsets) ? $"Inode: {InodeOffsets}" : string.Empty,
        !string.IsNullOrWhiteSpace(DirentOffsets) ? $"Dirent: {DirentOffsets}" : string.Empty,
        !string.IsNullOrWhiteSpace(BlocktableOffsets) ? $"Block tables: {BlocktableOffsets}" : string.Empty
    }.Where(text => !string.IsNullOrWhiteSpace(text)));

    public Brush FragmentationBrush
    {
        get
        {
            var color = FragmentationStatus switch
            {
                "Contiguous" => Color.FromArgb(32, 55, 185, 110),
                "Fragmented" => Color.FromArgb(40, 245, 180, 70),
                "Fully fragmented" => Color.FromArgb(46, 245, 90, 90),
                _ => Color.FromArgb(0, 0, 0, 0)
            };

            return new SolidColorBrush(color);
        }
    }
}

public sealed record PlayStationRecoverySummary(
    string Partition,
    string OutputRoot,
    int TotalFiles,
    int RecoveredFiles,
    int SkippedNoOffsets,
    int FailedFiles,
    int ContiguousFiles,
    int FragmentedFiles,
    int FullyFragmentedFiles,
    ulong BytesWritten)
{
    public static PlayStationRecoverySummary Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new PlayStationRecoverySummary(
            GetString(root, "partition"),
            GetString(root, "outputRoot"),
            GetInt32(root, "totalFiles"),
            GetInt32(root, "recoveredFiles"),
            GetInt32(root, "skippedNoOffsets"),
            GetInt32(root, "failedFiles"),
            GetInt32(root, "contiguousFiles"),
            GetInt32(root, "fragmentedFiles"),
            GetInt32(root, "fullyFragmentedFiles"),
            GetUInt64(root, "bytesWritten"));
    }

    public string ToStatusText()
    {
        return $"Recovered {RecoveredFiles:N0}/{TotalFiles:N0} files from {Partition} ({BytesWritten:N0} bytes). " +
               $"Fragmentation: {ContiguousFiles:N0} contiguous, {FragmentedFiles:N0} fragmented, {FullyFragmentedFiles:N0} fully fragmented. " +
               $"Unrecoverable {SkippedNoOffsets:N0}, failed {FailedFiles:N0}.";
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    private static ulong GetUInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetUInt64(out var number)
            ? number
            : 0;
    }
}

public sealed record PlayStationBridgeProgress(int Percent, string Stage, string Message);

internal static class PlayStationNativeBridge
{
    private const int Success = 0;
    private const int BufferTooSmall = 3;
    private const string BridgeFileName = "pshdd-bridge.dll";
    private const int MaxBridgeResponseBytes = 64 * 1024 * 1024;
    private const int MaxVirtualReadBytes = 16 * 1024 * 1024;

    private static readonly object LoadGate = new();
    private static bool _loadAttempted;
    private static IntPtr _libraryHandle;
    private static ListPartitionsDelegate? _listPartitions;
    private static DisplayPartitionDelegate? _displayPartition;
    private static ListFilesJsonDelegate? _listFilesJson;
    private static DecryptPartitionDelegate? _decryptPartition;
    private static ExportFileDelegate? _exportFile;
    private static RecoverFilesJsonDelegate? _recoverFilesJson;
    private static ListDeletedInodesJsonDelegate? _listDeletedInodesJson;
    private static ExportDeletedInodeDelegate? _exportDeletedInode;
    private static ListPartitionsVirtualDelegate? _listPartitionsVirtual;
    private static DisplayPartitionVirtualDelegate? _displayPartitionVirtual;
    private static ListFilesJsonVirtualDelegate? _listFilesJsonVirtual;
    private static DecryptPartitionVirtualDelegate? _decryptPartitionVirtual;
    private static ExportFileVirtualDelegate? _exportFileVirtual;
    private static RecoverFilesJsonVirtualDelegate? _recoverFilesJsonVirtual;
    private static ListDeletedInodesJsonVirtualDelegate? _listDeletedInodesJsonVirtual;
    private static ExportDeletedInodeVirtualDelegate? _exportDeletedInodeVirtual;
    private static SetProgressCallbackDelegate? _setProgressCallback;
    private static readonly ProgressCallbackDelegate ProgressCallback = OnNativeProgress;
    private static readonly VirtualDiskReadDelegate VirtualDiskRead = OnVirtualDiskRead;
    private static readonly VirtualDiskLengthDelegate VirtualDiskLength = OnVirtualDiskLength;
    private static Exception? _loadError;

    public static event Action<PlayStationBridgeProgress>? ProgressChanged;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ListPartitionsDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imagePath,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DisplayPartitionDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imagePath,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ListFilesJsonDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imagePath,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DecryptPartitionDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imagePath,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        [MarshalAs(UnmanagedType.LPWStr)] string outputPath,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExportFileDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imagePath,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        [MarshalAs(UnmanagedType.LPWStr)] string filePath,
        [MarshalAs(UnmanagedType.LPWStr)] string outputPath,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RecoverFilesJsonDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imagePath,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        [MarshalAs(UnmanagedType.LPWStr)] string outputDirectory,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ListDeletedInodesJsonDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imagePath,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExportDeletedInodeDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imagePath,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        uint inodeNumber,
        [MarshalAs(UnmanagedType.LPWStr)] string outputPath,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong VirtualDiskReadDelegate(
        IntPtr context,
        ulong offset,
        IntPtr data,
        uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong VirtualDiskLengthDelegate(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ListPartitionsVirtualDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imageLabel,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        IntPtr context,
        VirtualDiskReadDelegate readCallback,
        VirtualDiskLengthDelegate lengthCallback,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DisplayPartitionVirtualDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imageLabel,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        IntPtr context,
        VirtualDiskReadDelegate readCallback,
        VirtualDiskLengthDelegate lengthCallback,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ListFilesJsonVirtualDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imageLabel,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        IntPtr context,
        VirtualDiskReadDelegate readCallback,
        VirtualDiskLengthDelegate lengthCallback,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DecryptPartitionVirtualDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imageLabel,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        IntPtr context,
        VirtualDiskReadDelegate readCallback,
        VirtualDiskLengthDelegate lengthCallback,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        [MarshalAs(UnmanagedType.LPWStr)] string outputPath,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExportFileVirtualDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imageLabel,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        IntPtr context,
        VirtualDiskReadDelegate readCallback,
        VirtualDiskLengthDelegate lengthCallback,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        [MarshalAs(UnmanagedType.LPWStr)] string filePath,
        [MarshalAs(UnmanagedType.LPWStr)] string outputPath,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RecoverFilesJsonVirtualDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imageLabel,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        IntPtr context,
        VirtualDiskReadDelegate readCallback,
        VirtualDiskLengthDelegate lengthCallback,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        [MarshalAs(UnmanagedType.LPWStr)] string outputDirectory,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ListDeletedInodesJsonVirtualDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imageLabel,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        IntPtr context,
        VirtualDiskReadDelegate readCallback,
        VirtualDiskLengthDelegate lengthCallback,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExportDeletedInodeVirtualDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string imageLabel,
        [MarshalAs(UnmanagedType.LPWStr)] string keyPath,
        IntPtr context,
        VirtualDiskReadDelegate readCallback,
        VirtualDiskLengthDelegate lengthCallback,
        [MarshalAs(UnmanagedType.LPWStr)] string partitionName,
        uint inodeNumber,
        [MarshalAs(UnmanagedType.LPWStr)] string outputPath,
        [Out] byte[] output,
        int outputLength,
        out int requiredBytes);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ProgressCallbackDelegate(
        int percent,
        [MarshalAs(UnmanagedType.LPStr)] string stage,
        [MarshalAs(UnmanagedType.LPStr)] string message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetProgressCallbackDelegate(ProgressCallbackDelegate callback);

    private delegate int BridgeCall(byte[] output, int outputLength, out int requiredBytes);

    public static bool IsAvailable
    {
        get
        {
            TryEnsureLoaded();
            return _libraryHandle != IntPtr.Zero &&
                   _listPartitions != null &&
                   _displayPartition != null &&
                   _listFilesJson != null &&
                   _decryptPartition != null &&
                   _exportFile != null &&
                   _recoverFilesJson != null &&
                   _listDeletedInodesJson != null &&
                   _exportDeletedInode != null &&
                   _listPartitionsVirtual != null &&
                   _displayPartitionVirtual != null &&
                   _listFilesJsonVirtual != null &&
                   _decryptPartitionVirtual != null &&
                   _exportFileVirtual != null &&
                   _recoverFilesJsonVirtual != null &&
                   _listDeletedInodesJsonVirtual != null &&
                   _exportDeletedInodeVirtual != null &&
                   _setProgressCallback != null;
        }
    }

    public static string ListPartitions(string imagePath, string keyPath)
    {
        EnsureLoaded();
        return InvokeImageBridge(
            imagePath,
            (byte[] buffer, int length, out int requiredBytes) =>
                _listPartitions!(imagePath, keyPath, buffer, length, out requiredBytes),
            (IntPtr disk, byte[] buffer, int length, out int requiredBytes) =>
                _listPartitionsVirtual!(Path.GetFileName(imagePath), keyPath, disk, VirtualDiskRead, VirtualDiskLength, buffer, length, out requiredBytes));
    }

    public static string DisplayPartition(string imagePath, string keyPath, string partitionName)
    {
        EnsureLoaded();
        return InvokeImageBridge(
            imagePath,
            (byte[] buffer, int length, out int requiredBytes) =>
                _displayPartition!(imagePath, keyPath, partitionName, buffer, length, out requiredBytes),
            (IntPtr disk, byte[] buffer, int length, out int requiredBytes) =>
                _displayPartitionVirtual!(Path.GetFileName(imagePath), keyPath, disk, VirtualDiskRead, VirtualDiskLength, partitionName, buffer, length, out requiredBytes));
    }

    public static string ListFilesJson(string imagePath, string keyPath, string partitionName)
    {
        EnsureLoaded();
        return InvokeImageBridge(
            imagePath,
            (byte[] buffer, int length, out int requiredBytes) =>
                _listFilesJson!(imagePath, keyPath, partitionName, buffer, length, out requiredBytes),
            (IntPtr disk, byte[] buffer, int length, out int requiredBytes) =>
                _listFilesJsonVirtual!(Path.GetFileName(imagePath), keyPath, disk, VirtualDiskRead, VirtualDiskLength, partitionName, buffer, length, out requiredBytes));
    }

    public static string DecryptPartition(string imagePath, string keyPath, string partitionName, string outputPath)
    {
        EnsureLoaded();
        return InvokeImageBridge(
            imagePath,
            (byte[] buffer, int length, out int requiredBytes) =>
                _decryptPartition!(imagePath, keyPath, partitionName, outputPath, buffer, length, out requiredBytes),
            (IntPtr disk, byte[] buffer, int length, out int requiredBytes) =>
                _decryptPartitionVirtual!(Path.GetFileName(imagePath), keyPath, disk, VirtualDiskRead, VirtualDiskLength, partitionName, outputPath, buffer, length, out requiredBytes));
    }

    public static string ExportFile(string imagePath, string keyPath, string partitionName, string filePath, string outputPath)
    {
        EnsureLoaded();
        return InvokeImageBridge(
            imagePath,
            (byte[] buffer, int length, out int requiredBytes) =>
                _exportFile!(imagePath, keyPath, partitionName, filePath, outputPath, buffer, length, out requiredBytes),
            (IntPtr disk, byte[] buffer, int length, out int requiredBytes) =>
                _exportFileVirtual!(Path.GetFileName(imagePath), keyPath, disk, VirtualDiskRead, VirtualDiskLength, partitionName, filePath, outputPath, buffer, length, out requiredBytes));
    }

    public static string RecoverFilesJson(string imagePath, string keyPath, string partitionName, string outputDirectory)
    {
        EnsureLoaded();
        return InvokeImageBridge(
            imagePath,
            (byte[] buffer, int length, out int requiredBytes) =>
                _recoverFilesJson!(imagePath, keyPath, partitionName, outputDirectory, buffer, length, out requiredBytes),
            (IntPtr disk, byte[] buffer, int length, out int requiredBytes) =>
                _recoverFilesJsonVirtual!(Path.GetFileName(imagePath), keyPath, disk, VirtualDiskRead, VirtualDiskLength, partitionName, outputDirectory, buffer, length, out requiredBytes));
    }

    public static string ListDeletedInodesJson(string imagePath, string keyPath, string partitionName)
    {
        EnsureLoaded();
        return InvokeImageBridge(
            imagePath,
            (byte[] buffer, int length, out int requiredBytes) =>
                _listDeletedInodesJson!(imagePath, keyPath, partitionName, buffer, length, out requiredBytes),
            (IntPtr disk, byte[] buffer, int length, out int requiredBytes) =>
                _listDeletedInodesJsonVirtual!(Path.GetFileName(imagePath), keyPath, disk, VirtualDiskRead, VirtualDiskLength, partitionName, buffer, length, out requiredBytes));
    }

    public static string ExportDeletedInode(string imagePath, string keyPath, string partitionName, uint inodeNumber, string outputPath)
    {
        EnsureLoaded();
        return InvokeImageBridge(
            imagePath,
            (byte[] buffer, int length, out int requiredBytes) =>
                _exportDeletedInode!(imagePath, keyPath, partitionName, inodeNumber, outputPath, buffer, length, out requiredBytes),
            (IntPtr disk, byte[] buffer, int length, out int requiredBytes) =>
                _exportDeletedInodeVirtual!(Path.GetFileName(imagePath), keyPath, disk, VirtualDiskRead, VirtualDiskLength, partitionName, inodeNumber, outputPath, buffer, length, out requiredBytes));
    }

    private static void EnsureLoaded()
    {
        if (!TryEnsureLoaded())
        {
            throw new InvalidOperationException("Failed to load native PS HDD bridge.", _loadError);
        }
    }

    private static bool TryEnsureLoaded()
    {
        lock (LoadGate)
        {
            if (_loadAttempted)
            {
                return _libraryHandle != IntPtr.Zero;
            }

            _loadAttempted = true;
            try
            {
                var bridgePath = ResolveBridgePath()
                    ?? throw new FileNotFoundException($"Could not find {BridgeFileName}.");

                _libraryHandle = NativeLibrary.Load(
                    bridgePath,
                    typeof(PlayStationNativeBridge).Assembly,
                    DllImportSearchPath.AssemblyDirectory |
                    DllImportSearchPath.SafeDirectories |
                    DllImportSearchPath.UseDllDirectoryForDependencies);
                _listPartitions = Marshal.GetDelegateForFunctionPointer<ListPartitionsDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_list_partitions"));
                _displayPartition = Marshal.GetDelegateForFunctionPointer<DisplayPartitionDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_display_partition"));
                _listFilesJson = Marshal.GetDelegateForFunctionPointer<ListFilesJsonDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_list_files_json"));
                _decryptPartition = Marshal.GetDelegateForFunctionPointer<DecryptPartitionDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_decrypt_partition"));
                _exportFile = Marshal.GetDelegateForFunctionPointer<ExportFileDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_export_file"));
                _recoverFilesJson = Marshal.GetDelegateForFunctionPointer<RecoverFilesJsonDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_recover_files_json"));
                _listDeletedInodesJson = Marshal.GetDelegateForFunctionPointer<ListDeletedInodesJsonDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_list_deleted_inodes_json"));
                _exportDeletedInode = Marshal.GetDelegateForFunctionPointer<ExportDeletedInodeDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_export_deleted_inode"));
                _listPartitionsVirtual = Marshal.GetDelegateForFunctionPointer<ListPartitionsVirtualDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_list_partitions_virtual"));
                _displayPartitionVirtual = Marshal.GetDelegateForFunctionPointer<DisplayPartitionVirtualDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_display_partition_virtual"));
                _listFilesJsonVirtual = Marshal.GetDelegateForFunctionPointer<ListFilesJsonVirtualDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_list_files_json_virtual"));
                _decryptPartitionVirtual = Marshal.GetDelegateForFunctionPointer<DecryptPartitionVirtualDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_decrypt_partition_virtual"));
                _exportFileVirtual = Marshal.GetDelegateForFunctionPointer<ExportFileVirtualDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_export_file_virtual"));
                _recoverFilesJsonVirtual = Marshal.GetDelegateForFunctionPointer<RecoverFilesJsonVirtualDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_recover_files_json_virtual"));
                _listDeletedInodesJsonVirtual = Marshal.GetDelegateForFunctionPointer<ListDeletedInodesJsonVirtualDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_list_deleted_inodes_json_virtual"));
                _exportDeletedInodeVirtual = Marshal.GetDelegateForFunctionPointer<ExportDeletedInodeVirtualDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_export_deleted_inode_virtual"));
                _setProgressCallback = Marshal.GetDelegateForFunctionPointer<SetProgressCallbackDelegate>(
                    NativeLibrary.GetExport(_libraryHandle, "pshdd_set_progress_callback"));
                _setProgressCallback(ProgressCallback);
                return true;
            }
            catch (Exception ex)
            {
                _loadError = ex;
                _libraryHandle = IntPtr.Zero;
                _listPartitions = null;
                _displayPartition = null;
                _listFilesJson = null;
                _decryptPartition = null;
                _exportFile = null;
                _recoverFilesJson = null;
                _listDeletedInodesJson = null;
                _exportDeletedInode = null;
                _listPartitionsVirtual = null;
                _displayPartitionVirtual = null;
                _listFilesJsonVirtual = null;
                _decryptPartitionVirtual = null;
                _exportFileVirtual = null;
                _recoverFilesJsonVirtual = null;
                _listDeletedInodesJsonVirtual = null;
                _exportDeletedInodeVirtual = null;
                _setProgressCallback = null;
                return false;
            }
        }
    }

    private static void OnNativeProgress(int percent, string stage, string message)
    {
        ProgressChanged?.Invoke(new PlayStationBridgeProgress(
            percent,
            stage ?? string.Empty,
            message ?? string.Empty));
    }

    private static string InvokeBridge(BridgeCall call)
    {
        var buffer = new byte[64 * 1024];
        var status = call(buffer, buffer.Length, out var requiredBytes);
        if (status == BufferTooSmall)
        {
            if (requiredBytes <= 0 || requiredBytes > MaxBridgeResponseBytes)
            {
                throw new InvalidOperationException($"Native PS HDD bridge requested an invalid response buffer size: {requiredBytes:N0} bytes.");
            }

            buffer = new byte[Math.Max(requiredBytes, buffer.Length * 2)];
            status = call(buffer, buffer.Length, out requiredBytes);
        }

        var text = DecodeNullTerminatedUtf8(buffer);
        if (status != Success)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(text)
                ? $"Native PS HDD bridge failed with status {status}."
                : text);
        }

        return text;
    }

    private delegate int VirtualBridgeCall(IntPtr diskContext, byte[] output, int outputLength, out int requiredBytes);

    private static string InvokeImageBridge(string imagePath, BridgeCall rawCall, VirtualBridgeCall virtualCall)
    {
        if (!PlayStationArchiveDisk.IsSupported(imagePath))
        {
            return InvokeBridge(rawCall);
        }

        using var disk = PlayStationArchiveDisk.Open(imagePath);
        var handle = GCHandle.Alloc(disk);
        try
        {
            return InvokeBridge((byte[] buffer, int length, out int requiredBytes) =>
                virtualCall(GCHandle.ToIntPtr(handle), buffer, length, out requiredBytes));
        }
        finally
        {
            handle.Free();
        }
    }

    private static ulong OnVirtualDiskRead(IntPtr context, ulong offset, IntPtr data, uint length)
    {
        try
        {
            if (context == IntPtr.Zero || data == IntPtr.Zero || length == 0)
            {
                return 0;
            }

            var disk = (PlayStationArchiveDisk)GCHandle.FromIntPtr(context).Target!;
            var requestLength = length > MaxVirtualReadBytes ? MaxVirtualReadBytes : (int)length;
            var buffer = new byte[requestLength];
            var read = disk.Read(checked((long)offset), buffer, 0, buffer.Length);
            if (read > 0)
            {
                Marshal.Copy(buffer, 0, data, read);
            }

            return (ulong)read;
        }
        catch
        {
            return 0;
        }
    }

    private static ulong OnVirtualDiskLength(IntPtr context)
    {
        try
        {
            return context == IntPtr.Zero
                ? 0
                : (ulong)((PlayStationArchiveDisk)GCHandle.FromIntPtr(context).Target!).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string DecodeNullTerminatedUtf8(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static string? ResolveBridgePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, BridgeFileName),
            Path.Combine(AppContext.BaseDirectory, "tools", "ps-hdd", BridgeFileName),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "ps-hdd", BridgeFileName)
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }
}

internal static class PlayStationMountTool
{
    public static async Task<PlayStationMountToolResult> RunAsync(
        string mountToolPath,
        string command,
        string imagePath,
        string keyPath,
        string? partition = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = mountToolPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(imagePath);
        startInfo.ArgumentList.Add(keyPath);
        if (!string.IsNullOrWhiteSpace(partition))
        {
            startInfo.ArgumentList.Add(partition);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start PS-HDD-Tools mount-tool.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine, new[] { output, error }.Where(text => !string.IsNullOrWhiteSpace(text)));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                ? $"mount-tool exited with code {process.ExitCode}."
                : details);
        }

        return new PlayStationMountToolResult(output, error);
    }
}

internal sealed record PlayStationMountToolResult(string Output, string Error);
