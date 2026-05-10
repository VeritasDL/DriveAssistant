using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FATXTools.Wpf;

internal static class SgiIrixStorageImage
{
    private const int BlockSize = 512;
    private const uint VolumeHeaderMagic = 0x0BE5A941;
    private const int VolumeDirectoryOffset = 72;
    private const int VolumeDirectoryCount = 15;
    private const int VolumeDirectoryEntrySize = 16;
    private const int PartitionTableOffset = 312;
    private const int PartitionTableCount = 16;
    private const int PartitionTableEntrySize = 12;
    private const uint PartitionTypeSysV = 0x05;
    private const uint PartitionTypeVolume = 0x06;
    private const uint PartitionTypeEfs = 0x07;

    public static bool TryOpen(string sourcePath, long sourceLength, ReadOnlySpan<byte> header, out IReadOnlyList<PartitionModel> partitions)
    {
        partitions = [];
        if (header.Length < BlockSize || BinaryPrimitives.ReadUInt32BigEndian(header[..4]) != VolumeHeaderMagic)
        {
            return false;
        }

        var rows = new List<PartitionModel>();
        var volumeHeader = CreateVolumeHeaderVolume(sourcePath, sourceLength, header);
        rows.Add(new PartitionModel(volumeHeader, "SGI volume header/disklabel detected; volume-header files and partition rows are browsable."));

        foreach (var partition in ReadPartitionTable(header))
        {
            if (partition.BlockCount == 0 || partition.ByteOffset < 0 || partition.ByteLength <= 0 || partition.ByteOffset >= sourceLength)
            {
                continue;
            }

            var boundedLength = Math.Min(partition.ByteLength, sourceLength - partition.ByteOffset);
            if (partition.Type is PartitionTypeEfs or PartitionTypeSysV
                && SgiEfsVolume.TryOpen(sourcePath, partition.Index, partition.ByteOffset, boundedLength, out var efsVolume, out var efsStatus))
            {
                rows.Add(new PartitionModel(efsVolume, efsStatus));
                continue;
            }

            if (partition.Type is PartitionTypeVolume)
            {
                continue;
            }

            rows.Add(CreateRawPartition(sourcePath, partition, boundedLength));
        }

        partitions = rows;
        return rows.Count > 0;
    }

    private static RawConsoleVolume CreateVolumeHeaderVolume(string sourcePath, long sourceLength, ReadOnlySpan<byte> header)
    {
        var candidate = new GenericPartitionCandidate(0, Guid.Empty, 0, Math.Min(sourceLength, 32130L * BlockSize), "SGI volume header", BlockSize);
        var volume = new RawConsoleVolume(sourcePath, candidate, "SGI/IRIX volume header", "SGI disklabel and boot volume directory");
        var entries = new List<GenericFileSystemEntry>();

        var bootFile = ReadAscii(header.Slice(8, 16));
        if (!string.IsNullOrWhiteSpace(bootFile))
        {
            entries.Add(CreateFileEntry(volume, "/bootfile.txt", "bootfile.txt", "SGI Boot File", 8, 16, "metadata", "SGI volume-header boot file field"));
        }

        foreach (var entry in ReadVolumeDirectory(header))
        {
            if (entry.ByteOffset < 0 || entry.ByteOffset >= sourceLength || entry.ByteLength <= 0)
            {
                continue;
            }

            entries.Add(CreateFileEntry(
                volume,
                "/" + entry.Name,
                entry.Name,
                "SGI Volume Header File",
                entry.ByteOffset,
                Math.Min(entry.ByteLength, sourceLength - entry.ByteOffset),
                "volume header",
                $"SGI volume-header entry at LBN {entry.BlockNumber:N0}"));
        }

        foreach (var partition in ReadPartitionTable(header).Where(row => row.BlockCount > 0))
        {
            var name = $"partition-{partition.Index:D2}-{DescribePartitionType(partition.Type).Replace(' ', '-')}.bin";
            entries.Add(CreateFileEntry(
                volume,
                "/" + name,
                name,
                "SGI Partition Row",
                partition.ByteOffset,
                Math.Min(partition.ByteLength, Math.Max(0, sourceLength - partition.ByteOffset)),
                $"type 0x{partition.Type:X}",
                $"SGI disklabel partition {partition.Index}, {DescribePartitionType(partition.Type)}, first LBN {partition.FirstBlock:N0}, {partition.BlockCount:N0} block(s)"));
        }

        volume.AddRootEntries(entries);
        return volume;
    }

