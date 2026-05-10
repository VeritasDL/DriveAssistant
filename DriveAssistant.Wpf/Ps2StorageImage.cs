using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FATXTools.Wpf;

internal sealed class Ps2StorageImage : IDisposable
{
    private const int SectorSize = 512;
    private const int HeaderSize = 1024;
    private const uint ApaMagic = 0x00415041;
    private const ushort ApaTypePfs = 0x0100;
    private const ushort ApaTypeHdl = 0x1337;
    private const int PfsSuperSector = 8192;

    private readonly FileStream _stream;

    private Ps2StorageImage(string sourcePath, FileStream stream, IReadOnlyList<PartitionModel> partitions)
    {
        SourcePath = sourcePath;
        _stream = stream;
        Partitions = partitions;
    }

    public string SourcePath { get; }

    public IReadOnlyList<PartitionModel> Partitions { get; }

    public static Ps2StorageImage Open(string sourcePath)
    {
        var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        try
        {
            var headers = ReadApaHeaders(stream);
            if (headers.Count == 0)
            {
                throw new InvalidDataException("No PlayStation 2 APA partition headers were found.");
            }

            var partitions = headers
                .OrderBy(header => header.StartSector)
                .Select(header =>
                {
                    var candidate = new GenericPartitionCandidate(
                        header.StartSector,
                        Guid.Empty,
                        header.StartSector * SectorSize,
                        header.LengthSectors * SectorSize,
                        header.Id,
                        SectorSize);
                    var family = header.Type switch
                    {
                        ApaTypePfs => "PlayStation 2 APA/PFS",
                        ApaTypeHdl => "PlayStation 2 HDLoader",
                        _ => "PlayStation 2 APA"
                    };
                    if (header.Type == ApaTypePfs
                        && Ps2PfsVolume.TryOpen(sourcePath, candidate, family, headers, out var pfsVolume, out var pfsStatus))
                    {
                        return new PartitionModel(pfsVolume, pfsStatus);
                    }

                    var description = header.Type switch
                    {
                        ApaTypePfs => "PFS partition detected; raw export and file carving are available.",
                        ApaTypeHdl => "HDLoader game partition detected; raw export and file carving are available.",
                        _ => $"APA partition type 0x{header.Type:X4}; raw export and file carving are available."
                    };
                    return new PartitionModel(new RawConsoleVolume(sourcePath, candidate, family, description), description);
                })
                .ToList();

            return new Ps2StorageImage(sourcePath, stream, partitions);
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

    private static List<Ps2ApaHeader> ReadApaHeaders(FileStream stream)
    {
        var headers = new List<Ps2ApaHeader>();
        var seen = new HashSet<uint>();
        uint sector = 0;
        for (var count = 0; count < 4096; count++)
        {
            if (!seen.Add(sector))
            {
                break;
            }

            if (!TryReadHeader(stream, sector, out var header))
            {
                break;
            }

            headers.Add(header);
            if (header.NextSector == 0 || header.NextSector == sector)
            {
                break;
            }

            sector = header.NextSector;
        }

        return headers;
    }

    private static bool TryReadHeader(FileStream stream, uint sector, out Ps2ApaHeader header)
    {
        header = default;
        var offset = sector * (long)SectorSize;
        if (offset < 0 || offset + HeaderSize > stream.Length)
        {
            return false;
        }

        var buffer = new byte[HeaderSize];
        stream.Position = offset;
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)) != ApaMagic)
        {
            return false;
        }

        var id = ReadNullTerminatedAscii(buffer.AsSpan(16, 32));
        if (string.IsNullOrWhiteSpace(id))
        {
            id = sector == 0 ? "__mbr" : $"apa_{sector:X8}";
        }

        var start = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(64));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(68));
        if (length == 0)
        {
            return false;
        }

        header = new Ps2ApaHeader(
            id,
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8)),
            start == 0 ? sector : start,
            length,
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(72)));
        return true;
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        if (end >= 0)
        {
            data = data[..end];
        }

        return Encoding.ASCII.GetString(data).Trim();
    }

    internal readonly record struct Ps2ApaHeader(
        string Id,
        uint NextSector,
        uint StartSector,
        uint LengthSectors,
        ushort Type);
}

