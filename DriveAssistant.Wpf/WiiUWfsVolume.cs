using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FATXTools.Wpf;

internal sealed class WiiUWfsVolume : GenericFileSystemVolume
{
    private const int SectorSize = 512;
    private const int PhysicalBlockSize = 0x1000;
    private const int MetadataHeaderSize = 0x18;
    private const int DeviceHeaderSize = 0x48;
    private const int AreaHeaderSize = 0x60;
    private const int SubBlockAllocatorHeaderSize = 0x20;
    private const int DirectoryTreeHeaderOffset = MetadataHeaderSize + SubBlockAllocatorHeaderSize;
    private const int EntryMetadataSize = 0x2C;
    private const uint WfsVersion = 0x01010800;
    private const uint DirectoryFlag = 0x80000000;
    private const uint QuotaFlag = 0x40000000;
    private const uint AreaSizeRegularFlag = 0x20000000;
    private const uint UnencryptedFileFlag = 0x02000000;
    private const uint DirectoryLeafTreeFlag = 0x20000000;
    private readonly WiiUKeyMaterial _keyMaterial;
    private readonly uint _deviceIv;
    private readonly WfsArea _rootArea;
    private readonly List<GenericFileSystemEntry> _root = [];
    private readonly Dictionary<GenericFileSystemEntry, WfsNodeInfo> _entryInfo = new();
    private readonly Dictionary<(uint PhysicalBlock, int BlockLog2), WfsBlock> _loadedBlocks = [];
    private readonly HashSet<(uint PhysicalBlock, ushort MetadataOffset)> _activeMetadata = [];

    private WiiUWfsVolume(
        string sourcePath,
        GenericPartitionCandidate partition,
        WiiUKeyMaterial keyMaterial,
        uint deviceIv,
        WfsArea rootArea)
        : base(sourcePath, partition, "Nintendo Wii U WFS")
    {
        _keyMaterial = keyMaterial;
        _deviceIv = deviceIv;
        _rootArea = rootArea;
        LoadDirectory(rootArea, rootArea.RootDirectoryBlockNumber, "/", _root, depth: 0, new HashSet<(uint, uint)>());
    }

    public bool HasMlcKey => _keyMaterial.MlcKey.Length == 16;

    public override long ClusterSize => 1L << _rootArea.BlockLog2;

    public override long UsedSpace => Walk(_root).Where(entry => !entry.IsDirectory).Sum(entry => entry.Length);