    private static PartitionModel CreateRawPartition(string sourcePath, SgiPartitionRow partition, long length)
    {
        var candidate = new GenericPartitionCandidate((uint)partition.Index, Guid.Empty, partition.ByteOffset, length, $"SGI partition {partition.Index}", BlockSize);
        var volume = new RawConsoleVolume(sourcePath, candidate, "SGI/IRIX raw partition", DescribePartitionType(partition.Type));
        volume.AddRootEntries(
        [
            CreateFileEntry(
                volume,
                $"/sgi-partition-{partition.Index:D2}.bin",
                $"sgi-partition-{partition.Index:D2}.bin",
                "SGI Raw Partition",
                partition.ByteOffset,
                length,
                $"type 0x{partition.Type:X}",
                $"SGI disklabel partition {partition.Index}, {DescribePartitionType(partition.Type)}")
        ]);
        return new PartitionModel(volume, $"Detected SGI disklabel partition {partition.Index}, {DescribePartitionType(partition.Type)}; raw export and carving are available.");
    }

    private static IReadOnlyList<SgiVolumeDirectoryEntry> ReadVolumeDirectory(ReadOnlySpan<byte> header)
    {
        var rows = new List<SgiVolumeDirectoryEntry>();
        for (var index = 0; index < VolumeDirectoryCount; index++)
        {
            var offset = VolumeDirectoryOffset + index * VolumeDirectoryEntrySize;
            if (offset + VolumeDirectoryEntrySize > header.Length)
            {
                break;
            }

            var name = ReadAscii(header.Slice(offset, 8));
            var block = BinaryPrimitives.ReadUInt32BigEndian(header[(offset + 8)..]);
            var bytes = BinaryPrimitives.ReadUInt32BigEndian(header[(offset + 12)..]);
            if (string.IsNullOrWhiteSpace(name) || bytes == 0)
            {
                continue;
            }

            rows.Add(new SgiVolumeDirectoryEntry(name, block, bytes));
        }

        return rows;
    }

    private static IReadOnlyList<SgiPartitionRow> ReadPartitionTable(ReadOnlySpan<byte> header)
    {
        var rows = new List<SgiPartitionRow>();
        for (var index = 0; index < PartitionTableCount; index++)
        {
            var offset = PartitionTableOffset + index * PartitionTableEntrySize;
            if (offset + PartitionTableEntrySize > header.Length)
            {
                break;
            }

            var blocks = BinaryPrimitives.ReadUInt32BigEndian(header[offset..]);
            var first = BinaryPrimitives.ReadUInt32BigEndian(header[(offset + 4)..]);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header[(offset + 8)..]);
            rows.Add(new SgiPartitionRow(index, first, blocks, type));
        }

        return rows;
    }

    private static GenericFileSystemEntry CreateFileEntry(GenericFileSystemVolume volume, string path, string name, string kind, long offset, long length, string attributes, string status)
    {
        return new GenericFileSystemEntry
        {
            Volume = volume,
            Path = path,
            Name = name,
            Kind = kind,
            IsDirectory = false,
            Length = length,
            Offset = offset,
            Cluster = offset / BlockSize,
            Attributes = attributes,
            MetadataStatus = status,
            Extents = length > 0 ? [new FileExtent(offset, length)] : []
        };
    }

    private static string DescribePartitionType(uint type)
    {
        return type switch
        {
            0x00 => "volume header",
            0x01 => "track replacement",
            0x02 => "sector replacement",
            0x03 => "raw",
            0x04 => "BSD",
            PartitionTypeSysV => "System V/EFS",
            PartitionTypeVolume => "whole volume",
            PartitionTypeEfs => "EFS",
            0x08 => "logical volume",
            0x09 => "raw logical volume",
            0x0A => "XFS",
            0x0B => "XFS log",
            0x0C => "XLV",
            _ => $"unknown type 0x{type:X}"
        };
    }

    private static string ReadAscii(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        if (end >= 0)
        {
            data = data[..end];
        }

        return Encoding.ASCII.GetString(data).Trim();
    }

    private sealed record SgiVolumeDirectoryEntry(string Name, uint BlockNumber, uint ByteLength)
    {
        public long ByteOffset => BlockNumber * (long)BlockSize;
    }

    private sealed record SgiPartitionRow(int Index, uint FirstBlock, uint BlockCount, uint Type)
    {
        public long ByteOffset => FirstBlock * (long)BlockSize;

        public long ByteLength => BlockCount * (long)BlockSize;
    }
}

