using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FATXTools.Wpf;

public sealed class PlayStationStorageImage : IDisposable
{
    private static readonly Regex PartitionLinePattern = new(
        @"^(?<name>\S+)\s+(?<start>[0-9a-fA-F]+)\s+(?<end>[0-9a-fA-F]+)\s+(?<length>[0-9a-fA-F]+)\s*$",
        RegexOptions.Compiled);

    private PlayStationStorageImage(string imagePath, string keyPath, IReadOnlyList<PlayStationVolume> volumes)
    {
        ImagePath = imagePath;
        KeyPath = keyPath;
        Volumes = volumes;
    }

    public string ImagePath { get; }

    public string KeyPath { get; }

    public IReadOnlyList<PlayStationVolume> Volumes { get; }

    public static PlayStationStorageImage Open(string imagePath, string keyPath)
    {
        if (!PlayStationNativeBridge.IsAvailable)
        {
            throw new InvalidOperationException("The native PlayStation HDD bridge is required for the unified filesystem view.");
        }

        var partitionText = PlayStationNativeBridge.ListPartitions(imagePath, keyPath);
        var volumes = ParsePartitions(partitionText)
            .Select(row => new PlayStationVolume(imagePath, keyPath, row.Name, ParseHexInt64(row.Start), ParseHexInt64(row.Length)))
            .ToList();

        if (volumes.Count == 0)
        {
            throw new InvalidDataException("No PlayStation HDD partitions were found.");
        }

        foreach (var volume in volumes)
        {
            volume.TryLoad();
        }

        if (!volumes.Any(volume => volume.IsLoaded))
        {
            var error = volumes.FirstOrDefault(volume => !string.IsNullOrWhiteSpace(volume.LoadError))?.LoadError;
            throw new InvalidDataException(string.IsNullOrWhiteSpace(error)
                ? "No PlayStation HDD partitions could be indexed."
                : error);
        }

        return new PlayStationStorageImage(imagePath, keyPath, volumes);
    }

    public void Dispose()
    {
    }