    public static bool TryOpen(string sourcePath, string? keyPath, out WiiUWfsVolume volume, out string status)
    {
        volume = null!;
        if (!WiiUKeyMaterial.TryLoad(keyPath, out var keyMaterial, out var keyStatus) || keyMaterial == null)
        {
            status = $"Wii U WFS keys unavailable. {keyStatus}";
            return false;
        }

        try
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
            foreach (var rootBlockSize in new[] { PhysicalBlockSize, 0x2000 })
            {
                if (stream.Length < rootBlockSize)
                {
                    continue;
                }

                var encryptedRoot = new byte[rootBlockSize];
                if (!GenericFileSystemImage.ReadExactly(stream, 0, encryptedRoot))
                {
                    continue;
                }

                var rootBlock = DecryptBlock(encryptedRoot, keyMaterial.MlcKey, stream.Length, iv: 0);
                if (!TryParseDeviceHeader(rootBlock, out var deviceIv, out var deviceType))
                {
                    continue;
                }

                var rootArea = ReadArea(rootBlock, physicalBlock: 0, isRootArea: true);
                if (rootArea.BlockLog2 is < 12 or > 16 || rootArea.RootDirectoryBlockNumber == 0)
                {
                    continue;
                }

                var candidate = new GenericPartitionCandidate(0, Guid.Empty, 0, stream.Length, Path.GetFileName(sourcePath), SectorSize);
                volume = new WiiUWfsVolume(sourcePath, candidate, keyMaterial, deviceIv, rootArea);
                if (volume.GetRoot().Count == 0)
                {
                    status = $"Wii U WFS header decrypted, but no root directory entries were parsed. Device type 0x{deviceType:X4}.";
                    volume = null!;
                    return false;
                }

                status = $"Mounted Wii U WFS MLC, device type 0x{deviceType:X4}, {volume.GetRoot().Count:N0} root entries. File export decrypts WFS file blocks with otp.bin.";
                return true;
            }

            status = $"Wii U WFS header did not decrypt as a supported MLC volume. {keyStatus}";
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException or CryptographicException)
        {
            status = $"Wii U WFS mount failed: {ex.Message}. {keyStatus}";
            return false;
        }
    }

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var rows = new List<GenericFileSystemEntry>();
        var blocks = _loadedBlocks.Values.ToList();
        for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = blocks[blockIndex];
            for (ushort offset = 0x40; offset + EntryMetadataSize <= block.Data.Length; offset = (ushort)(offset + 8))
            {
                if (_activeMetadata.Contains((block.PhysicalBlock, offset)))
                {
                    continue;
                }

                var metadata = ReadEntryMetadata(block.Data, offset);
                if (!metadata.IsPlausible)
                {
                    continue;
                }

                var name = $"wfs_deleted_{block.PhysicalBlock:X8}_{offset:X4}";
                var node = new WfsNodeInfo(_rootArea, block, offset, metadata, name);
                var entry = CreateEntry(name, "/$DELETED/" + name, node, isDeleted: true);
                rows.Add(entry);
                _entryInfo[entry] = node;
            }

            progress?.Report((int)((blockIndex + 1) * 100L / Math.Max(1, blocks.Count)));
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

        if (!_entryInfo.TryGetValue(entry, out var node))
        {
            base.CopyFile(entry, destinationPath, progress, cancellationToken);
            return;
        }

        using var output = File.Create(destinationPath);
        foreach (var chunk in ReadFileChunks(node, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Length == 0)
            {
                continue;
            }

            output.Write(chunk, 0, chunk.Length);
            progress?.Invoke(chunk.Length);
        }
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return cluster * (long)PhysicalBlockSize;
    }

    private void LoadDirectory(WfsArea area, uint directoryBlockNumber, string path, List<GenericFileSystemEntry> rows, int depth, HashSet<(uint, uint)> visited)
    {
        if (depth > 64 || !visited.Add((area.PhysicalBlock, directoryBlockNumber)))
        {
            return;
        }

        var directoryBlocks = EnumerateDirectoryLeafBlocks(area, LoadMetadataBlock(area, directoryBlockNumber)).Take(8192).ToList();
        foreach (var block in directoryBlocks)
        {
            foreach (var pair in EnumerateDirectoryTree(block.Data, leafValueSize: 2))
            {
                if (pair.Value <= 0 || pair.Value + EntryMetadataSize > block.Data.Length || string.IsNullOrWhiteSpace(pair.Name))
                {
                    continue;
                }

                var metadataOffset = (ushort)pair.Value;
                var metadata = ReadEntryMetadata(block.Data, metadataOffset);
                if (!metadata.IsPlausible)
                {
                    continue;
                }

                var name = metadata.GetCaseSensitiveName(pair.Name);
                var childPath = CombinePath(path, name);
                var node = new WfsNodeInfo(area, block, metadataOffset, metadata, name);
                var entry = CreateEntry(name, childPath, node, isDeleted: false);
                rows.Add(entry);
                _entryInfo[entry] = node;
                _activeMetadata.Add((block.PhysicalBlock, metadataOffset));

                if (!entry.IsDirectory)
                {
                    continue;
                }

                try
                {
                    var childArea = area;
                    var childDirectoryBlock = metadata.DirectoryBlockNumber;
                    if (metadata.IsQuota)
                    {
                        var quotaBlockSizeLog2 = metadata.HasRegularAreaSize ? 13 : 12;
                        var quotaBlock = LoadMetadataBlock(area, metadata.DirectoryBlockNumber, quotaBlockSizeLog2);
                        childArea = ReadArea(quotaBlock.Data, quotaBlock.PhysicalBlock, isRootArea: false);
                        childDirectoryBlock = childArea.RootDirectoryBlockNumber;
                    }

                    LoadDirectory(childArea, childDirectoryBlock, childPath, entry.Children, depth + 1, visited);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException or CryptographicException)
                {
                    entry.Children.Clear();
                }
            }
        }

        visited.Remove((area.PhysicalBlock, directoryBlockNumber));
    }

    private GenericFileSystemEntry CreateEntry(string name, string path, WfsNodeInfo node, bool isDeleted)
    {
        var metadata = node.Metadata;
        var isDirectory = metadata.IsDirectory;
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = path,
            Name = isDeleted ? $"_{name.TrimStart('_')}" : name,
            Kind = isDirectory ? "Directory" : "File",
            IsDirectory = isDirectory,
            Length = isDirectory ? 0 : metadata.FileSize,
            Offset = node.MetadataBlock.PhysicalBlock * (long)PhysicalBlockSize + node.MetadataOffset,
            Cluster = metadata.DirectoryBlockNumber,
            IsDeleted = isDeleted,
            Attributes = $"flags 0x{metadata.Flags:X8}, mode 0x{metadata.Mode:X8}, owner 0x{metadata.Owner:X8}, group 0x{metadata.Group:X8}",
            MetadataStatus = isDeleted ? "Unreferenced Wii U WFS metadata candidate" : "Active Wii U WFS metadata entry",
            Extents = []
        };
    }

    private IEnumerable<byte[]> ReadFileChunks(WfsNodeInfo node, CancellationToken cancellationToken)
    {
        var metadata = node.Metadata;
        var allocation = ReadMetadataAllocation(node.MetadataBlock.Data, node.MetadataOffset, metadata);
        var remaining = (long)metadata.FileSize;
        if (metadata.SizeCategory == 0)
        {
            var baseSize = metadata.MetadataBaseSize;
            var readable = Math.Min(remaining, Math.Max(0, allocation.Length - baseSize));
            if (readable > 0)
            {
                yield return allocation.AsSpan(baseSize, checked((int)readable)).ToArray();
            }

            yield break;
        }

        foreach (var chunk in metadata.SizeCategory switch
                 {
                     1 => ReadCategory1Or2(node, allocation, typeLog2: 0, cancellationToken),
                     2 => ReadCategory1Or2(node, allocation, typeLog2: 3, cancellationToken),
                     3 => ReadCategory3(node, allocation, cancellationToken),
                     4 => ReadCategory4(node, allocation, cancellationToken),
                     _ => []
                 })
        {
            if (remaining <= 0)
            {
                yield break;
            }

            var writable = checked((int)Math.Min(chunk.Length, remaining));
            remaining -= writable;
            yield return writable == chunk.Length ? chunk : chunk[..writable];
        }
    }

    private IEnumerable<byte[]> ReadCategory1Or2(WfsNodeInfo node, byte[] allocation, int typeLog2, CancellationToken cancellationToken)
    {
        var metadata = node.Metadata;
        var dataBlockLog2 = node.Area.BlockLog2 + typeLog2;
        var itemCount = DivCeil(metadata.SizeOnDisk, 1U << dataBlockLog2);
        long yieldedBytes = 0;
        foreach (var itemOffset in EnumerateReversedMetadataItems(metadata, itemCount, 0x18))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (itemOffset + 4 > allocation.Length)
            {
                yield break;
            }

            var blockNumber = BinaryPrimitives.ReadUInt32BigEndian(allocation.AsSpan(itemOffset));
            var maximumBlockSize = 1U << dataBlockLog2;
            var dataSize = (uint)Math.Min((long)maximumBlockSize, Math.Max(0, metadata.FileSize - yieldedBytes));
            if (dataSize == 0)
            {
                yield break;
            }

            yield return LoadDataBlock(node.Area, blockNumber, dataSize, metadata.IsEncrypted);
            yieldedBytes += dataSize;
        }
    }

    private IEnumerable<byte[]> ReadCategory3(WfsNodeInfo node, byte[] allocation, CancellationToken cancellationToken)
    {
        var metadata = node.Metadata;
        var clusterDataLog2 = node.Area.BlockLog2 + 6;
        var clusterCount = DivCeil(metadata.SizeOnDisk, 1U << clusterDataLog2);
        long yieldedBytes = 0;
        foreach (var itemOffset in EnumerateReversedMetadataItems(metadata, clusterCount, 0xA4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (itemOffset + 4 > allocation.Length)
            {
                yield break;
            }

            var clusterBlockNumber = BinaryPrimitives.ReadUInt32BigEndian(allocation.AsSpan(itemOffset));
            foreach (var chunk in ReadClusterDataBlocks(node.Area, metadata, clusterBlockNumber, ref yieldedBytes, cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    private IEnumerable<byte[]> ReadCategory4(WfsNodeInfo node, byte[] allocation, CancellationToken cancellationToken)
    {
        var metadata = node.Metadata;
        var clusterDataLog2 = node.Area.BlockLog2 + 6;
        var clusterCount = DivCeil(metadata.SizeOnDisk, 1U << clusterDataLog2);
        var clustersInBlock = Math.Min(48, ((1 << node.Area.BlockLog2) - MetadataHeaderSize) / 0xA4);
        var metadataBlockCount = DivCeil(clusterCount, (uint)clustersInBlock);
        long yieldedBytes = 0;
        foreach (var itemOffset in EnumerateReversedMetadataItems(metadata, metadataBlockCount, 4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (itemOffset + 4 > allocation.Length)
            {
                yield break;
            }

            var metadataBlockNumber = BinaryPrimitives.ReadUInt32BigEndian(allocation.AsSpan(itemOffset));
            var block = LoadMetadataBlock(node.Area, metadataBlockNumber);
            for (var clusterIndex = 0; clusterIndex < clustersInBlock && yieldedBytes < metadata.FileSize; clusterIndex++)
            {
                var clusterMetadataOffset = MetadataHeaderSize + clusterIndex * 0xA4;
                if (clusterMetadataOffset + 4 > block.Data.Length)
                {
                    yield break;
                }

                var clusterBlockNumber = BinaryPrimitives.ReadUInt32BigEndian(block.Data.AsSpan(clusterMetadataOffset));
                foreach (var chunk in ReadClusterDataBlocks(node.Area, metadata, clusterBlockNumber, ref yieldedBytes, cancellationToken))
                {
                    yield return chunk;
                }
            }
        }
    }

    private IReadOnlyList<byte[]> ReadClusterDataBlocks(WfsArea area, WfsEntryMetadata metadata, uint clusterBlockNumber, ref long yieldedBytes, CancellationToken cancellationToken)
    {
        var chunks = new List<byte[]>();
        var largeBlockLog2 = area.BlockLog2 + 3;
        for (var largeBlockIndex = 0; largeBlockIndex < 8 && yieldedBytes < metadata.FileSize; largeBlockIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = metadata.FileSize - yieldedBytes;
            var dataSize = (uint)Math.Min((long)(1U << largeBlockLog2), remaining);
            var blockNumber = clusterBlockNumber + (uint)(largeBlockIndex << 3);
            yieldedBytes += dataSize;
            chunks.Add(LoadDataBlock(area, blockNumber, dataSize, metadata.IsEncrypted));
        }

        return chunks;
    }

    private byte[] LoadDataBlock(WfsArea area, uint areaBlockNumber, uint dataSize, bool encrypted)
    {
        var physicalBlock = area.AreaBlockToPhysicalBlock(areaBlockNumber);
        var alignedSize = checked((int)AlignUp(dataSize, SectorSize));
        var encryptedData = new byte[alignedSize];
        using var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        if (!GenericFileSystemImage.ReadExactly(stream, physicalBlock * (long)PhysicalBlockSize, encryptedData))
        {
            return [];
        }

        var data = encrypted
            ? DecryptBlock(encryptedData, _keyMaterial.MlcKey, Length, CalcIv(area, physicalBlock))
            : encryptedData;
        return data.Length == dataSize ? data : data[..checked((int)dataSize)];
    }

    private byte[] ReadMetadataAllocation(ReadOnlySpan<byte> block, ushort offset, WfsEntryMetadata metadata)
    {
        var length = 1 << Math.Clamp((int)metadata.MetadataLog2Size, 6, 20);
        if (offset + length > block.Length)
        {
            length = block.Length - offset;
        }

        return block.Slice(offset, Math.Max(0, length)).ToArray();
    }

    private IEnumerable<int> EnumerateReversedMetadataItems(WfsEntryMetadata metadata, uint itemCount, int itemSize)
    {
        var end = AlignPowerOfTwo(metadata.MetadataBaseSize + checked((int)itemCount) * itemSize);
        for (var index = 0; index < itemCount; index++)
        {
            yield return end - itemSize * (index + 1);
        }
    }

    private IEnumerable<WfsBlock> EnumerateDirectoryLeafBlocks(WfsArea area, WfsBlock block)
    {
        var flags = BinaryPrimitives.ReadUInt32BigEndian(block.Data);
        if ((flags & DirectoryLeafTreeFlag) != 0)
        {
            yield return block;
            yield break;
        }

        foreach (var pair in EnumerateDirectoryTree(block.Data, leafValueSize: 4))
        {
            WfsBlock childBlock;
            try
            {
                childBlock = LoadMetadataBlock(area, pair.Value);
            }
            catch
            {
                continue;
            }

            foreach (var leaf in EnumerateDirectoryLeafBlocks(area, childBlock))
            {
                yield return leaf;
            }
        }
    }

    private WfsBlock LoadMetadataBlock(WfsArea area, uint areaBlockNumber, int? blockLog2Override = null)
    {
        var blockLog2 = blockLog2Override ?? area.BlockLog2;
        var physicalBlock = area.AreaBlockToPhysicalBlock(areaBlockNumber);
        var key = (physicalBlock, blockLog2);
        if (_loadedBlocks.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var blockSize = 1 << blockLog2;
        var encrypted = new byte[blockSize];
        using var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        if (!GenericFileSystemImage.ReadExactly(stream, physicalBlock * (long)PhysicalBlockSize, encrypted))
        {
            throw new InvalidDataException($"Could not read WFS metadata block 0x{areaBlockNumber:X}.");
        }

        var data = DecryptBlock(encrypted, _keyMaterial.MlcKey, Length, CalcIv(area, physicalBlock));
        var block = new WfsBlock(physicalBlock, blockLog2, data);
        _loadedBlocks[key] = block;
        return block;
    }

    private uint CalcIv(WfsArea area, uint physicalBlock)
    {
        unchecked
        {
            return (area.Iv ^ _deviceIv) + ((physicalBlock - area.PhysicalBlock) << 3);
        }
    }

    private static bool TryParseDeviceHeader(ReadOnlySpan<byte> block, out uint deviceIv, out ushort deviceType)
    {
        deviceIv = 0;
        deviceType = 0;
        if (block.Length < MetadataHeaderSize + DeviceHeaderSize + AreaHeaderSize)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(block[0x1C..]);
        deviceType = BinaryPrimitives.ReadUInt16BigEndian(block[0x20..]);
        if (version != WfsVersion || deviceType is not (0x1281 or 0x136A or 0x16A2))
        {
            return false;
        }

        deviceIv = BinaryPrimitives.ReadUInt32BigEndian(block[0x18..]);
        return true;
    }

    private static WfsArea ReadArea(ReadOnlySpan<byte> block, uint physicalBlock, bool isRootArea)
    {
        var offset = MetadataHeaderSize + (isRootArea ? DeviceHeaderSize : 0);
        if (offset + AreaHeaderSize > block.Length)
        {
            throw new InvalidDataException("WFS area header is truncated.");
        }

        return new WfsArea(
            physicalBlock,
            BinaryPrimitives.ReadUInt32BigEndian(block[offset..]),
            BinaryPrimitives.ReadUInt32BigEndian(block[(offset + 8)..]),
            block[offset + 21]);
    }

    private static IReadOnlyList<WfsTreePair> EnumerateDirectoryTree(ReadOnlySpan<byte> block, int leafValueSize)
    {
        var rows = new List<WfsTreePair>();
        if (block.Length < DirectoryTreeHeaderOffset + 4)
        {
            return rows;
        }

        var rootOffset = BinaryPrimitives.ReadUInt16BigEndian(block[DirectoryTreeHeaderOffset..]);
        var recordsCount = BinaryPrimitives.ReadUInt16BigEndian(block[(DirectoryTreeHeaderOffset + 2)..]);
        if (rootOffset == 0 || recordsCount == 0)
        {
            return rows;
        }

        VisitTreeNode(block, rootOffset, string.Empty, leafValueSize, rows, new HashSet<ushort>());
        return rows;
    }

    private static void VisitTreeNode(ReadOnlySpan<byte> block, ushort offset, string prefix, int leafValueSize, List<WfsTreePair> rows, HashSet<ushort> visited)
    {
        if (offset <= 0 || offset + 2 > block.Length || !visited.Add(offset))
        {
            return;
        }

        var prefixLength = block[offset];
        var keyCount = block[offset + 1];
        if (prefixLength > 128 || keyCount > 128 || offset + 2 + prefixLength + keyCount > block.Length)
        {
            return;
        }

        var nodePrefix = Encoding.ASCII.GetString(block.Slice(offset + 2, prefixLength));
        var hasLeaf = keyCount > 0 && block[offset + 2 + prefixLength] == 0;
        var nodeSize = CalcNodeSize(prefixLength, keyCount, hasLeaf, leafValueSize);
        if (offset + nodeSize > block.Length)
        {
            return;
        }

        if (hasLeaf)
        {
            rows.Add(new WfsTreePair(prefix + nodePrefix, ReadTreeValue(block, offset, nodeSize, 0, true, leafValueSize)));
        }

        for (var index = hasLeaf ? 1 : 0; index < keyCount; index++)
        {
            var key = (char)block[offset + 2 + prefixLength + index];
            var childOffset = (ushort)ReadTreeValue(block, offset, nodeSize, index, hasLeaf, leafValueSize);
            VisitTreeNode(block, childOffset, prefix + nodePrefix + key, leafValueSize, rows, visited);
        }
    }

    private static uint ReadTreeValue(ReadOnlySpan<byte> block, int offset, int nodeSize, int index, bool hasLeaf, int leafValueSize)
    {
        if (hasLeaf && index == 0 && leafValueSize == 4)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(block.Slice(offset + nodeSize - 4, 4));
        }

        var valueBase = offset + nodeSize - 2;
        if (hasLeaf && leafValueSize == 4)
        {
            valueBase -= 2;
        }

        return BinaryPrimitives.ReadUInt16BigEndian(block.Slice(valueBase - index * 2, 2));
    }

    private static int CalcNodeSize(int prefixLength, int keyCount, bool hasLeaf, int leafValueSize)
    {
        var size = 2 + prefixLength + keyCount * 3;
        if (hasLeaf && leafValueSize != 2)
        {
            size += leafValueSize - 2;
        }

        return AlignPowerOfTwo(size);
    }

    private static WfsEntryMetadata ReadEntryMetadata(ReadOnlySpan<byte> block, int offset)
    {
        if (offset < 0 || offset + EntryMetadataSize > block.Length)
        {
            return default;
        }

        return new WfsEntryMetadata(
            BinaryPrimitives.ReadUInt32BigEndian(block[offset..]),
            BinaryPrimitives.ReadUInt32BigEndian(block[(offset + 4)..]),
            BinaryPrimitives.ReadUInt32BigEndian(block[(offset + 20)..]),
            BinaryPrimitives.ReadUInt32BigEndian(block[(offset + 24)..]),
            BinaryPrimitives.ReadUInt32BigEndian(block[(offset + 28)..]),
            BinaryPrimitives.ReadUInt32BigEndian(block[(offset + 32)..]),
            BinaryPrimitives.ReadUInt32BigEndian(block[(offset + 36)..]),
            block[offset + 40],
            block[offset + 41],
            block[offset + 42],
            block[offset + 43]);
    }

    private static byte[] DecryptBlock(byte[] encrypted, byte[] key, long deviceLength, uint iv)
    {
        var data = encrypted.ToArray();
        var aesIv = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(aesIv.AsSpan(0), (uint)encrypted.Length);
        BinaryPrimitives.WriteUInt32BigEndian(aesIv.AsSpan(4), iv);
        BinaryPrimitives.WriteUInt32BigEndian(aesIv.AsSpan(8), (uint)Math.Max(0, deviceLength / SectorSize));
        BinaryPrimitives.WriteUInt32BigEndian(aesIv.AsSpan(12), SectorSize);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor(key, aesIv);
        decryptor.TransformBlock(data, 0, data.Length, data, 0);
        return data;
    }

    private static long AlignUp(long value, int alignment)
    {
        return ((value + alignment - 1) / alignment) * alignment;
    }

    private static int AlignPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return Math.Max(8, result);
    }

    private static uint DivCeil(uint value, uint divisor)
    {
        return divisor == 0 ? 0 : (value + divisor - 1) / divisor;
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

    private readonly record struct WfsArea(uint PhysicalBlock, uint Iv, uint RootDirectoryBlockNumber, int BlockLog2)
    {
        public uint AreaBlockToPhysicalBlock(uint areaBlockNumber)
        {
            return PhysicalBlock + (areaBlockNumber << (BlockLog2 - 12));
        }
    }

    private sealed record WfsBlock(uint PhysicalBlock, int BlockLog2, byte[] Data);

    private sealed record WfsNodeInfo(WfsArea Area, WfsBlock MetadataBlock, ushort MetadataOffset, WfsEntryMetadata Metadata, string Name);

    private readonly record struct WfsTreePair(string Name, uint Value);

    private readonly record struct WfsEntryMetadata(
        uint Flags,
        uint SizeOnDisk,
        uint FileSize,
        uint DirectoryBlockNumber,
        uint Owner,
        uint Group,
        uint Mode,
        byte MetadataLog2Size,
        byte SizeCategory,
        byte FilenameLength,
        byte CaseBitmap)
    {
        public bool IsDirectory => (Flags & DirectoryFlag) != 0;

        public bool IsQuota => (Flags & QuotaFlag) != 0;

        public bool HasRegularAreaSize => (Flags & AreaSizeRegularFlag) != 0;

        public bool IsEncrypted => (Flags & UnencryptedFileFlag) == 0;

        public int MetadataBaseSize => 0x2B + (FilenameLength + 7) / 8;

        public bool IsPlausible
        {
            get
            {
                if (MetadataLog2Size is < 6 or > 20 || SizeCategory > 4)
                {
                    return false;
                }

                if (IsDirectory)
                {
                    return DirectoryBlockNumber > 0 || IsQuota;
                }

                return FileSize <= SizeOnDisk || SizeCategory == 0;
            }
        }

        public string GetCaseSensitiveName(string lowerName)
        {
            if (string.IsNullOrEmpty(lowerName) || FilenameLength == 0 || FilenameLength != lowerName.Length)
            {
                return lowerName;
            }

            var chars = lowerName.ToCharArray();
            for (var index = 0; index < chars.Length; index++)
            {
                if (((CaseBitmap >> (index % 8)) & 1) != 0)
                {
                    chars[index] = char.ToUpperInvariant(chars[index]);
                }
            }

            return new string(chars);
        }
    }
}
