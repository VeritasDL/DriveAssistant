using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FATXTools.Wpf;

public sealed class Fat12Volume : GenericFileSystemVolume
{
    private readonly List<GenericFileSystemEntry> _root = [];
    private readonly byte[] _fat;
    private readonly ushort _bytesPerSector;
    private readonly byte _sectorsPerCluster;
    private readonly long _rootDirectoryOffset;
    private readonly long _rootDirectoryLength;
    private readonly long _dataOffset;

    private Fat12Volume(
        string sourcePath,
        GenericPartitionCandidate partition,
        ushort bytesPerSector,
        byte sectorsPerCluster,
        long rootDirectoryOffset,
        long rootDirectoryLength,
        long dataOffset,
        byte[] fat)
        : base(sourcePath, partition, "FAT12")
    {
        _bytesPerSector = bytesPerSector;
        _sectorsPerCluster = sectorsPerCluster;
        _rootDirectoryOffset = rootDirectoryOffset;
        _rootDirectoryLength = rootDirectoryLength;
        _dataOffset = dataOffset;
        _fat = fat;
    }

    public override long ClusterSize => (long)_bytesPerSector * _sectorsPerCluster;

    public override long UsedSpace => Walk(_root).Where(entry => !entry.IsDirectory).Sum(entry => Math.Max(0, entry.Length));

