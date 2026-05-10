using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FATXTools.Wpf;

public sealed class SwitchStorageImage : IDisposable
{
    private const int SectorSize = 512;

    private static readonly Dictionary<Guid, SwitchPartitionInfo> KnownPartitions = new()
    {
        [new Guid("98109E25-64E2-4C95-8A77-414916F5BCEB")] = new("PRODINFO", 0, true, SwitchPartitionFileSystem.Raw, "Calibration binary"),
        [new Guid("F3056AEC-5449-494C-9F2C-5FDCB75B6E6E")] = new("PRODINFOF", 0, true, SwitchPartitionFileSystem.Fat12, "Calibration FAT12 filesystem"),
        [new Guid("5365DE36-911B-4BB4-8FF9-AA1EBCD73990")] = new("BCPKG2-1-Normal-Main", null, false, SwitchPartitionFileSystem.Raw, "Normal package2 main"),
        [new Guid("8455717B-BD2B-4162-8454-91695218FC38")] = new("BCPKG2-2-Normal-Sub", null, false, SwitchPartitionFileSystem.Raw, "Normal package2 backup"),
        [new Guid("8ED9BBCA-9C06-4E9B-8A45-8F03C9F9A3F9")] = new("BCPKG2-3-SafeMode-Main", null, false, SwitchPartitionFileSystem.Raw, "SafeMode package2 main"),
        [new Guid("A44F9F6B-4ED3-441F-A34A-56AAA136BC6A")] = new("SAFE", 1, true, SwitchPartitionFileSystem.Fat32, "SAFE BIS FAT32 filesystem"),
        [new Guid("B66D0B5B-C6F2-44D6-A7C5-6DA0A8AF6C78")] = new("SYSTEM", 2, true, SwitchPartitionFileSystem.Fat32, "SYSTEM BIS FAT32 filesystem"),
        [new Guid("2B777F63-E842-47AF-94C4-25A7F18B2280")] = new("USER", 3, true, SwitchPartitionFileSystem.Fat32, "USER BIS FAT32 filesystem")
    };

    private readonly FileStream _stream;

    private SwitchStorageImage(string sourcePath, FileStream stream, IReadOnlyList<PartitionModel> partitions)
    {
        SourcePath = sourcePath;
        _stream = stream;
        Partitions = partitions;
    }

    public string SourcePath { get; }

    public IReadOnlyList<PartitionModel> Partitions { get; }

