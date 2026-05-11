using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FATXTools.Wpf;

internal sealed class CdIso9660Volume : GenericFileSystemVolume
{
    private const int LogicalSectorSize = 2048;
    private const int MaxProbeSectors = 512;
    private readonly string _dataPath;
    private readonly int _sectorSize;
    private readonly int _dataOffset;
    private readonly int _logicalSectorBase;
    private readonly List<GenericFileSystemEntry> _root = [];
    private readonly HashSet<long> _activeRecordOffsets = [];

    private CdIso9660Volume(
        string sourcePath,
        string dataPath,
        GenericPartitionCandidate partition,
        string familyText,
        int sectorSize,
        int dataOffset,
        int logicalSectorBase)
        : base(sourcePath, partition, familyText)
    {
        _dataPath = dataPath;
        _sectorSize = sectorSize;
        _dataOffset = dataOffset;
        _logicalSectorBase = logicalSectorBase;
    }

    public override long ClusterSize => LogicalSectorSize;

    public override long UsedSpace => Walk(_root).Where(entry => !entry.IsDirectory).Sum(entry => entry.Length);

    public static bool TryOpen(string sourcePath, out CdIso9660Volume volume, out string status)
    {
        volume = null!;
        status = string.Empty;
        foreach (var candidate in CreateCandidates(sourcePath))
        {
            if (TryOpenCandidate(sourcePath, candidate, out volume, out status))
            {
                return true;
            }
        }

        return false;
    }

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var rows = new List<GenericFileSystemEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stream = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        var sectorCount = stream.Length / Math.Max(1, _sectorSize);
        var sector = new byte[LogicalSectorSize];
        for (var physicalSector = 0L; physicalSector < sectorCount; physicalSector++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logicalSector = checked((int)Math.Min(int.MaxValue, physicalSector + _logicalSectorBase));
            if (!ReadLogicalSector(stream, logicalSector, sector))
            {
                continue;
            }

            foreach (var candidate in ScanDirectoryRecordCandidates(sector, logicalSector))
            {
                if (_activeRecordOffsets.Contains(candidate.RecordOffset) || candidate.Name is "." or "..")
                {
                    continue;
                }

                var key = $"{candidate.Lba:X8}:{candidate.Length:X8}:{candidate.Name}";
                if (!seen.Add(key))
                {
                    continue;
                }

                rows.Add(candidate.IsDirectory
                    ? CreateScannedDirectoryEntry(candidate)
                    : CreateScannedFileEntry(candidate));
            }

            if ((physicalSector & 0xFF) == 0)
            {
                progress?.Report((int)Math.Min(99, physicalSector * 100 / Math.Max(1, sectorCount)));
            }
        }