    public static Fat12Volume Open(string sourcePath, GenericPartitionCandidate partition)
    {
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        Span<byte> boot = stackalloc byte[GenericFileSystemImage.SectorSize];
        if (!GenericFileSystemImage.ReadExactly(stream, partition.Offset, boot))
        {
            throw new InvalidDataException("Could not read FAT12 boot sector.");
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
        var sectorsPerCluster = boot[13];
        var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..]);
        var fatCount = boot[16];
        var rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..]);
        var totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(boot[19..]);
        var sectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(boot[22..]);
        var totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(boot[32..]);
        var totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;
        if (!IsValidBoot(bytesPerSector, sectorsPerCluster, reservedSectors, fatCount, rootEntryCount, sectorsPerFat, totalSectors))
        {
            throw new InvalidDataException("Invalid FAT12 boot sector.");
        }

        var fatOffset = partition.Offset + (long)reservedSectors * bytesPerSector;
        var fatBytes = checked((int)Math.Min((long)sectorsPerFat * bytesPerSector, int.MaxValue));
        var fatRaw = new byte[fatBytes];
        if (!GenericFileSystemImage.ReadExactly(stream, fatOffset, fatRaw))
        {
            throw new InvalidDataException("Could not read FAT12 table.");
        }

        var rootDirectoryOffset = fatOffset + fatCount * (long)sectorsPerFat * bytesPerSector;
        var rootDirectoryLength = ((rootEntryCount * 32L + bytesPerSector - 1) / bytesPerSector) * bytesPerSector;
        var dataOffset = rootDirectoryOffset + rootDirectoryLength;
        var volume = new Fat12Volume(sourcePath, partition, bytesPerSector, sectorsPerCluster, rootDirectoryOffset, rootDirectoryLength, dataOffset, fatRaw);
        volume._root.AddRange(volume.ReadDirectoryBytes(volume.ReadBytes(rootDirectoryOffset, rootDirectoryLength), "/", rootDirectoryOffset, includeDeleted: false, CancellationToken.None));
        return volume;
    }

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var rows = new List<GenericFileSystemEntry>();
        foreach (var entry in ReadDirectoryBytes(ReadBytes(_rootDirectoryOffset, _rootDirectoryLength), "/", _rootDirectoryOffset, includeDeleted: true, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDeleted)
            {
                rows.Add(entry);
            }
            else if (entry.IsDirectory && entry.Cluster >= 2)
            {
                ScanDeletedDirectory((uint)entry.Cluster, entry.Path, rows, cancellationToken);
            }
        }

        progress?.Report(100);
        return rows;
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return _dataOffset + (cluster - 2L) * ClusterSize;
    }

    private void ScanDeletedDirectory(uint cluster, string path, List<GenericFileSystemEntry> rows, CancellationToken cancellationToken)
    {
        foreach (var entry in ReadDirectoryClusterChain(cluster, path, includeDeleted: true, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDeleted)
            {
                rows.Add(entry);
            }
            else if (entry.IsDirectory && entry.Cluster >= 2)
            {
                ScanDeletedDirectory((uint)entry.Cluster, entry.Path, rows, cancellationToken);
            }
        }
    }

    private List<GenericFileSystemEntry> ReadDirectoryClusterChain(uint firstCluster, string path, bool includeDeleted, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        foreach (var cluster in GetClusterChain(firstCluster))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = ReadBytes(ClusterToOffset(cluster), ClusterSize);
            output.Write(data, 0, data.Length);
        }

        return ReadDirectoryBytes(output.ToArray(), path, ClusterToOffset(firstCluster), includeDeleted, cancellationToken);
    }

    private List<GenericFileSystemEntry> ReadDirectoryBytes(byte[] data, string path, long directoryOffset, bool includeDeleted, CancellationToken cancellationToken)
    {
        var entries = new List<GenericFileSystemEntry>();
        var lfnParts = new List<string>();
        for (var offset = 0; offset + 32 <= data.Length; offset += 32)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = data.AsSpan(offset, 32);
            var first = entry[0];
            if (first == 0x00)
            {
                break;
            }

            var attr = entry[11];
            if (attr == 0x0F)
            {
                lfnParts.Insert(0, DecodeFatLongName(entry));
                continue;
            }

            var deleted = first == 0xE5;
            if (deleted && !includeDeleted)
            {
                lfnParts.Clear();
                continue;
            }

            if ((attr & 0x08) != 0)
            {
                lfnParts.Clear();
                continue;
            }

            var shortName = DecodeFatShortName(entry, deleted);
            if (shortName is "." or "..")
            {
                lfnParts.Clear();
                continue;
            }

            var name = string.Concat(lfnParts).TrimEnd('\0');
            lfnParts.Clear();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = shortName;
            }

            var isDirectory = (attr & 0x10) != 0;
            var firstDataCluster = BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]);
            var childPath = CombinePath(path, name);
            var clusters = deleted
                ? BuildContiguousClusterList(firstDataCluster, isDirectory ? ClusterSize : size)
                : GetClusterChain(firstDataCluster);
            var extents = isDirectory ? new List<FileExtent>() : BuildExtents(clusters, size);
            var item = new GenericFileSystemEntry
            {
                Volume = this,
                Path = childPath,
                Name = deleted ? $"_{name.TrimStart('_')}" : name,
                Kind = isDirectory ? "Folder" : "File",
                IsDirectory = isDirectory,
                Length = isDirectory ? 0 : size,
                Modified = DecodeFatDateTime(entry),
                Accessed = DecodeFatDate(entry),
                Created = DecodeFatDateTime(entry, createTime: true),
                Offset = directoryOffset + offset,
                Cluster = firstDataCluster,
                IsDeleted = deleted,
                Attributes = $"0x{attr:X2}",
                MetadataStatus = deleted ? "Deleted FAT12 directory entry" : "Active FAT12 directory entry",
                Extents = extents
            };

            if (!deleted && isDirectory && firstDataCluster >= 2)
            {
                item.Children.AddRange(ReadDirectoryClusterChain(firstDataCluster, childPath, includeDeleted: false, cancellationToken));
            }

            entries.Add(item);
        }

        return entries;
    }

    private byte[] ReadBytes(long offset, long length)
    {
        var buffer = new byte[checked((int)Math.Min(length, int.MaxValue))];
        using var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        GenericFileSystemImage.ReadExactly(stream, offset, buffer);
        return buffer;
    }

    private List<uint> GetClusterChain(uint firstCluster)
    {
        var chain = new List<uint>();
        var seen = new HashSet<uint>();
        var cluster = firstCluster;
        while (cluster >= 2 && cluster < MaxClusterCount && cluster < 0xFF8 && seen.Add(cluster))
        {
            chain.Add(cluster);
            var next = ReadFatEntry(cluster);
            if (next >= 0xFF8 || next == 0)
            {
                break;
            }

            cluster = next;
        }

        return chain;
    }

    private uint ReadFatEntry(uint cluster)
    {
        var offset = (int)(cluster + cluster / 2);
        if (offset + 1 >= _fat.Length)
        {
            return 0xFFF;
        }

        var pair = _fat[offset] | (_fat[offset + 1] << 8);
        return (cluster & 1) == 0 ? (uint)(pair & 0x0FFF) : (uint)(pair >> 4);
    }

    private uint MaxClusterCount => (uint)(_fat.Length * 2 / 3);

    private List<uint> BuildContiguousClusterList(uint firstCluster, long length)
    {
        if (firstCluster < 2)
        {
            return [];
        }

        var count = Math.Max(1, (int)((length + ClusterSize - 1) / ClusterSize));
        return Enumerable.Range((int)firstCluster, count)
            .Where(cluster => cluster > 1 && cluster < MaxClusterCount)
            .Select(cluster => (uint)cluster)
            .ToList();
    }

    private static bool IsValidBoot(
        ushort bytesPerSector,
        byte sectorsPerCluster,
        ushort reservedSectors,
        byte fatCount,
        ushort rootEntryCount,
        ushort sectorsPerFat,
        uint totalSectors)
    {
        return bytesPerSector is 512 or 1024 or 2048 or 4096
            && sectorsPerCluster != 0
            && (sectorsPerCluster & (sectorsPerCluster - 1)) == 0
            && reservedSectors != 0
            && fatCount is 1 or 2
            && rootEntryCount != 0
            && sectorsPerFat != 0
            && totalSectors != 0;
    }

    private static string DecodeFatLongName(ReadOnlySpan<byte> entry)
    {
        Span<byte> raw = stackalloc byte[26];
        entry.Slice(1, 10).CopyTo(raw);
        entry.Slice(14, 12).CopyTo(raw[10..]);
        entry.Slice(28, 4).CopyTo(raw[22..]);
        return Encoding.Unicode.GetString(raw).TrimEnd('\0', '\uffff');
    }

    private static string DecodeFatShortName(ReadOnlySpan<byte> entry, bool deleted)
    {
        Span<byte> nameRaw = stackalloc byte[11];
        entry[..11].CopyTo(nameRaw);
        if (deleted)
        {
            nameRaw[0] = (byte)'_';
        }

        var name = Encoding.ASCII.GetString(nameRaw[..8]).Trim();
        var extension = Encoding.ASCII.GetString(nameRaw[8..]).Trim();
        return string.IsNullOrWhiteSpace(extension) ? name : $"{name}.{extension}";
    }

    private static DateTime DecodeFatDateTime(ReadOnlySpan<byte> entry, bool createTime = false)
    {
        var timeOffset = createTime ? 14 : 22;
        var dateOffset = createTime ? 16 : 24;
        var time = BinaryPrimitives.ReadUInt16LittleEndian(entry[timeOffset..]);
        var date = BinaryPrimitives.ReadUInt16LittleEndian(entry[dateOffset..]);
        return DecodeFatDateTime(date, time);
    }

    private static DateTime DecodeFatDate(ReadOnlySpan<byte> entry)
    {
        var date = BinaryPrimitives.ReadUInt16LittleEndian(entry[18..]);
        return DecodeFatDateTime(date, 0);
    }

    private static DateTime DecodeFatDateTime(ushort date, ushort time)
    {
        try
        {
            var year = 1980 + ((date >> 9) & 0x7F);
            var month = (date >> 5) & 0x0F;
            var day = date & 0x1F;
            var hour = (time >> 11) & 0x1F;
            var minute = (time >> 5) & 0x3F;
            var second = (time & 0x1F) * 2;
            return month == 0 || day == 0 ? DateTime.MinValue : new DateTime(year, month, day, hour, minute, second);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string CombinePath(string path, string name)
    {
        return path.TrimEnd('/') + "/" + name;
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
}