    public static SwitchStorageImage Open(string sourcePath, string? keyPath)
    {
        var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        try
        {
            var records = ReadGpt(stream);
            if (records.Count == 0 || !records.Any(record => IsKnownSwitchPartition(record)))
            {
                throw new InvalidDataException("No Nintendo Switch NAND GPT partitions were found.");
            }

            var keys = SwitchBisKeySet.Load(keyPath);
            var partitions = new List<PartitionModel>();
            foreach (var record in records.OrderBy(record => record.Offset))
            {
                var info = GetPartitionInfo(record);
                var name = string.IsNullOrWhiteSpace(record.Name) ? info.Name : record.Name;
                var candidate = new GenericPartitionCandidate(record.Index, record.TypeGuid, record.Offset, record.Length, name, record.LogicalSectorSize);

                if (info is { Encrypted: true, FileSystem: SwitchPartitionFileSystem.Fat32, BisKeyId: { } keyId }
                    && keys.TryGet(keyId, out var key))
                {
                    var reader = new SwitchBisPartitionReader(sourcePath, record.Offset, record.Length, key);
                    try
                    {
                        var volume = SwitchFat32Volume.Open(candidate, reader);
                        partitions.Add(new PartitionModel(volume, $"Mounted, Switch BIS key {keyId}, FAT32, {volume.GetRoot().Count:N0} root entries"));
                        continue;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or ArgumentException)
                    {
                        partitions.Add(new PartitionModel(new SwitchRawVolume(sourcePath, candidate, info.Description), $"Detected, decrypt/mount failed: {ex.Message}"));
                        continue;
                    }
                }

                var status = info switch
                {
                    { Encrypted: true, BisKeyId: { } statusKeyId } when !keys.Contains(statusKeyId) => $"Detected, encrypted with BIS key {statusKeyId}; provide BIS KEY {statusKeyId} (crypt) and BIS KEY {statusKeyId} (tweak) to mount",
                    { FileSystem: SwitchPartitionFileSystem.Fat12 } => "Detected, FAT12 BIS partition; raw carving/export only in this build",
                    { FileSystem: SwitchPartitionFileSystem.Raw } => $"Detected, raw Switch partition: {info.Description}",
                    _ => $"Detected, {info.Description}"
                };
                partitions.Add(new PartitionModel(new SwitchRawVolume(sourcePath, candidate, info.Description), status));
            }

            return new SwitchStorageImage(sourcePath, stream, partitions);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    private static bool IsKnownSwitchPartition(SwitchGptPartitionRecord record)
    {
        if (KnownPartitions.ContainsKey(record.TypeGuid))
        {
            return true;
        }

        return record.Name is "PRODINFO" or "PRODINFOF" or "SAFE" or "SYSTEM" or "USER"
               || record.Name.StartsWith("BCPKG2-", StringComparison.OrdinalIgnoreCase);
    }

    private static SwitchPartitionInfo GetPartitionInfo(SwitchGptPartitionRecord record)
    {
        if (KnownPartitions.TryGetValue(record.TypeGuid, out var info))
        {
            return info;
        }

        return record.Name.ToUpperInvariant() switch
        {
            "PRODINFO" => new SwitchPartitionInfo("PRODINFO", 0, true, SwitchPartitionFileSystem.Raw, "Calibration binary"),
            "PRODINFOF" => new SwitchPartitionInfo("PRODINFOF", 0, true, SwitchPartitionFileSystem.Fat12, "Calibration FAT12 filesystem"),
            "SAFE" => new SwitchPartitionInfo("SAFE", 1, true, SwitchPartitionFileSystem.Fat32, "SAFE BIS FAT32 filesystem"),
            "SYSTEM" => new SwitchPartitionInfo("SYSTEM", 2, true, SwitchPartitionFileSystem.Fat32, "SYSTEM BIS FAT32 filesystem"),
            "USER" => new SwitchPartitionInfo("USER", 3, true, SwitchPartitionFileSystem.Fat32, "USER BIS FAT32 filesystem"),
            _ => new SwitchPartitionInfo(record.Name, null, false, SwitchPartitionFileSystem.Raw, "Switch raw partition")
        };
    }

    private static IReadOnlyList<SwitchGptPartitionRecord> ReadGpt(Stream stream)
    {
        var header = new byte[SectorSize];
        if (!ReadExactly(stream, SectorSize, header) || Encoding.ASCII.GetString(header, 0, 8) != "EFI PART")
        {
            return [];
        }

        var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72, 8));
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80, 4));
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84, 4));
        if (entriesLba == 0 || entryCount == 0 || entrySize < 128 || entrySize > 4096)
        {
            return [];
        }

        var records = new List<SwitchGptPartitionRecord>();
        var buffer = new byte[entrySize];
        for (uint index = 0; index < entryCount; index++)
        {
            var offset = checked((long)entriesLba * SectorSize + index * (long)entrySize);
            if (!ReadExactly(stream, offset, buffer))
            {
                break;
            }

            var typeGuid = new Guid(buffer.AsSpan(0, 16));
            if (typeGuid == Guid.Empty)
            {
                continue;
            }

            var firstLba = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(32, 8));
            var lastLba = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(40, 8));
            if (firstLba == 0 || lastLba < firstLba)
            {
                continue;
            }

            var name = Encoding.Unicode.GetString(buffer, 56, Math.Min(72, buffer.Length - 56)).TrimEnd('\0');
            records.Add(new SwitchGptPartitionRecord(
                index + 1,
                typeGuid,
                checked((long)firstLba * SectorSize),
                checked((long)(lastLba - firstLba + 1) * SectorSize),
                string.IsNullOrWhiteSpace(name) ? $"GPT partition {index + 1}" : name,
                SectorSize));
        }

        return records;
    }

    private static bool ReadExactly(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}

internal sealed class SwitchRawVolume : GenericFileSystemVolume
{
    private readonly string _description;

    public SwitchRawVolume(string sourcePath, GenericPartitionCandidate partition, string description)
        : base(sourcePath, partition, "Nintendo Switch NAND")
    {
        _description = description;
    }

    public override long ClusterSize => 0x4000;

    public override long UsedSpace => 0;

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => [];

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        progress?.Report(0);
        return [];
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return Offset + cluster * ClusterSize;
    }

    public override string ToString()
    {
        return $"{Name} ({_description})";
    }
}

internal sealed class SwitchFat32Volume : GenericFileSystemVolume
{
    private readonly SwitchBisPartitionReader _reader;
    private readonly List<GenericFileSystemEntry> _root = [];
    private readonly uint[] _fat;
    private readonly ushort _bytesPerSector;
    private readonly byte _sectorsPerCluster;
    private readonly uint _rootCluster;
    private readonly long _dataOffset;