        progress?.Report(100);
        return rows;
    }

    public override void CopyFile(GenericFileSystemEntry entry, string destinationPath, Action<long>? progress, CancellationToken cancellationToken)
    {
        if (entry.IsDirectory)
        {
            throw new InvalidOperationException("Select a file, not a directory.");
        }

        using var input = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        using var output = File.Create(destinationPath);
        var buffer = new byte[LogicalSectorSize];
        var remaining = entry.Length;
        foreach (var extent in entry.Extents)
        {
            var logicalSector = (int)(extent.Offset / LogicalSectorSize);
            var sectorOffset = (int)(extent.Offset % LogicalSectorSize);
            var extentRemaining = Math.Min(extent.Length, remaining);
            while (extentRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadLogicalSector(input, logicalSector, buffer))
                {
                    return;
                }

                var writable = Math.Min(LogicalSectorSize - sectorOffset, extentRemaining);
                output.Write(buffer, sectorOffset, (int)writable);
                progress?.Invoke(writable);
                remaining -= writable;
                extentRemaining -= writable;
                logicalSector++;
                sectorOffset = 0;
            }
        }
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return cluster * LogicalSectorSize;
    }

    private static bool TryOpenCandidate(string sourcePath, CdTrackCandidate candidate, out CdIso9660Volume volume, out string status)
    {
        volume = null!;
        status = string.Empty;
        if (!File.Exists(candidate.DataPath))
        {
            return false;
        }

        using var stream = new FileStream(candidate.DataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        if (!TryReadPrimaryVolumeDescriptor(stream, candidate, out var pvd, out var physicalPvdSector))
        {
            return false;
        }

        var volumeId = ReadAscii(pvd.AsSpan(40, 32));
        if (string.IsNullOrWhiteSpace(volumeId))
        {
            volumeId = Path.GetFileNameWithoutExtension(candidate.DataPath);
        }

        var rootRecord = pvd.AsSpan(156);
        if (rootRecord.Length == 0 || rootRecord[0] == 0)
        {
            return false;
        }

        var rootLba = BinaryPrimitives.ReadUInt32LittleEndian(rootRecord[2..]);
        var rootLength = BinaryPrimitives.ReadUInt32LittleEndian(rootRecord[10..]);
        var logicalSectorBase = TryFindDirectoryPhysicalSector(stream, candidate, rootLba, out var rootPhysicalSector)
            ? checked((int)rootLba - rootPhysicalSector)
            : physicalPvdSector - 16;
        var familyText = LooksLikePspUmdRoot(stream, candidate, checked((int)rootLba - logicalSectorBase), rootLength)
            ? "Sony PSP UMD ISO9660"
            : candidate.FamilyText;
        var sourceLength = new FileInfo(candidate.DataPath).Length;
        var partition = new GenericPartitionCandidate(0, Guid.Empty, 0, sourceLength, volumeId, LogicalSectorSize);
        volume = new CdIso9660Volume(sourcePath, candidate.DataPath, partition, familyText, candidate.SectorSize, candidate.DataOffset, logicalSectorBase);
        volume.LoadDirectory(stream, (int)rootLba, rootLength, "/", volume._root, new HashSet<int>());
        var trackText = candidate.TrackNumber > 0 ? $" track {candidate.TrackNumber}" : string.Empty;
        status = $"Mounted {familyText}{trackText} ISO9660 volume '{volumeId}', {volume._root.Count:N0} root entries. Data-track file export is available.";
        return volume._root.Count > 0 || rootLength > 0;
    }

    private static bool TryReadPrimaryVolumeDescriptor(FileStream stream, CdTrackCandidate candidate, out byte[] pvd, out int physicalSector)
    {
        pvd = new byte[LogicalSectorSize];
        physicalSector = 0;
        foreach (var sector in ProbeSectors())
        {
            if (!ReadSector(stream, candidate, sector, pvd))
            {
                continue;
            }

            if (pvd[0] == 1 && Encoding.ASCII.GetString(pvd, 1, 5) == "CD001" && pvd[6] == 1)
            {
                physicalSector = sector;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindDirectoryPhysicalSector(FileStream stream, CdTrackCandidate candidate, uint directoryLba, out int physicalSector)
    {
        physicalSector = 0;
        var sector = new byte[LogicalSectorSize];
        for (var index = 0; index < MaxProbeSectors; index++)
        {
            if (!ReadSector(stream, candidate, index, sector))
            {
                continue;
            }

            if (sector[0] < 34 || sector[32] != 1 || sector[33] != 0)
            {
                continue;
            }

            var recordLba = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(2, 4));
            if (recordLba == directoryLba)
            {
                physicalSector = index;
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikePspUmdRoot(FileStream stream, CdTrackCandidate candidate, int rootLba, long rootLength)
    {
        if (rootLength <= 0 || rootLength > 1024 * 1024)
        {
            return false;
        }

        var directory = new byte[rootLength];
        var sector = new byte[LogicalSectorSize];
        var written = 0;
        var logicalSector = rootLba;
        while (written < directory.Length)
        {
            if (!ReadSector(stream, candidate, logicalSector, sector))
            {
                return false;
            }

            var copy = Math.Min(sector.Length, directory.Length - written);
            Buffer.BlockCopy(sector, 0, directory, written, copy);
            written += copy;
            logicalSector++;
        }

        var hasPspGame = false;
        var hasUmdData = false;
        var offset = 0;
        while (offset < directory.Length)
        {
            var recordLength = directory[offset];
            if (recordLength == 0)
            {
                offset = ((offset / LogicalSectorSize) + 1) * LogicalSectorSize;
                continue;
            }

            if (offset + recordLength > directory.Length || recordLength < 34)
            {
                break;
            }

            var record = directory.AsSpan(offset, recordLength);
            offset += recordLength;
            var nameLength = record[32];
            if (33 + nameLength > record.Length)
            {
                continue;
            }

            var name = DecodeFileIdentifier(record.Slice(33, nameLength));
            hasPspGame |= name.Equals("PSP_GAME", StringComparison.OrdinalIgnoreCase);
            hasUmdData |= name.Equals("UMD_DATA.BIN", StringComparison.OrdinalIgnoreCase);
        }

        return hasPspGame && hasUmdData;
    }

    private void LoadDirectory(FileStream stream, int logicalSector, long length, string path, List<GenericFileSystemEntry> rows, HashSet<int> visited)
    {
        if (length <= 0 || !visited.Add(logicalSector))
        {
            return;
        }

        var directory = ReadLogicalBytes(stream, logicalSector, length);
        var offset = 0;
        while (offset < directory.Length)
        {
            var recordLength = directory[offset];
            if (recordLength == 0)
            {
                offset = ((offset / LogicalSectorSize) + 1) * LogicalSectorSize;
                continue;
            }

            if (offset + recordLength > directory.Length || recordLength < 34)
            {
                break;
            }

            var record = directory.AsSpan(offset, recordLength);
            _activeRecordOffsets.Add(logicalSector * (long)LogicalSectorSize + offset);
            offset += recordLength;
            var nameLength = record[32];
            if (33 + nameLength > record.Length)
            {
                continue;
            }

            var name = DecodeFileIdentifier(record.Slice(33, nameLength));
            if (name is "." or "..")
            {
                continue;
            }

            var entryLba = BinaryPrimitives.ReadUInt32LittleEndian(record[2..]);
            var entryLength = BinaryPrimitives.ReadUInt32LittleEndian(record[10..]);
            var flags = record[25];
            var isDirectory = (flags & 0x02) != 0;
            var childPath = CombinePath(path, name);
            if (isDirectory)
            {
                var child = CreateDirectoryEntry(name, childPath, entryLba, entryLength);
                rows.Add(child);
                LoadDirectory(stream, (int)entryLba, entryLength, childPath, child.Children, visited);
                continue;
            }

            rows.Add(CreateFileEntry(name, childPath, entryLba, entryLength));
        }
    }

    private byte[] ReadLogicalBytes(FileStream stream, int logicalSector, long length)
    {
        var data = new byte[length];
        var sector = new byte[LogicalSectorSize];
        var written = 0;
        while (written < data.Length)
        {
            if (!ReadLogicalSector(stream, logicalSector, sector))
            {
                break;
            }

            var copy = Math.Min(sector.Length, data.Length - written);
            Buffer.BlockCopy(sector, 0, data, written, copy);
            written += copy;
            logicalSector++;
        }

        return data;
    }

    private bool ReadLogicalSector(FileStream stream, int logicalSector, byte[] buffer)
    {
        return ReadSector(stream, new CdTrackCandidate(_dataPath, _sectorSize, _dataOffset, 0, FamilyText), logicalSector - _logicalSectorBase, buffer);
    }

    private static bool ReadSector(FileStream stream, CdTrackCandidate candidate, int physicalSector, byte[] buffer)
    {
        if (physicalSector < 0)
        {
            return false;
        }

        var offset = candidate.ByteOffset + physicalSector * (long)candidate.SectorSize + candidate.DataOffset;
        if (offset < 0 || offset + LogicalSectorSize > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        var read = stream.Read(buffer, 0, LogicalSectorSize);
        return read == LogicalSectorSize;
    }

    private GenericFileSystemEntry CreateDirectoryEntry(string name, string path, uint lba, uint length)
    {
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = path,
            Name = name,
            Kind = "Directory",
            IsDirectory = true,
            Length = length,
            Offset = lba * (long)LogicalSectorSize,
            Cluster = lba,
            Attributes = "ISO9660 directory",
            MetadataStatus = "Active ISO9660 directory record",
            Extents = []
        };
    }

    private GenericFileSystemEntry CreateFileEntry(string name, string path, uint lba, uint length)
    {
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = path,
            Name = name,
            Kind = "File",
            IsDirectory = false,
            Length = length,
            Offset = lba * (long)LogicalSectorSize,
            Cluster = lba,
            Attributes = "ISO9660 file",
            MetadataStatus = "Active ISO9660 file record",
            Extents = [new FileExtent(lba * (long)LogicalSectorSize, length)]
        };
    }

    private GenericFileSystemEntry CreateScannedDirectoryEntry(IsoDirectoryRecordCandidate candidate)
    {
        var name = $"orphan_iso_dir_{candidate.Lba:X8}_{SanitizeName(candidate.Name)}";
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = "/$ORPHANED/" + name,
            Name = name,
            Kind = "Directory",
            IsDirectory = true,
            Length = candidate.Length,
            Offset = candidate.RecordOffset,
            Cluster = candidate.Lba,
            IsDeleted = true,
            Attributes = $"ISO9660 flags 0x{candidate.Flags:X2}",
            MetadataStatus = "Unreferenced ISO9660 directory record candidate",
            Extents = []
        };
    }

    private GenericFileSystemEntry CreateScannedFileEntry(IsoDirectoryRecordCandidate candidate)
    {
        var name = $"orphan_iso_file_{candidate.Lba:X8}_{SanitizeName(candidate.Name)}";
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = "/$ORPHANED/" + name,
            Name = name,
            Kind = "File",
            IsDirectory = false,
            Length = candidate.Length,
            Offset = candidate.RecordOffset,
            Cluster = candidate.Lba,
            IsDeleted = true,
            Attributes = $"ISO9660 flags 0x{candidate.Flags:X2}",
            MetadataStatus = "Unreferenced ISO9660 file record candidate",
            Extents = [new FileExtent(candidate.Lba * (long)LogicalSectorSize, candidate.Length)]
        };
    }

    private static IEnumerable<IsoDirectoryRecordCandidate> ScanDirectoryRecordCandidates(byte[] sector, int logicalSector)
    {
        for (var offset = 0; offset + 34 <= sector.Length; offset++)
        {
            var recordLength = sector[offset];
            if (recordLength < 34 || offset + recordLength > sector.Length)
            {
                continue;
            }

            var record = sector.AsSpan(offset, recordLength);
            if (!TryReadDirectoryRecordCandidate(record, logicalSector * (long)LogicalSectorSize + offset, out var candidate))
            {
                continue;
            }

            yield return candidate;
            offset += recordLength - 1;
        }
    }

    private static bool TryReadDirectoryRecordCandidate(ReadOnlySpan<byte> record, long recordOffset, out IsoDirectoryRecordCandidate candidate)
    {
        candidate = default;
        if (record.Length < 34 || record[0] != record.Length)
        {
            return false;
        }

        var lbaLe = BinaryPrimitives.ReadUInt32LittleEndian(record[2..]);
        var lbaBe = BinaryPrimitives.ReadUInt32BigEndian(record[6..]);
        var lengthLe = BinaryPrimitives.ReadUInt32LittleEndian(record[10..]);
        var lengthBe = BinaryPrimitives.ReadUInt32BigEndian(record[14..]);
        if (lbaLe != lbaBe || lengthLe != lengthBe || lengthLe > 512U * 1024U * 1024U)
        {
            return false;
        }

        var flags = record[25];
        var nameLength = record[32];
        if (nameLength == 0 || 33 + nameLength > record.Length || record[28] == 0 || record[31] == 0)
        {
            return false;
        }

        var rawName = record.Slice(33, nameLength);
        if (!IsIsoFileIdentifier(rawName))
        {
            return false;
        }

        var name = DecodeFileIdentifier(rawName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        candidate = new IsoDirectoryRecordCandidate(name, lbaLe, lengthLe, flags, recordOffset, (flags & 0x02) != 0);
        return true;
    }

    private static bool IsIsoFileIdentifier(ReadOnlySpan<byte> name)
    {
        if (name.Length == 1 && name[0] is 0 or 1)
        {
            return true;
        }

        foreach (var value in name)
        {
            if (value < 0x20 || value > 0x7E || value is (byte)'/' or (byte)'\\')
            {
                return false;
            }
        }

        return true;
    }

    private static string SanitizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        }

        return builder.Length == 0 ? "entry" : builder.ToString();
    }

    private static IEnumerable<CdTrackCandidate> CreateCandidates(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension == ".cue")
        {
            foreach (var candidate in CreateCueCandidates(sourcePath))
            {
                yield return candidate;
            }

            yield break;
        }

        if (extension == ".gdi")
        {
            foreach (var candidate in CreateGdiCandidates(sourcePath))
            {
                yield return candidate;
            }

            yield break;
        }

        if (extension is ".iso" or ".bin")
        {
            yield return new CdTrackCandidate(sourcePath, 2048, 0, 0, "CD ISO9660 data track");
            yield return new CdTrackCandidate(sourcePath, 2352, 16, 0, "CD MODE1/2352 ISO9660 data track");
            yield return new CdTrackCandidate(sourcePath, 2352, 24, 0, "CD MODE2/2352 ISO9660 data track");
        }
    }

    private static IEnumerable<CdTrackCandidate> CreateCueCandidates(string cuePath)
    {
        string? currentFile = null;
        foreach (var rawLine in File.ReadLines(cuePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                currentFile = ParseQuotedToken(line) ?? line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
                continue;
            }

            if (currentFile == null || !line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !int.TryParse(parts[1], out var trackNumber))
            {
                continue;
            }

            var mode = parts[2].ToUpperInvariant();
            if (mode.Contains("AUDIO", StringComparison.Ordinal))
            {
                continue;
            }

            var sectorSize = mode.Contains("/2352", StringComparison.Ordinal) ? 2352 : 2048;
            var dataOffset = mode.StartsWith("MODE1", StringComparison.Ordinal) && sectorSize == 2352 ? 16 :
                mode.StartsWith("MODE2", StringComparison.Ordinal) && sectorSize == 2352 ? 24 : 0;
            var dataPath = Path.Combine(Path.GetDirectoryName(cuePath) ?? string.Empty, currentFile);
            yield return new CdTrackCandidate(dataPath, sectorSize, dataOffset, 0, "PlayStation/Dreamcast CD data track", trackNumber);
        }
    }

    private static IEnumerable<CdTrackCandidate> CreateGdiCandidates(string gdiPath)
    {
        var lines = File.ReadAllLines(gdiPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count < 2 || !int.TryParse(lines[0], out _))
        {
            yield break;
        }

        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6
                || !int.TryParse(parts[0], out var trackNumber)
                || !int.TryParse(parts[2], out var trackType)
                || !int.TryParse(parts[3], out var sectorSize)
                || !long.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var byteOffset))
            {
                continue;
            }

            if (trackType == 0)
            {
                continue;
            }

            var fileName = string.Join(' ', parts.Skip(4).Take(parts.Length - 5));
            var dataOffset = sectorSize == 2352 ? 16 : 0;
            if (sectorSize == 2352 && trackType == 4)
            {
                dataOffset = 24;
            }

            var dataPath = Path.Combine(Path.GetDirectoryName(gdiPath) ?? string.Empty, fileName);
            yield return new CdTrackCandidate(dataPath, sectorSize, dataOffset, byteOffset, "Dreamcast GDI ISO9660 data track", trackNumber);
        }
    }

    private static IEnumerable<int> ProbeSectors()
    {
        yield return 16;
        yield return 166;
        for (var sector = 0; sector < MaxProbeSectors; sector++)
        {
            if (sector is 16 or 166)
            {
                continue;
            }

            yield return sector;
        }
    }

    private static string DecodeFileIdentifier(ReadOnlySpan<byte> identifier)
    {
        if (identifier.Length == 1 && identifier[0] == 0)
        {
            return ".";
        }

        if (identifier.Length == 1 && identifier[0] == 1)
        {
            return "..";
        }

        var name = Encoding.ASCII.GetString(identifier).TrimEnd('\0', ' ');
        var version = name.IndexOf(';', StringComparison.Ordinal);
        return version >= 0 ? name[..version] : name;
    }

    private static string ReadAscii(ReadOnlySpan<byte> data)
    {
        return Encoding.ASCII.GetString(data).TrimEnd('\0', ' ');
    }

    private static string? ParseQuotedToken(string line)
    {
        var first = line.IndexOf('"');
        if (first < 0)
        {
            return null;
        }

        var second = line.IndexOf('"', first + 1);
        return second > first ? line.Substring(first + 1, second - first - 1) : null;
    }

    private static string CombinePath(string parent, string name)
    {
        return parent == "/" ? "/" + name : parent.TrimEnd('/') + "/" + name;
    }

    private static IEnumerable<GenericFileSystemEntry> Walk(IEnumerable<GenericFileSystemEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Walk(entry.Children))
            {
                yield return child;
            }
        }
    }

    private readonly record struct CdTrackCandidate(
        string DataPath,
        int SectorSize,
        int DataOffset,
        long ByteOffset,
        string FamilyText,
        int TrackNumber = 0);

    private readonly record struct IsoDirectoryRecordCandidate(
        string Name,
        uint Lba,
        uint Length,
        byte Flags,
        long RecordOffset,
        bool IsDirectory);
}