internal sealed class Ps2PfsVolume : GenericFileSystemVolume
{
    private const int SectorSize = 512;
    private const int PfsBlockSize = 0x2000;
    private const int PfsSuperSector = 8192;
    private const uint PfsSuperMagic = 0x50465300;
    private const uint PfsSegmentMagic = 0x53454744;
    private const int PfsInodeSize = 1024;
    private const int PfsDentrySectorSize = 512;
    private const int PfsMaxInodeBlocks = 114;
    private const ushort PfsModeDirectory = 0x1000;
    private const ushort PfsModeRegular = 0x2000;
    private const ushort PfsModeMask = 0xF000;

    private readonly IReadOnlyDictionary<int, PfsSubpart> _subparts;
    private readonly HashSet<string> _activeInodeKeys = new(StringComparer.Ordinal);
    private readonly PfsSuperBlock _superBlock;
    private readonly List<GenericFileSystemEntry> _root = [];

    private Ps2PfsVolume(
        string sourcePath,
        GenericPartitionCandidate partition,
        string familyText,
        IReadOnlyDictionary<int, PfsSubpart> subparts,
        PfsSuperBlock superBlock)
        : base(sourcePath, partition, familyText)
    {
        _subparts = subparts;
        _superBlock = superBlock;
        _activeInodeKeys.Add(BlockKey(_superBlock.Root));
        LoadDirectory(_superBlock.Root, "/", _root, new HashSet<string>(), CancellationToken.None, includeDeleted: false, deletedRows: null);
    }

    public override long ClusterSize => PfsBlockSize;

    public override long UsedSpace => Walk(_root).Where(entry => !entry.IsDirectory).Sum(entry => entry.Length);

    public static bool TryOpen(
        string sourcePath,
        GenericPartitionCandidate partition,
        string familyText,
        IReadOnlyList<Ps2StorageImage.Ps2ApaHeader> headers,
        out Ps2PfsVolume volume,
        out string status)
    {
        volume = null!;
        status = string.Empty;
        try
        {
            var superBlock = ReadSuperBlock(sourcePath, partition.Offset);
            if (superBlock.Magic != PfsSuperMagic || superBlock.Version == 0)
            {
                status = "PFS partition detected, but the PFS superblock did not validate; raw export and file carving are available.";
                return false;
            }

            var subparts = BuildSubparts(partition, headers);
            volume = new Ps2PfsVolume(sourcePath, partition, familyText, subparts, superBlock);
            status = $"Mounted, PS2 PFS, {volume.GetRoot().Count:N0} root entries, zone 0x{superBlock.ZoneSize:X}, {superBlock.NumSubparts:N0} subparts";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            status = $"PFS partition detected, but mount failed: {ex.Message}; raw export and file carving are available.";
            return false;
        }
    }

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var rows = new List<GenericFileSystemEntry>();
        LoadDirectory(_superBlock.Root, "/", new List<GenericFileSystemEntry>(), new HashSet<string>(), cancellationToken, includeDeleted: true, rows);
        ScanOrphanInodes(rows, cancellationToken, progress);
        progress?.Report(100);
        return rows
            .GroupBy(entry => $"{entry.Offset:X}:{entry.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return BlockToOffset(0, cluster);
    }