    private SwitchFat32Volume(
        GenericPartitionCandidate partition,
        SwitchBisPartitionReader reader,
        ushort bytesPerSector,
        byte sectorsPerCluster,
        uint rootCluster,
        long dataOffset,
        uint[] fat)
        : base(reader.SourcePath, partition, "Nintendo Switch NAND FAT32")
    {
        _reader = reader;
        _bytesPerSector = bytesPerSector;
        _sectorsPerCluster = sectorsPerCluster;
        _rootCluster = rootCluster;
        _dataOffset = dataOffset;
        _fat = fat;
    }

    public override long ClusterSize => (long)_bytesPerSector * _sectorsPerCluster;

    public override long UsedSpace => Walk(_root).Where(entry => !entry.IsDirectory).Sum(entry => Math.Max(0, entry.Length));

    public static SwitchFat32Volume Open(GenericPartitionCandidate partition, SwitchBisPartitionReader reader)
    {
        Span<byte> boot = stackalloc byte[GenericFileSystemImage.SectorSize];
        if (!reader.Read(0, boot) || boot[510] != 0x55 || boot[511] != 0xAA)
        {
            throw new InvalidDataException("Could not decrypt a valid Switch FAT32 boot sector.");
        }

        var fatType = Encoding.ASCII.GetString(boot.Slice(82, 8));
        if (fatType != "FAT32   ")
        {
            throw new InvalidDataException("The decrypted Switch partition is not FAT32.");
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
        var sectorsPerCluster = boot[13];
        var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..]);
        var fatCount = boot[16];
        var sectorsPerFat = BinaryPrimitives.ReadUInt32LittleEndian(boot[36..]);
        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot[44..]);
        if (bytesPerSector == 0 || sectorsPerCluster == 0 || sectorsPerFat == 0 || rootCluster < 2)
        {
            throw new InvalidDataException("Invalid Switch FAT32 boot sector.");
        }

        var fatOffset = (long)reservedSectors * bytesPerSector;
        var fatBytes = checked((int)Math.Min((long)sectorsPerFat * bytesPerSector, int.MaxValue));
        var fatRaw = new byte[fatBytes];
        if (!reader.Read(fatOffset, fatRaw))
        {
            throw new InvalidDataException("Could not read Switch FAT32 allocation table.");
        }

        var fat = new uint[fatRaw.Length / 4];
        for (var index = 0; index < fat.Length; index++)
        {
            fat[index] = BinaryPrimitives.ReadUInt32LittleEndian(fatRaw.AsSpan(index * 4, 4)) & 0x0FFFFFFF;
        }

        var dataOffset = ((long)reservedSectors + fatCount * (long)sectorsPerFat) * bytesPerSector;
        var volume = new SwitchFat32Volume(partition, reader, bytesPerSector, sectorsPerCluster, rootCluster, dataOffset, fat);
        volume._root.AddRange(volume.ReadDirectory(rootCluster, "/", includeDeleted: false, CancellationToken.None));
        return volume;
    }

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var rows = new List<GenericFileSystemEntry>();
        ScanDeletedDirectory(_rootCluster, "/", rows, cancellationToken);
        progress?.Report(rows.Count);
        return rows;
    }

    public override void CopyFile(GenericFileSystemEntry entry, string destinationPath, Action<long>? progress, CancellationToken cancellationToken)
    {
        if (entry.IsDirectory)
        {
            throw new InvalidOperationException("Select a file, not a directory.");
        }

        const int bufferSize = 0x100000;
        var remaining = entry.Length;
        var buffer = new byte[bufferSize];
        using var output = File.Create(destinationPath);
        foreach (var extent in entry.Extents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0)
            {
                break;
            }

            var readable = Math.Min(extent.Length, remaining);
            var relativeOffset = extent.Offset - Offset;
            while (readable > 0 && remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, Math.Min(readable, remaining));
                if (!_reader.Read(relativeOffset, buffer.AsSpan(0, count)))
                {
                    return;
                }

                output.Write(buffer, 0, count);
                relativeOffset += count;
                readable -= count;
                remaining -= count;
                progress?.Invoke(count);
            }
        }
    }

    public string CreateDecryptedPartitionImage(CancellationToken cancellationToken, IProgress<long>? progress = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"drive-assistant-switch-{Guid.NewGuid():N}.img");
        const int bufferSize = SwitchBisPartitionReader.CryptoSectorSize * 256;
        var buffer = new byte[bufferSize];
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize, FileOptions.SequentialScan);
        for (long offset = 0; offset < Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, Length - offset);
            if (!_reader.Read(offset, buffer.AsSpan(0, count)))
            {
                throw new IOException("Could not decrypt Switch partition data for file carving.");
            }

            output.Write(buffer, 0, count);
            offset += count;
            progress?.Report(offset);
        }

        return path;
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return Offset + _dataOffset + (cluster - 2L) * ClusterSize;
    }

    private long ClusterToRelativeOffset(uint cluster)
    {
        return _dataOffset + (cluster - 2L) * ClusterSize;
    }

    private void ScanDeletedDirectory(uint cluster, string path, List<GenericFileSystemEntry> rows, CancellationToken cancellationToken)
    {
        foreach (var entry in ReadDirectory(cluster, path, includeDeleted: true, cancellationToken))
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

    private List<GenericFileSystemEntry> ReadDirectory(uint firstCluster, string path, bool includeDeleted, CancellationToken cancellationToken)
    {
        var entries = new List<GenericFileSystemEntry>();
        var lfnParts = new List<string>();
        foreach (var cluster in GetClusterChain(firstCluster))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = ReadCluster(cluster);
            for (var offset = 0; offset + 32 <= data.Length; offset += 32)
            {
                var entry = data.AsSpan(offset, 32);
                var first = entry[0];
                if (first == 0x00)
                {
                    return entries;
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
                var firstDataCluster = ((uint)BinaryPrimitives.ReadUInt16LittleEndian(entry[20..]) << 16)
                                       | BinaryPrimitives.ReadUInt16LittleEndian(entry[26..]);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]);
                if (deleted && isDirectory && LooksLikeSwitchContentFile(name))
                {
                    isDirectory = false;
                }

                var childPath = CombinePath(path, name);
                var clusters = deleted
                    ? BuildContiguousClusterList(firstDataCluster, isDirectory ? ClusterSize : size)
                    : GetClusterChain(firstDataCluster);
                var extents = isDirectory ? new List<FileExtent>() : BuildExtents(clusters, size);
                var children = new List<GenericFileSystemEntry>();
                var kind = isDirectory ? "Folder" : "File";
                var metadataStatus = deleted ? "Deleted Switch FAT32 directory entry" : "Active Switch FAT32 directory entry";
                if (!deleted && !isDirectory && TryCreateSwitchContainerChildren(childPath, name, size, extents, out var containerKind, out var containerStatus, out var containerChildren))
                {
                    isDirectory = true;
                    kind = containerKind;
                    metadataStatus = containerStatus;
                    children.AddRange(containerChildren);
                    extents = [];
                }

                var item = new GenericFileSystemEntry
                {
                    Volume = this,
                    Path = childPath,
                    Name = deleted ? $"_{name.TrimStart('_')}" : name,
                    Kind = kind,
                    IsDirectory = isDirectory,
                    Length = isDirectory ? 0 : size,
                    Modified = DecodeFatDateTime(entry),
                    Accessed = DecodeFatDate(entry),
                    Created = DecodeFatDateTime(entry, createTime: true),
                    Offset = ClusterToOffset(cluster) + offset,
                    Cluster = firstDataCluster,
                    IsDeleted = deleted,
                    Attributes = $"0x{attr:X2}",
                    MetadataStatus = metadataStatus,
                    Extents = extents
                };
                item.Children.AddRange(children);

                if (!deleted && children.Count == 0 && isDirectory && firstDataCluster >= 2)
                {
                    item.Children.AddRange(ReadDirectory(firstDataCluster, childPath, includeDeleted: false, cancellationToken));
                }

                entries.Add(item);
            }
        }

        return entries;
    }

    private bool TryCreateSwitchContainerChildren(
        string parentPath,
        string name,
        long size,
        IReadOnlyList<FileExtent> extents,
        out string kind,
        out string metadataStatus,
        out IReadOnlyList<GenericFileSystemEntry> children)
    {
        kind = "File";
        metadataStatus = "Active Switch FAT32 directory entry";
        children = [];
        if (size <= 0 || extents.Count == 0)
        {
            return false;
        }

        var extension = Path.GetExtension(name);
        if (extension.Equals(".nsp", StringComparison.OrdinalIgnoreCase) && TryReadPfs0Package(parentPath, name, size, extents, out var pfsChildren))
        {
            kind = "NSP/PFS0 Package";
            metadataStatus = $"Browseable Nintendo Switch PFS0 package; {pfsChildren.Count - 1:N0} nested file entries. The raw package is exposed as __container.";
            children = pfsChildren;
            return true;
        }

        if (extension.Equals(".nca", StringComparison.OrdinalIgnoreCase) && TryReadNcaArchive(parentPath, name, size, extents, out var ncaStatus, out var ncaChildren))
        {
            kind = "NCA Content Archive";
            metadataStatus = ncaStatus;
            children = ncaChildren;
            return true;
        }

        return false;
    }

    private bool TryReadPfs0Package(string parentPath, string name, long size, IReadOnlyList<FileExtent> extents, out IReadOnlyList<GenericFileSystemEntry> children)
    {
        children = [];
        var header = ReadFileSpan(extents, 0, 0x10);
        if (header.Length != 0x10 || !header.AsSpan(0, 4).SequenceEqual("PFS0"u8))
        {
            return false;
        }

        var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var stringTableSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if (fileCount == 0 || fileCount > 4096 || stringTableSize > 0x100000)
        {
            return false;
        }

        var entriesSize = checked((long)fileCount * 0x18);
        var headerSize = 0x10 + entriesSize + stringTableSize;
        if (headerSize <= 0 || headerSize > size)
        {
            return false;
        }

        var raw = ReadFileSpan(extents, 0x10, (int)(entriesSize + stringTableSize));
        if (raw.Length != entriesSize + stringTableSize)
        {
            return false;
        }

        var rows = new List<GenericFileSystemEntry>
        {
            CreateVirtualFile(parentPath, "__container" + Path.GetExtension(name), "Raw Package", 0, size, extents, "Whole NSP/PFS0 package raw export")
        };
        var stringTable = raw.AsSpan((int)entriesSize, (int)stringTableSize);
        for (var index = 0; index < fileCount; index++)
        {
            var entry = raw.AsSpan(index * 0x18, 0x18);
            var fileOffset = checked((long)Math.Min((ulong)long.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(entry)));
            var fileSize = checked((long)Math.Min((ulong)long.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(entry[8..])));
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[0x10..]);
            if (nameOffset >= stringTable.Length || headerSize + fileOffset + fileSize > size)
            {
                return false;
            }

            var childName = ReadNullTerminatedUtf8(stringTable[(int)nameOffset..]);
            if (string.IsNullOrWhiteSpace(childName))
            {
                childName = $"entry_{index:D4}.bin";
            }

            var childExtents = BuildVirtualExtents(extents, headerSize + fileOffset, fileSize);
            var childKind = GetSwitchContentKind(childName);
            var childStatus = $"PFS0 package entry {index}: {childName}";
            var childHeader = ReadFileSpan(extents, headerSize + fileOffset, (int)Math.Min(0x400, fileSize));
            if (Path.GetExtension(childName).Equals(".cnmt", StringComparison.OrdinalIgnoreCase))
            {
                childStatus = TryDescribeCnmt(childHeader) ?? childStatus;
            }
            else if (Path.GetExtension(childName).Equals(".nca", StringComparison.OrdinalIgnoreCase))
            {
                childStatus = TryDescribeNca(childHeader) ?? childStatus;
            }

            rows.Add(CreateVirtualFile(parentPath, childName, childKind, headerSize + fileOffset, fileSize, childExtents, childStatus));
        }

        children = rows;
        return true;
    }

    private bool TryReadNcaArchive(string parentPath, string name, long size, IReadOnlyList<FileExtent> extents, out string metadataStatus, out IReadOnlyList<GenericFileSystemEntry> children)
    {
        metadataStatus = "Active Switch NCA content archive";
        children = [];
        var header = ReadFileSpan(extents, 0, (int)Math.Min(0x400, size));
        if (header.Length < 0x400 || !header.AsSpan(0x200, 3).SequenceEqual("NCA"u8))
        {
            return false;
        }

        metadataStatus = TryDescribeNca(header) ?? metadataStatus;
        var rows = new List<GenericFileSystemEntry>
        {
            CreateVirtualFile(parentPath, "__container" + Path.GetExtension(name), "Raw NCA", 0, size, extents, "Whole NCA raw export")
        };

        for (var index = 0; index < 4; index++)
        {
            var section = header.AsSpan(0x240 + index * 0x10, 0x10);
            var startMedia = BinaryPrimitives.ReadUInt32LittleEndian(section);
            var endMedia = BinaryPrimitives.ReadUInt32LittleEndian(section[4..]);
            if (startMedia == 0 || endMedia <= startMedia)
            {
                continue;
            }

            var sectionOffset = startMedia * 0x200L;
            var sectionSize = Math.Min((endMedia - startMedia) * 0x200L, size - sectionOffset);
            if (sectionOffset < 0 || sectionSize <= 0 || sectionOffset >= size)
            {
                continue;
            }

            rows.Add(CreateVirtualFile(
                parentPath,
                $"section_{index}.bin",
                "NCA Section",
                sectionOffset,
                sectionSize,
                BuildVirtualExtents(extents, sectionOffset, sectionSize),
                $"NCA section {index}; encrypted/raw section span from NCA section table"));
        }

        children = rows;
        return rows.Count > 1;
    }

    private GenericFileSystemEntry CreateVirtualFile(string parentPath, string name, string kind, long relativeOffset, long length, IReadOnlyList<FileExtent> extents, string metadataStatus)
    {
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = CombinePath(parentPath, name),
            Name = name,
            Kind = kind,
            IsDirectory = false,
            Length = length,
            Offset = extents.Count > 0 ? extents[0].Offset : Offset + relativeOffset,
            Cluster = ClusterSize > 0 ? (extents.Count > 0 ? (extents[0].Offset - Offset) / ClusterSize : 0) : 0,
            Attributes = "virtual",
            MetadataStatus = metadataStatus,
            Extents = extents
        };
    }

    private byte[] ReadFileSpan(IReadOnlyList<FileExtent> extents, long relativeOffset, int count)
    {
        if (count <= 0 || relativeOffset < 0)
        {
            return [];
        }

        var output = new byte[count];
        var written = 0;
        var cursor = 0L;
        foreach (var extent in extents)
        {
            if (written >= count)
            {
                break;
            }

            var extentStart = cursor;
            var extentEnd = cursor + extent.Length;
            if (relativeOffset >= extentEnd)
            {
                cursor = extentEnd;
                continue;
            }

            var withinExtent = Math.Max(0, relativeOffset - extentStart);
            var readable = (int)Math.Min(count - written, extent.Length - withinExtent);
            if (readable <= 0)
            {
                cursor = extentEnd;
                continue;
            }

            var relativePartitionOffset = extent.Offset - Offset + withinExtent;
            if (!_reader.Read(relativePartitionOffset, output.AsSpan(written, readable)))
            {
                return output.AsSpan(0, written).ToArray();
            }

            written += readable;
            relativeOffset += readable;
            cursor = extentEnd;
        }

        return written == output.Length ? output : output.AsSpan(0, written).ToArray();
    }

    private static IReadOnlyList<FileExtent> BuildVirtualExtents(IReadOnlyList<FileExtent> parentExtents, long relativeOffset, long length)
    {
        var rows = new List<FileExtent>();
        if (relativeOffset < 0 || length <= 0)
        {
            return rows;
        }

        var remaining = length;
        var cursor = 0L;
        foreach (var extent in parentExtents)
        {
            if (remaining <= 0)
            {
                break;
            }

            var extentStart = cursor;
            var extentEnd = cursor + extent.Length;
            if (relativeOffset >= extentEnd)
            {
                cursor = extentEnd;
                continue;
            }

            var withinExtent = Math.Max(0, relativeOffset - extentStart);
            var readable = Math.Min(remaining, extent.Length - withinExtent);
            if (readable > 0)
            {
                rows.Add(new FileExtent(extent.Offset + withinExtent, readable));
                remaining -= readable;
                relativeOffset += readable;
            }

            cursor = extentEnd;
        }

        return rows;
    }

    private static string GetSwitchContentKind(string name)
    {
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".nca" => "NCA Content",
            ".cnmt" => "CNMT Metadata",
            ".tik" => "Ticket",
            ".cert" => "Certificate",
            ".nro" => "NRO Executable",
            ".nso" => "NSO Executable",
            _ => "Package File"
        };
    }

    private static string? TryDescribeNca(ReadOnlySpan<byte> header)
    {
        if (header.Length < 0x220 || !header.Slice(0x200, 3).SequenceEqual("NCA"u8))
        {
            return null;
        }

        var distribution = header[0x204] switch
        {
            0 => "download",
            1 => "gamecard",
            _ => $"0x{header[0x204]:X2}"
        };
        var contentType = header[0x205] switch
        {
            0 => "program",
            1 => "meta",
            2 => "control",
            3 => "manual",
            4 => "data",
            5 => "public data",
            _ => $"0x{header[0x205]:X2}"
        };
        var programId = BinaryPrimitives.ReadUInt64LittleEndian(header[0x210..]);
        return $"Nintendo Switch NCA content archive; {distribution}, {contentType}, crypto type 0x{header[0x206]:X2}, key index 0x{header[0x207]:X2}, program/content id 0x{programId:X16}";
    }

    private static string? TryDescribeCnmt(ReadOnlySpan<byte> header)
    {
        if (header.Length < 0x20)
        {
            return null;
        }

        var titleId = BinaryPrimitives.ReadUInt64LittleEndian(header);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var type = header[0x0C] switch
        {
            0x01 => "system program",
            0x02 => "system data",
            0x03 => "system update",
            0x04 => "boot image package",
            0x05 => "boot image package safe",
            0x80 => "application",
            0x81 => "patch",
            0x82 => "add-on content",
            0x83 => "delta",
            _ => $"0x{header[0x0C]:X2}"
        };
        var contentCount = BinaryPrimitives.ReadUInt16LittleEndian(header[0x0E..]);
        var metaCount = BinaryPrimitives.ReadUInt16LittleEndian(header[0x10..]);
        return $"Nintendo Switch CNMT content metadata; title id 0x{titleId:X16}, version {version}, type {type}, content entries {contentCount:N0}, meta entries {metaCount:N0}";
    }

    private static string ReadNullTerminatedUtf8(ReadOnlySpan<byte> value)
    {
        var length = value.IndexOf((byte)0);
        if (length < 0)
        {
            length = value.Length;
        }

        return Encoding.UTF8.GetString(value[..length]);
    }

    private byte[] ReadCluster(uint cluster)
    {
        var buffer = new byte[ClusterSize];
        _reader.Read(ClusterToRelativeOffset(cluster), buffer);
        return buffer;
    }

    private static bool LooksLikeSwitchContentFile(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".nca", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".nsp", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".xci", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".nro", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".nso", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".tik", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".cert", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".cnmt", StringComparison.OrdinalIgnoreCase);
    }

    private List<uint> GetClusterChain(uint firstCluster)
    {
        var chain = new List<uint>();
        var seen = new HashSet<uint>();
        var cluster = firstCluster;
        while (cluster >= 2 && cluster < _fat.Length && cluster < 0x0FFFFFF8 && seen.Add(cluster))
        {
            chain.Add(cluster);
            var next = _fat[cluster];
            if (next >= 0x0FFFFFF8 || next == 0)
            {
                break;
            }

            cluster = next;
        }

        return chain;
    }

    private List<uint> BuildContiguousClusterList(uint firstCluster, long length)
    {
        if (firstCluster < 2)
        {
            return [];
        }

        var count = Math.Max(1, (int)((length + ClusterSize - 1) / ClusterSize));
        return Enumerable.Range((int)firstCluster, count)
            .Where(cluster => cluster > 1 && cluster < _fat.Length)
            .Select(cluster => (uint)cluster)
            .ToList();
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

internal sealed class SwitchBisPartitionReader
{
    internal const int CryptoSectorSize = 0x4000;
    private readonly string _sourcePath;
    private readonly long _partitionOffset;
    private readonly long _partitionLength;
    private readonly SwitchBisKey _key;

    public SwitchBisPartitionReader(string sourcePath, long partitionOffset, long partitionLength, SwitchBisKey key)
    {
        _sourcePath = sourcePath;
        _partitionOffset = partitionOffset;
        _partitionLength = partitionLength;
        _key = key;
    }

    public string SourcePath => _sourcePath;

    public bool Read(long relativeOffset, Span<byte> destination)
    {
        if (relativeOffset < 0 || relativeOffset + destination.Length > _partitionLength)
        {
            return false;
        }

        using var stream = new FileStream(_sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        var sectorOffset = (int)(relativeOffset % CryptoSectorSize);
        var copied = 0;
        var sector = new byte[CryptoSectorSize];
        while (copied < destination.Length)
        {
            var sectorIndex = (relativeOffset + copied) / CryptoSectorSize;
            var absoluteOffset = _partitionOffset + sectorIndex * CryptoSectorSize;
            if (!ReadExactly(stream, absoluteOffset, sector))
            {
                return false;
            }

            SwitchXts.DecryptSector(sector, _key, (ulong)sectorIndex);
            var sourceOffset = copied == 0 ? sectorOffset : 0;
            var count = Math.Min(destination.Length - copied, CryptoSectorSize - sourceOffset);
            sector.AsSpan(sourceOffset, count).CopyTo(destination[copied..]);
            copied += count;
        }

        return true;
    }

    private static bool ReadExactly(Stream stream, long offset, byte[] buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}

internal sealed class SwitchBisKeySet
{
    private static readonly Regex BisTextRegex = new(@"BIS\s+KEY\s+(?<id>[0-3])\s+\((?<kind>crypt|tweak)\)\s*[:=]?\s*(?<hex>[0-9a-fA-F]{32})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ProdKeysRegex = new(@"bis_key_(?<id>0[0-3])_(?<kind>crypt|tweak)\s*=\s*(?<hex>[0-9a-fA-F]{32})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly Dictionary<int, SwitchBisKey> _keys;

    private SwitchBisKeySet(Dictionary<int, SwitchBisKey> keys)
    {
        _keys = keys;
    }

    public static SwitchBisKeySet Empty { get; } = new([]);

    public static SwitchBisKeySet Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        var text = File.ReadAllText(path);
        var parts = new Dictionary<int, (byte[]? Crypt, byte[]? Tweak)>();
        foreach (Match match in BisTextRegex.Matches(text).Concat(ProdKeysRegex.Matches(text)))
        {
            var id = int.Parse(match.Groups["id"].Value) % 10;
            var kind = match.Groups["kind"].Value.ToLowerInvariant();
            var bytes = Convert.FromHexString(match.Groups["hex"].Value);
            parts.TryGetValue(id, out var current);
            parts[id] = kind == "crypt"
                ? (bytes, current.Tweak)
                : (current.Crypt, bytes);
        }

        var keys = new Dictionary<int, SwitchBisKey>();
        foreach (var (id, value) in parts)
        {
            if (value.Crypt?.Length == 16 && value.Tweak?.Length == 16)
            {
                keys[id] = new SwitchBisKey(value.Crypt, value.Tweak);
            }
        }

        return new SwitchBisKeySet(keys);
    }

    public bool Contains(int id) => _keys.ContainsKey(id);

    public bool TryGet(int id, out SwitchBisKey key)
    {
        if (_keys.TryGetValue(id, out var found))
        {
            key = found;
            return true;
        }

        key = null!;
        return false;
    }
}

internal static class SwitchXts
{
    public static void DecryptSector(Span<byte> sector, SwitchBisKey key, ulong sectorIndex)
    {
        using var dataAes = Aes.Create();
        dataAes.Mode = CipherMode.ECB;
        dataAes.Padding = PaddingMode.None;
        dataAes.Key = key.Crypt;
        using var tweakAes = Aes.Create();
        tweakAes.Mode = CipherMode.ECB;
        tweakAes.Padding = PaddingMode.None;
        tweakAes.Key = key.Tweak;
        using var dataDecryptor = dataAes.CreateDecryptor();
        using var tweakEncryptor = tweakAes.CreateEncryptor();

        Span<byte> tweakInput = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(tweakInput[8..], sectorIndex);
        var tweakPlain = tweakInput.ToArray();
        var tweakBytes = new byte[16];
        tweakEncryptor.TransformBlock(tweakPlain, 0, 16, tweakBytes, 0);
        var input = new byte[16];
        var output = new byte[16];
        for (var offset = 0; offset + 16 <= sector.Length; offset += 16)
        {
            for (var index = 0; index < 16; index++)
            {
                input[index] = (byte)(sector[offset + index] ^ tweakBytes[index]);
            }

            dataDecryptor.TransformBlock(input, 0, 16, output, 0);
            for (var index = 0; index < 16; index++)
            {
                sector[offset + index] = (byte)(output[index] ^ tweakBytes[index]);
            }

            MultiplyTweak(tweakBytes);
        }
    }

    private static void MultiplyTweak(byte[] tweak)
    {
        var carryIn = 0;
        for (var index = 0; index < tweak.Length; index++)
        {
            var value = tweak[index];
            var carryOut = (value >> 7) & 1;
            tweak[index] = (byte)(((value << 1) & 0xFF) | carryIn);
            carryIn = carryOut;
        }

        if (carryIn != 0)
        {
            tweak[0] ^= 0x87;
        }
    }
}

internal sealed record SwitchBisKey(byte[] Crypt, byte[] Tweak);

internal sealed record SwitchGptPartitionRecord(uint Index, Guid TypeGuid, long Offset, long Length, string Name, int LogicalSectorSize);

internal sealed record SwitchPartitionInfo(string Name, int? BisKeyId, bool Encrypted, SwitchPartitionFileSystem FileSystem, string Description);

internal enum SwitchPartitionFileSystem
{
    Raw,
    Fat12,
    Fat32
}