    private static List<PlayStationPartitionInfo> ParsePartitions(string output)
    {
        var rows = new List<PlayStationPartitionInfo>();
        foreach (var line in output
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n')
                     .Select(line => line.Trim())
                     .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("[", StringComparison.Ordinal)))
        {
            var match = PartitionLinePattern.Match(line);
            if (!match.Success || line.StartsWith("Partition", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add(new PlayStationPartitionInfo(
                match.Groups["name"].Value,
                match.Groups["start"].Value,
                match.Groups["end"].Value,
                match.Groups["length"].Value));
        }

        return rows;
    }

    private static long ParseHexInt64(string text)
    {
        return long.TryParse(text.Trim().TrimStart('0', 'x', 'X'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private sealed record PlayStationPartitionInfo(string Name, string Start, string End, string Length);
}

public sealed class PlayStationVolume
{
    private readonly string _imagePath;
    private readonly string _keyPath;
    private readonly List<PlayStationFileEntry> _root = [];
    private bool _loadAttempted;

    public PlayStationVolume(string imagePath, string keyPath, string name, long offset, long length)
    {
        _imagePath = imagePath;
        _keyPath = keyPath;
        Name = name;
        Offset = offset;
        Length = length;
    }

    public string Name { get; }

    public long Offset { get; }

    public long Length { get; }

    public bool IsLoaded { get; private set; }

    public string? LoadError { get; private set; }

    public string FamilyText => "PlayStation HDD";

    public long UsedSpace => GetAllEntries().Where(entry => !entry.IsDirectory).Sum(entry => Math.Max(0, entry.Length));

    public long FreeSpace => 0;

    public long TotalSpace => Length;

    public IReadOnlyList<PlayStationFileEntry> GetRoot()
    {
        EnsureLoaded();
        return _root;
    }

    public IReadOnlyList<PlayStationFileEntry> GetChildren(PlayStationFileEntry? directory)
    {
        EnsureLoaded();
        return directory?.Children ?? _root;
    }

    public void CopyFile(PlayStationFileEntry entry, string outputPath)
    {
        if (entry.IsDirectory)
        {
            throw new InvalidOperationException("Select a file, not a directory.");
        }

        if (entry.Length == 0)
        {
            File.Create(outputPath).Dispose();
            return;
        }

        PlayStationNativeBridge.ExportFile(_imagePath, _keyPath, Name, entry.Path, outputPath);
    }

    public string DecryptToFile(string outputPath)
    {
        return PlayStationNativeBridge.DecryptPartition(_imagePath, _keyPath, Name, outputPath);
    }

    public bool TryLoad()
    {
        if (_loadAttempted)
        {
            return IsLoaded;
        }

        _loadAttempted = true;
        try
        {
            var json = PlayStationNativeBridge.ListFilesJson(_imagePath, _keyPath, Name);
            _root.Clear();
            _root.AddRange(BuildEntries(ParseRows(json)));
            IsLoaded = true;
            LoadError = null;
            return true;
        }
        catch (Exception ex)
        {
            IsLoaded = false;
            LoadError = ex.Message;
            return false;
        }
    }

    private void EnsureLoaded()
    {
        if (!TryLoad())
        {
            throw new InvalidOperationException(LoadError ?? $"Failed to index PlayStation partition {Name}.");
        }
    }

    private IEnumerable<PlayStationFileEntry> GetAllEntries()
    {
        EnsureLoaded();
        foreach (var entry in _root)
        {
            foreach (var child in Walk(entry))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<PlayStationFileEntry> Walk(PlayStationFileEntry entry)
    {
        yield return entry;
        foreach (var child in entry.Children)
        {
            foreach (var nested in Walk(child))
            {
                yield return nested;
            }
        }
    }

    private List<PlayStationFileEntry> BuildEntries(IEnumerable<PlayStationFileEntry> parsedEntries)
    {
        var entries = new Dictionary<string, PlayStationFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in parsedEntries)
        {
            entries[entry.Path] = entry;
        }

        foreach (var entry in entries.Values.ToList())
        {
            var parentPath = GetParentPath(entry.Path);
            if (string.IsNullOrEmpty(parentPath) || parentPath == "/")
            {
                continue;
            }

            if (!entries.ContainsKey(parentPath))
            {
                entries[parentPath] = PlayStationFileEntry.CreateDirectory(this, parentPath);
            }
        }

        foreach (var entry in entries.Values)
        {
            entry.Children.Clear();
        }

        var roots = new List<PlayStationFileEntry>();
        foreach (var entry in entries.Values.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
        {
            var parentPath = GetParentPath(entry.Path);
            if (string.IsNullOrEmpty(parentPath) || parentPath == "/" || !entries.TryGetValue(parentPath, out var parent))
            {
                roots.Add(entry);
                continue;
            }

            entry.Parent = parent;
            parent.Children.Add(entry);
        }

        SortChildren(roots);
        return roots;
    }

    private List<PlayStationFileEntry> ParseRows(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rows = new List<PlayStationFileEntry>();
        if (!document.RootElement.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var file in files.EnumerateArray())
        {
            var path = NormalizePath(GetJsonString(file, "path"));
            if (string.IsNullOrWhiteSpace(path) || path == "/")
            {
                continue;
            }

            rows.Add(new PlayStationFileEntry(
                this,
                path,
                GetJsonString(file, "name"),
                GetJsonString(file, "type"),
                (long)Math.Min(GetJsonUInt64(file, "size"), long.MaxValue),
                ParseDate(GetJsonString(file, "created")),
                ParseDate(GetJsonString(file, "modified")),
                ParseDate(GetJsonString(file, "accessed")),
                GetJsonInt32(file, "dataOffsetCount"),
                GetJsonInt32(file, "dataRunCount"),
                (long)Math.Min(GetJsonUInt64(file, "largestRunBytes"), long.MaxValue),
                (long)Math.Min(GetJsonUInt64(file, "estimatedAllocationUnit"), long.MaxValue),
                GetJsonString(file, "fragmentationStatus"),
                GetJsonString(file, "dataRanges"),
                GetJsonString(file, "dataOffsets"),
                GetJsonString(file, "inodeOffsets"),
                GetJsonString(file, "direntOffsets"),
                GetJsonString(file, "blocktableOffsets")));
        }

        return rows;
    }

    private static void SortChildren(List<PlayStationFileEntry> entries)
    {
        entries.Sort((left, right) =>
        {
            var kindCompare = right.IsDirectory.CompareTo(left.IsDirectory);
            return kindCompare != 0
                ? kindCompare
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var entry in entries)
        {
            SortChildren(entry.Children);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        path = path.Replace('\\', '/').Trim();
        return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
    }

    private static string GetParentPath(string path)
    {
        path = NormalizePath(path);
        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
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

    private static DateTime ParseDate(string value)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
            ? date
            : DateTime.MinValue;
    }
}

public sealed class PlayStationFileEntry
{
    private static readonly Regex HexPattern = new(@"(?:0x)?[0-9a-fA-F]+", RegexOptions.Compiled);

    public PlayStationFileEntry(
        PlayStationVolume volume,
        string path,
        string name,
        string type,
        long length,
        DateTime created,
        DateTime modified,
        DateTime accessed,
        int dataOffsetCount,
        int dataRunCount,
        long largestRunBytes,
        long estimatedAllocationUnit,
        string fragmentationStatus,
        string dataRanges,
        string dataOffsets,
        string inodeOffsets,
        string direntOffsets,
        string blocktableOffsets)
    {
        Volume = volume;
        Path = path;
        Name = string.IsNullOrWhiteSpace(name)
            ? path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path
            : name;
        Type = type;
        Length = length;
        Created = created;
        Modified = modified;
        Accessed = accessed;
        DataOffsetCount = dataOffsetCount;
        DataRunCount = dataRunCount;
        LargestRunBytes = largestRunBytes;
        EstimatedAllocationUnit = estimatedAllocationUnit;
        FragmentationStatus = fragmentationStatus;
        DataRanges = dataRanges;
        DataOffsets = dataOffsets;
        InodeOffsets = inodeOffsets;
        DirentOffsets = direntOffsets;
        BlocktableOffsets = blocktableOffsets;
        Offset = ParseFirstHex(dataOffsets) ?? ParseFirstHex(dataRanges) ?? 0;
    }

    public PlayStationVolume Volume { get; }

    public string Path { get; }

    public string Name { get; }

    public string Type { get; }

    public long Length { get; }

    public DateTime Created { get; }

    public DateTime Modified { get; }

    public DateTime Accessed { get; }

    public int DataOffsetCount { get; }

    public int DataRunCount { get; }

    public long LargestRunBytes { get; }

    public long EstimatedAllocationUnit { get; }

    public string FragmentationStatus { get; }

    public string DataRanges { get; }

    public string DataOffsets { get; }

    public string InodeOffsets { get; }

    public string DirentOffsets { get; }

    public string BlocktableOffsets { get; }

    public long Offset { get; }

    public PlayStationFileEntry? Parent { get; set; }

    public List<PlayStationFileEntry> Children { get; } = [];

    public bool IsDirectory => Type.Equals("Directory", StringComparison.OrdinalIgnoreCase);

    public int FolderCount => Children.Count(entry => entry.IsDirectory);

    public int FileCount => Children.Count(entry => !entry.IsDirectory);

    public string ExtentSummary => string.Join(Environment.NewLine, new[]
    {
        RecoveryDisplayStatus,
        EstimatedAllocationUnit > 0 ? $"Estimated allocation unit: {EstimatedAllocationUnit:N0} bytes" : string.Empty,
        DataOffsetCount > 0 ? $"Data offsets: {DataOffsetCount:N0}" : string.Empty,
        DataRunCount > 0 ? $"Estimated runs: {DataRunCount:N0}" : string.Empty,
        LargestRunBytes > 0 ? $"Largest run: {LargestRunBytes:N0} bytes" : string.Empty,
        !string.IsNullOrWhiteSpace(DataRanges) ? $"Ranges: {DataRanges}" : string.Empty,
        !string.IsNullOrWhiteSpace(InodeOffsets) ? $"Inode: {InodeOffsets}" : string.Empty,
        !string.IsNullOrWhiteSpace(DirentOffsets) ? $"Dirent: {DirentOffsets}" : string.Empty,
        !string.IsNullOrWhiteSpace(BlocktableOffsets) ? $"Block tables: {BlocktableOffsets}" : string.Empty
    }.Where(text => !string.IsNullOrWhiteSpace(text)));

    public string RecoveryDisplayStatus
    {
        get
        {
            if (IsDirectory)
            {
                return "Directory";
            }

            if (Length == 0)
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

    public static PlayStationFileEntry CreateDirectory(PlayStationVolume volume, string path)
    {
        return new PlayStationFileEntry(
            volume,
            path,
            path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path,
            "Directory",
            0,
            DateTime.MinValue,
            DateTime.MinValue,
            DateTime.MinValue,
            0,
            0,
            0,
            0,
            "Directory",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
    }

    private static long? ParseFirstHex(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = HexPattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? match.Value[2..]
            : match.Value;
        return long.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