    private static PfsSuperBlock ReadSuperBlock(string sourcePath, long partitionOffset)
    {
        Span<byte> data = stackalloc byte[40];
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        if (!GenericFileSystemImage.ReadExactly(stream, partitionOffset + PfsSuperSector * SectorSize, data))
        {
            throw new EndOfStreamException("Could not read PFS superblock.");
        }

        return new PfsSuperBlock(
            BinaryPrimitives.ReadUInt32LittleEndian(data[0..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            ReadBlockInfo(data[24..]),
            ReadBlockInfo(data[32..]));
    }

    private static IReadOnlyDictionary<int, PfsSubpart> BuildSubparts(GenericPartitionCandidate partition, IReadOnlyList<Ps2StorageImage.Ps2ApaHeader> headers)
    {
        var subparts = new Dictionary<int, PfsSubpart>
        {
            [0] = new PfsSubpart(partition.Offset, partition.Length)
        };

        var prefix = partition.Name + ".";
        foreach (var header in headers)
        {
            if (!header.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(header.Id[prefix.Length..], out var index) && index >= 1)
            {
                subparts[index] = new PfsSubpart(header.StartSector * (long)SectorSize, header.LengthSectors * (long)SectorSize);
            }
        }

        return subparts;
    }

    private void LoadDirectory(
        PfsBlockInfo directoryBlock,
        string path,
        List<GenericFileSystemEntry> rows,
        HashSet<string> visited,
        CancellationToken cancellationToken,
        bool includeDeleted,
        List<GenericFileSystemEntry>? deletedRows)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = $"{directoryBlock.Subpart}:{directoryBlock.Number}";
        if (!visited.Add(key))
        {
            return;
        }

        var inode = ReadInode(directoryBlock);
        var activeOffsets = new HashSet<long>();
        foreach (var record in EnumerateDirectoryRecords(inode, scanSlack: includeDeleted, activeOffsets))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Name is "." or "..")
            {
                continue;
            }

            var entryPath = CombinePath(path, record.Name);
            var childBlock = new PfsBlockInfo(record.InodeBlock, record.Subpart, 1);
            var deleted = record.IsDeletedCandidate;
            if (!TryReadInode(childBlock, out var childInode))
            {
                if (deleted)
                {
                    deletedRows?.Add(CreateMetadataOnlyDeletedEntry(record, entryPath));
                }

                continue;
            }

            var child = CreateEntry(childInode, record, entryPath, deleted);
            if (deleted)
            {
                deletedRows?.Add(child);
                continue;
            }

            rows.Add(child);
            _activeInodeKeys.Add(BlockKey(childBlock));
            if (child.IsDirectory)
            {
                LoadDirectory(childBlock, entryPath, child.Children, visited, cancellationToken, includeDeleted, deletedRows);
            }
        }
    }

    private IEnumerable<PfsDirectoryRecord> EnumerateDirectoryRecords(PfsInode inode, bool scanSlack, HashSet<long> activeOffsets)
    {
        var remainingLength = inode.Size > long.MaxValue ? long.MaxValue : (long)inode.Size;
        foreach (var extent in BuildPfsExtents(inode, includeInodeBlock: false))
        {
            var readable = Math.Min(extent.Length, Math.Max(0, remainingLength));
            if (readable <= 0)
            {
                break;
            }

            using var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
            var buffer = new byte[(int)Math.Min(readable, 16 * 1024 * 1024)];
            var consumed = 0L;
            while (consumed < readable)
            {
                var chunk = (int)Math.Min(buffer.Length, readable - consumed);
                if (!GenericFileSystemImage.ReadExactly(stream, extent.Offset + consumed, buffer.AsSpan(0, chunk)))
                {
                    break;
                }

                foreach (var record in EnumerateDirectoryBuffer(buffer.AsSpan(0, chunk), extent.Offset + consumed, scanSlack, activeOffsets))
                {
                    yield return record;
                }

                consumed += chunk;
            }

            remainingLength -= consumed;
        }
    }

    private static List<PfsDirectoryRecord> EnumerateDirectoryBuffer(ReadOnlySpan<byte> data, long absoluteOffset, bool scanSlack, HashSet<long> activeOffsets)
    {
        var rows = new List<PfsDirectoryRecord>();
        for (var sector = 0; sector + PfsDentrySectorSize <= data.Length; sector += PfsDentrySectorSize)
        {
            var offset = sector;
            while (offset + 8 <= sector + PfsDentrySectorSize)
            {
                var recordOffset = absoluteOffset + offset;
                if (!TryReadDirectoryRecord(data[offset..], recordOffset, deletedCandidate: false, out var record))
                {
                    break;
                }

                var length = record.AllocatedLength;
                if (length <= 0 || offset + length > sector + PfsDentrySectorSize)
                {
                    break;
                }

                activeOffsets.Add(recordOffset);
                if (record.InodeBlock != 0 && record.Name.Length > 0)
                {
                    rows.Add(record);
                }

                if (scanSlack)
                {
                    var activeSize = (record.Name.Length + 8 + 3) & ~3;
                    var slackStart = offset + Math.Max(8, activeSize);
                    for (var candidateOffset = (slackStart + 3) & ~3; candidateOffset + 8 < offset + length; candidateOffset += 4)
                    {
                        var candidateAbsolute = absoluteOffset + candidateOffset;
                        if (activeOffsets.Contains(candidateAbsolute))
                        {
                            continue;
                        }

                        if (TryReadDirectoryRecord(data[candidateOffset..], candidateAbsolute, deletedCandidate: true, out var candidate)
                            && candidate.InodeBlock != 0
                            && candidate.Name.Length > 0
                            && candidate.Name is not "." and not "..")
                        {
                            rows.Add(candidate);
                        }
                    }
                }

                offset += length;
            }
        }

        return rows;
    }

    private static bool TryReadDirectoryRecord(ReadOnlySpan<byte> data, long absoluteOffset, bool deletedCandidate, out PfsDirectoryRecord record)
    {
        record = default;
        if (data.Length < 8)
        {
            return false;
        }

        var inode = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var sub = data[4];
        var nameLength = data[5];
        var rawLength = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var allocatedLength = rawLength & 0x0FFF;
        var type = (ushort)(rawLength & PfsModeMask);
        if (allocatedLength < 8 || allocatedLength > PfsDentrySectorSize || allocatedLength % 4 != 0 || nameLength > 255 || 8 + nameLength > allocatedLength || 8 + nameLength > data.Length)
        {
            return false;
        }

        var nameBytes = data.Slice(8, nameLength);
        if (nameBytes.IndexOf((byte)0) >= 0 || !IsPrintableName(nameBytes))
        {
            return false;
        }

        var name = Encoding.ASCII.GetString(nameBytes);
        record = new PfsDirectoryRecord(inode, sub, name, allocatedLength, type, absoluteOffset, deletedCandidate);
        return true;
    }

    private static bool IsPrintableName(ReadOnlySpan<byte> name)
    {
        if (name.Length == 0)
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

    private GenericFileSystemEntry CreateEntry(PfsInode inode, PfsDirectoryRecord record, string path, bool deleted)
    {
        var isDirectory = (inode.Mode & PfsModeMask) == PfsModeDirectory || record.Type == PfsModeDirectory;
        var extents = isDirectory ? [] : BuildPfsExtents(inode, includeInodeBlock: false);
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = path,
            Name = record.Name,
            Kind = isDirectory ? "Directory" : "File",
            IsDirectory = isDirectory,
            Length = isDirectory ? 0 : checked((long)Math.Min(inode.Size, long.MaxValue)),
            Created = inode.Created,
            Modified = inode.Modified,
            Accessed = inode.Accessed,
            Offset = record.Offset,
            Cluster = record.InodeBlock,
            IsDeleted = deleted,
            Attributes = $"mode 0x{inode.Mode:X4}, attr 0x{inode.Attr:X4}",
            MetadataStatus = deleted ? "Deleted/slack PS2 PFS directory entry candidate" : "Active PS2 PFS directory entry",
            Extents = extents
        };
    }

    private GenericFileSystemEntry CreateMetadataOnlyDeletedEntry(PfsDirectoryRecord record, string path)
    {
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = path,
            Name = record.Name,
            Kind = record.Type == PfsModeDirectory ? "Directory" : "File",
            IsDirectory = record.Type == PfsModeDirectory,
            Length = 0,
            Offset = record.Offset,
            Cluster = record.InodeBlock,
            IsDeleted = true,
            Attributes = $"dentry type 0x{record.Type:X4}",
            MetadataStatus = "Deleted/slack PS2 PFS directory entry; inode no longer readable",
            Extents = []
        };
    }

    private PfsInode ReadInode(PfsBlockInfo block)
    {
        if (!TryReadInode(block, out var inode))
        {
            throw new InvalidDataException($"PFS inode block {block.Number} in subpart {block.Subpart} did not validate.");
        }

        return inode;
    }

    private bool TryReadInode(PfsBlockInfo block, out PfsInode inode)
    {
        inode = default!;
        var offset = BlockToOffset(block.Subpart, block.Number);
        Span<byte> data = stackalloc byte[PfsInodeSize];
        using var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        if (!GenericFileSystemImage.ReadExactly(stream, offset, data))
        {
            return false;
        }

        return TryParseInode(block, data, out inode);
    }

    private static bool TryParseInode(PfsBlockInfo block, ReadOnlySpan<byte> data, out PfsInode inode)
    {
        inode = default!;
        if (data.Length < PfsInodeSize || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != PfsSegmentMagic)
        {
            return false;
        }

        var blocks = new List<PfsBlockInfo>();
        for (var index = 0; index < PfsMaxInodeBlocks; index++)
        {
            var info = ReadBlockInfo(data[(40 + index * 8)..]);
            if (info.Number == 0 || info.Count == 0)
            {
                continue;
            }

            blocks.Add(info);
        }

        inode = new PfsInode(
            block,
            ReadBlockInfo(data[16..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[952..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[954..]),
            ReadDate(data[960..]),
            ReadDate(data[968..]),
            ReadDate(data[976..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[984..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[992..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[996..]),
            blocks);
        return true;
    }

    private void ScanOrphanInodes(List<GenericFileSystemEntry> rows, CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var seen = new HashSet<string>(_activeInodeKeys, StringComparer.Ordinal);
        var totalBlocks = _subparts.Values.Sum(subpart => Math.Max(0, subpart.Length / PfsBlockSize));
        if (totalBlocks <= 0)
        {
            return;
        }

        var scanned = 0L;
        Span<byte> data = stackalloc byte[PfsInodeSize];
        using var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        foreach (var pair in _subparts.OrderBy(pair => pair.Key))
        {
            var subpart = pair.Value;
            var blockCount = Math.Max(0, subpart.Length / PfsBlockSize);
            for (var block = 0L; block < blockCount; block++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var absoluteOffset = subpart.Offset + block * PfsBlockSize;
                if (absoluteOffset < 0 || absoluteOffset + PfsInodeSize > stream.Length)
                {
                    break;
                }

                if (!GenericFileSystemImage.ReadExactly(stream, absoluteOffset, data))
                {
                    break;
                }

                var blockInfo = new PfsBlockInfo((uint)block, pair.Key, 1);
                var key = BlockKey(blockInfo);
                if (!seen.Contains(key)
                    && TryParseInode(blockInfo, data, out var inode)
                    && IsRecoverableOrphanInode(inode))
                {
                    var entry = CreateOrphanInodeEntry(inode);
                    rows.Add(entry);
                    foreach (var segment in EnumerateInodeSegments(inode))
                    {
                        seen.Add(BlockKey(segment.Block));
                    }
                }

                scanned++;
                if ((scanned & 0xFFF) == 0)
                {
                    progress?.Report((int)Math.Min(99, scanned * 100 / totalBlocks));
                }
            }
        }
    }

    private static bool IsRecoverableOrphanInode(PfsInode inode)
    {
        var mode = inode.Mode & PfsModeMask;
        if (mode is not (PfsModeRegular or PfsModeDirectory))
        {
            return false;
        }

        if (inode.Size > long.MaxValue)
        {
            return false;
        }

        return mode == PfsModeDirectory || inode.DataBlocks.Count > 0;
    }

    private GenericFileSystemEntry CreateOrphanInodeEntry(PfsInode inode)
    {
        var mode = inode.Mode & PfsModeMask;
        var isDirectory = mode == PfsModeDirectory;
        var name = $"orphan_pfs_inode_s{inode.Block.Subpart}_{inode.Block.Number:X8}";
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = "/$ORPHANED/" + name,
            Name = name,
            Kind = isDirectory ? "Directory" : "File",
            IsDirectory = isDirectory,
            Length = isDirectory ? 0 : checked((long)Math.Min(inode.Size, long.MaxValue)),
            Created = inode.Created,
            Modified = inode.Modified,
            Accessed = inode.Accessed,
            Offset = BlockToOffset(inode.Block.Subpart, inode.Block.Number),
            Cluster = inode.Block.Number,
            IsDeleted = true,
            Attributes = $"mode 0x{inode.Mode:X4}, attr 0x{inode.Attr:X4}",
            MetadataStatus = "Unreferenced PS2 PFS inode candidate",
            Extents = isDirectory ? [] : BuildPfsExtents(inode, includeInodeBlock: false)
        };
    }

    private List<FileExtent> BuildPfsExtents(PfsInode inode, bool includeInodeBlock)
    {
        var extents = new List<FileExtent>();
        foreach (var segment in EnumerateInodeSegments(inode))
        {
            var dataBlockCount = segment.NumberData == 0
                ? segment.DataBlocks.Count
                : (int)Math.Min(segment.NumberData, (uint)segment.DataBlocks.Count);
            foreach (var info in segment.DataBlocks.Take(dataBlockCount))
            {
                if (!includeInodeBlock && info.Number == segment.Block.Number && info.Subpart == segment.Block.Subpart)
                {
                    continue;
                }

                var length = info.Count * (long)PfsBlockSize;
                if (length <= 0)
                {
                    continue;
                }

                extents.Add(new FileExtent(BlockToOffset(info.Subpart, info.Number), length));
            }
        }

        return extents;
    }

    private IEnumerable<PfsInode> EnumerateInodeSegments(PfsInode inode)
    {
        var current = inode;
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{inode.Block.Subpart}:{inode.Block.Number}"
        };

        while (true)
        {
            yield return current;
            var next = current.NextSegment;
            if (next.Number == 0)
            {
                yield break;
            }

            var key = $"{next.Subpart}:{next.Number}";
            if (!visited.Add(key) || !TryReadInode(next, out current))
            {
                yield break;
            }
        }
    }

    private long BlockToOffset(int subpart, uint block)
    {
        if (!_subparts.TryGetValue(subpart, out var partition))
        {
            partition = new PfsSubpart(Offset, Length);
        }

        return partition.Offset + block * (long)PfsBlockSize;
    }

    private static string BlockKey(PfsBlockInfo block)
    {
        return $"{block.Subpart}:{block.Number}";
    }

    private static PfsBlockInfo ReadBlockInfo(ReadOnlySpan<byte> data)
    {
        return new PfsBlockInfo(
            BinaryPrimitives.ReadUInt32LittleEndian(data),
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[6..]));
    }

    private static DateTime ReadDate(ReadOnlySpan<byte> data)
    {
        try
        {
            var second = data[1];
            var minute = data[2];
            var hour = data[3];
            var day = data[4];
            var month = data[5];
            var year = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            if (year < 1970 || month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 59)
            {
                return DateTime.MinValue;
            }

            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
        }
        catch
        {
            return DateTime.MinValue;
        }
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

    private readonly record struct PfsSuperBlock(
        uint Magic,
        uint Version,
        uint ZoneSize,
        uint NumSubparts,
        PfsBlockInfo Log,
        PfsBlockInfo Root);

    private readonly record struct PfsBlockInfo(uint Number, int Subpart, ushort Count);

    private readonly record struct PfsSubpart(long Offset, long Length);

    private sealed record PfsInode(
        PfsBlockInfo Block,
        PfsBlockInfo NextSegment,
        ushort Mode,
        ushort Attr,
        DateTime Accessed,
        DateTime Created,
        DateTime Modified,
        ulong Size,
        uint NumberBlocks,
        uint NumberData,
        IReadOnlyList<PfsBlockInfo> DataBlocks);

    private readonly record struct PfsDirectoryRecord(
        uint InodeBlock,
        int Subpart,
        string Name,
        int AllocatedLength,
        ushort Type,
        long Offset,
        bool IsDeletedCandidate);
}