internal sealed class SgiEfsVolume : GenericFileSystemVolume
{
    private const int BlockSize = 512;
    private const int SuperBlock = 1;
    private const uint EfsMagic = 0x072959;
    private const uint EfsNewMagic = 0x07295A;
    private const ushort DirectoryMagic = 0xBEEF;
    private const int InodeSize = 128;
    private const int InodesPerBlock = BlockSize / InodeSize;
    private const int RootInode = 2;
    private const int MaxEntries = 200_000;

    private readonly EfsSuperBlock _superBlock;
    private readonly List<GenericFileSystemEntry> _root = [];
    private int _entryCount;

    private SgiEfsVolume(string sourcePath, GenericPartitionCandidate partition, EfsSuperBlock superBlock)
        : base(sourcePath, partition, "SGI IRIX EFS")
    {
        _superBlock = superBlock;
    }

    public override long ClusterSize => BlockSize;

    public override long UsedSpace => Length;

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public static bool TryOpen(string sourcePath, int partitionIndex, long offset, long length, out SgiEfsVolume volume, out string status)
    {
        volume = null!;
        status = string.Empty;
        try
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
            Span<byte> super = stackalloc byte[BlockSize];
            if (!GenericFileSystemImage.ReadExactly(stream, offset + SuperBlock * BlockSize, super))
            {
                return false;
            }

            var magic = BinaryPrimitives.ReadUInt32BigEndian(super[28..]);
            if (magic is not EfsMagic and not EfsNewMagic)
            {
                return false;
            }

            var parsed = new EfsSuperBlock(
                StartBlock: offset / BlockSize,
                TotalBlocks: BinaryPrimitives.ReadUInt32BigEndian(super[0..]),
                FirstBlock: BinaryPrimitives.ReadUInt32BigEndian(super[4..]),
                GroupSize: BinaryPrimitives.ReadUInt32BigEndian(super[8..]),
                InodeBlocks: BinaryPrimitives.ReadUInt16BigEndian(super[12..]),
                TotalGroups: BinaryPrimitives.ReadUInt16BigEndian(super[18..]),
                Magic: magic);

            if (parsed.TotalBlocks == 0 || parsed.FirstBlock == 0 || parsed.GroupSize == 0 || parsed.InodeBlocks == 0)
            {
                return false;
            }

            var candidate = new GenericPartitionCandidate((uint)partitionIndex, Guid.Empty, offset, length, $"SGI EFS partition {partitionIndex}", BlockSize);
            volume = new SgiEfsVolume(sourcePath, candidate, parsed);
            using var reader = new EfsReader(sourcePath, parsed, length);
            var rootInode = reader.ReadInode(RootInode);
            if (!rootInode.IsDirectory)
            {
                return false;
            }

            volume._root.AddRange(volume.ReadDirectory(reader, rootInode, "/", 0));
            status = $"Mounted SGI IRIX EFS partition {partitionIndex}, {volume._root.Count:N0} root entries. Metadata scan, directory browsing, carving, and export are available.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or ArgumentException)
        {
            status = $"SGI IRIX EFS mount failed: {ex.Message}";
            return false;
        }
    }

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        progress?.Report(0);
        return [];
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return (_superBlock.StartBlock + cluster) * BlockSize;
    }

    private IReadOnlyList<GenericFileSystemEntry> ReadDirectory(EfsReader reader, EfsInode directory, string path, int depth)
    {
        if (depth > 64 || _entryCount >= MaxEntries)
        {
            return [];
        }

        var rows = new List<GenericFileSystemEntry>();
        foreach (var block in reader.ReadFileBlocks(directory))
        {
            if (block.Length < BlockSize || BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(0, 2)) != DirectoryMagic)
            {
                continue;
            }

            var slots = Math.Min((int)block[3], 254);
            for (var slot = 0; slot < slots; slot++)
            {
                if (_entryCount >= MaxEntries)
                {
                    return rows;
                }

                var entryOffset = block[4 + slot] * 2;
                if (entryOffset < 4 || entryOffset + 5 > block.Length)
                {
                    continue;
                }

                var inodeNumber = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(entryOffset, 4));
                var nameLength = block[entryOffset + 4];
                if (inodeNumber == 0 || nameLength == 0 || entryOffset + 5 + nameLength > block.Length)
                {
                    continue;
                }

                var name = Encoding.ASCII.GetString(block, entryOffset + 5, nameLength);
                if (name is "." or ".." || name.IndexOf('\0') >= 0)
                {
                    continue;
                }

                EfsInode child;
                try
                {
                    child = reader.ReadInode(inodeNumber);
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                var childPath = CombinePath(path, name);
                var entry = CreateEntry(child, childPath, name);
                _entryCount++;
                if (entry.IsDirectory)
                {
                    entry.Children.AddRange(ReadDirectory(reader, child, childPath, depth + 1));
                }

                rows.Add(entry);
            }
        }

        return rows;
    }

    private GenericFileSystemEntry CreateEntry(EfsInode inode, string path, string name)
    {
        var firstExtent = inode.Extents.Count > 0 ? inode.Extents[0] : new FileExtent(0, 0);
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = path,
            Name = name,
            Kind = inode.IsDirectory ? "IRIX Directory" : inode.IsSymlink ? "IRIX Symlink" : "IRIX File",
            IsDirectory = inode.IsDirectory,
            Length = inode.Size,
            Created = ToDateTime(inode.Created),
            Modified = ToDateTime(inode.Modified),
            Accessed = ToDateTime(inode.Accessed),
            Offset = firstExtent.Offset,
            Cluster = firstExtent.Offset / BlockSize,
            Attributes = $"mode 0{inode.Mode:X}; inode {inode.Number}",
            MetadataStatus = $"Active SGI EFS inode {inode.Number}, {inode.Extents.Count:N0} extent(s)",
            Extents = inode.IsDirectory ? [] : inode.Extents
        };
    }

    private static DateTime ToDateTime(uint seconds)
    {
        if (seconds == 0)
        {
            return DateTime.MinValue;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    private static string CombinePath(string parent, string name)
    {
        return parent == "/" ? "/" + name : parent + "/" + name;
    }

    private sealed class EfsReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly EfsSuperBlock _superBlock;
        private readonly long _partitionLength;

        public EfsReader(string sourcePath, EfsSuperBlock superBlock, long partitionLength)
        {
            _stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
            _superBlock = superBlock;
            _partitionLength = partitionLength;
        }

        public EfsInode ReadInode(uint inodeNumber)
        {
            var inodeIndex = inodeNumber / InodesPerBlock;
            var block = _superBlock.StartBlock
                        + _superBlock.FirstBlock
                        + _superBlock.GroupSize * (inodeIndex / _superBlock.InodeBlocks)
                        + inodeIndex % _superBlock.InodeBlocks;
            var offset = (long)block * BlockSize + inodeNumber % InodesPerBlock * InodeSize;
            Span<byte> data = stackalloc byte[InodeSize];
            if (!GenericFileSystemImage.ReadExactly(_stream, offset, data))
            {
                throw new InvalidDataException($"Could not read SGI EFS inode {inodeNumber}.");
            }

            var mode = BinaryPrimitives.ReadUInt16BigEndian(data[0..]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
            var numExtents = BinaryPrimitives.ReadUInt16BigEndian(data[28..]);
            var directExtents = new List<EfsExtent>();
            for (var index = 0; index < Math.Min((int)numExtents, 12); index++)
            {
                var raw = data.Slice(32 + index * 8, 8);
                var extent = ReadExtent(raw);
                if (extent.Magic != 0 || extent.Length == 0)
                {
                    continue;
                }

                directExtents.Add(extent);
            }

            var extents = BuildFileExtents(directExtents, numExtents, size);
            return new EfsInode(
                inodeNumber,
                numExtents,
                mode,
                size,
                BinaryPrimitives.ReadUInt32BigEndian(data[12..]),
                BinaryPrimitives.ReadUInt32BigEndian(data[16..]),
                BinaryPrimitives.ReadUInt32BigEndian(data[20..]),
                extents,
                directExtents);
        }

        public IEnumerable<byte[]> ReadFileBlocks(EfsInode inode)
        {
            var blockCount = inode.Size <= 0 ? 0 : (int)Math.Min(int.MaxValue, (inode.Size + BlockSize - 1) / BlockSize);
            for (var logicalBlock = 0; logicalBlock < blockCount; logicalBlock++)
            {
                var diskBlock = MapBlock(inode, logicalBlock);
                if (diskBlock <= 0)
                {
                    continue;
                }

                var buffer = new byte[BlockSize];
                if (GenericFileSystemImage.ReadExactly(_stream, diskBlock * (long)BlockSize, buffer))
                {
                    yield return buffer;
                }
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
        }

        private List<FileExtent> BuildFileExtents(IReadOnlyList<EfsExtent> directExtents, int numExtents, long size)
        {
            var rows = new List<FileExtent>();
            if (size <= 0 || numExtents <= 0)
            {
                return rows;
            }

            if (numExtents <= 12)
            {
                foreach (var extent in directExtents.OrderBy(extent => extent.LogicalOffset))
                {
                    var logicalStart = extent.LogicalOffset * (long)BlockSize;
                    if (logicalStart >= size)
                    {
                        continue;
                    }

                    var length = Math.Min(extent.Length * (long)BlockSize, size - logicalStart);
                    rows.Add(new FileExtent((_superBlock.StartBlock + extent.BlockNumber) * (long)BlockSize, length));
                }
            }
            else
            {
                foreach (var logicalBlock in Enumerable.Range(0, (int)Math.Min(int.MaxValue, (size + BlockSize - 1) / BlockSize)))
                {
                    var diskBlock = MapBlockFromIndirect(directExtents, logicalBlock);
                    if (diskBlock <= 0)
                    {
                        continue;
                    }

                    var physical = diskBlock * (long)BlockSize;
                    var length = Math.Min(BlockSize, size - logicalBlock * (long)BlockSize);
                    if (rows.Count > 0 && rows[^1].Offset + rows[^1].Length == physical)
                    {
                        rows[^1] = rows[^1] with { Length = rows[^1].Length + length };
                    }
                    else
                    {
                        rows.Add(new FileExtent(physical, length));
                    }
                }
            }

            return rows;
        }

        private long MapBlock(EfsInode inode, int logicalBlock)
        {
            if (inode.ExtentCount <= 12)
            {
                foreach (var extent in inode.DirectExtents)
                {
                    if (logicalBlock >= extent.LogicalOffset && logicalBlock < extent.LogicalOffset + extent.Length)
                    {
                        return _superBlock.StartBlock + extent.BlockNumber + logicalBlock - extent.LogicalOffset;
                    }
                }

                return 0;
            }

            return MapBlockFromIndirect(inode.DirectExtents, logicalBlock);
        }

        private long MapBlockFromIndirect(IReadOnlyList<EfsExtent> directExtents, int logicalBlock)
        {
            var buffer = new byte[BlockSize];
            foreach (var direct in directExtents)
            {
                for (var blockOffset = 0; blockOffset < direct.Length; blockOffset++)
                {
                    var physicalBlock = _superBlock.StartBlock + direct.BlockNumber + blockOffset;
                    if (!GenericFileSystemImage.ReadExactly(_stream, physicalBlock * (long)BlockSize, buffer))
                    {
                        return 0;
                    }

                    for (var index = 0; index < BlockSize / 8; index++)
                    {
                        var extent = ReadExtent(buffer.AsSpan(index * 8, 8));
                        if (extent.Magic != 0 || extent.Length == 0)
                        {
                            continue;
                        }

                        if (logicalBlock >= extent.LogicalOffset && logicalBlock < extent.LogicalOffset + extent.Length)
                        {
                            return _superBlock.StartBlock + extent.BlockNumber + logicalBlock - extent.LogicalOffset;
                        }
                    }
                }
            }

            return 0;
        }

        private static EfsExtent ReadExtent(ReadOnlySpan<byte> raw)
        {
            return new EfsExtent(
                raw[0],
                raw[1] << 16 | raw[2] << 8 | raw[3],
                raw[4],
                raw[5] << 16 | raw[6] << 8 | raw[7]);
        }
    }

    private sealed record EfsSuperBlock(long StartBlock, uint TotalBlocks, uint FirstBlock, uint GroupSize, ushort InodeBlocks, ushort TotalGroups, uint Magic);

    private sealed record EfsExtent(int Magic, int BlockNumber, int Length, int LogicalOffset);

    private sealed record EfsInode(uint Number, int ExtentCount, ushort Mode, long Size, uint Accessed, uint Modified, uint Created, IReadOnlyList<FileExtent> Extents, IReadOnlyList<EfsExtent> DirectExtents)
    {
        public bool IsDirectory => (Mode & 0xF000) == 0x4000;

        public bool IsSymlink => (Mode & 0xF000) == 0xA000;
    }
}
