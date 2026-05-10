using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FATXTools.Wpf;

internal sealed class WiiNandVolume : GenericFileSystemVolume
{
    private const int PageSize = 2048;
    private const int SpareSize = 64;
    private const int PagesPerCluster = 8;
    private const int ClusterBytes = PageSize * PagesPerCluster;
    private const int RawPageBytes = PageSize + SpareSize;
    private const int ClusterCount = 0x8000;
    private const int SffsStartCluster = 0x7F00;
    private const int SffsSuperblockStep = 0x10;
    private const int SffsSuperblockCount = 0x10;
    private const int FatOffset = 0x0C;
    private const int FatLength = ClusterCount * 2;
    private const int FstOffset = FatOffset + FatLength;
    private const int FstEntrySize = 0x20;
    private const ushort FatEndOfChain = 0xFFFB;
    private const ushort FatEmpty = 0xFFFE;
    private const ushort EmptyNode = 0xFFFF;
    private const byte NodeFile = 1;
    private const byte NodeDirectory = 2;
    private static readonly HashSet<string> KnownRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sys",
        "ticket",
        "title",
        "shared1",
        "shared2",
        "tmp",
        "import",
        "meta"
    };

    private readonly WiiNandLayout _layout;
    private readonly byte[]? _nandKey;
    private readonly ushort[] _fat;
    private readonly IReadOnlyList<WiiSffsNode> _nodes;
    private readonly HashSet<int> _activeNodeIndexes = [];
    private readonly List<GenericFileSystemEntry> _root = [];

    private WiiNandVolume(
        string sourcePath,
        GenericPartitionCandidate partition,
        WiiNandLayout layout,
        byte[]? nandKey,
        ushort[] fat,
        IReadOnlyList<WiiSffsNode> nodes)
        : base(sourcePath, partition, "Nintendo Wii NAND SFFS")
    {
        _layout = layout;
        _nandKey = nandKey;
        _fat = fat;
        _nodes = nodes;
        LoadChildren(0, "/", _root, new HashSet<int>());
    }

    public bool HasNandKey => _nandKey?.Length == 16;

    public override long ClusterSize => ClusterBytes;

    public override long UsedSpace => Walk(_root).Where(entry => !entry.IsDirectory).Sum(entry => entry.Length);

    public static bool TryOpen(string sourcePath, string? keyPath, out WiiNandVolume volume, out string status)
    {
        volume = null!;
        status = string.Empty;
        var length = new FileInfo(sourcePath).Length;
        if (!TryDetectLayout(length, out var layout))
        {
            status = "Not a supported 512 MiB Wii NAND dump layout.";
            return false;
        }

        WiiKeyMaterial.TryLoad(keyPath, out var keyMaterial, out var keyStatus);
        try
        {
            var superblock = ReadBestSuperblock(sourcePath, layout, out var generation);
            if (superblock.Length == 0)
            {
                status = $"Wii NAND layout detected, but no SFFS superblock was found. {keyStatus}";
                return false;
            }

            var fat = ReadFat(superblock);
            var nodes = ReadNodes(superblock);
            if (nodes.Count == 0 || (nodes[0].Mode & 3) != NodeDirectory)
            {
                status = $"Wii NAND SFFS metadata did not contain a valid root node. {keyStatus}";
                return false;
            }

            var candidate = new GenericPartitionCandidate(0, Guid.Empty, 0, length, Path.GetFileName(sourcePath), 512);
            volume = new WiiNandVolume(sourcePath, candidate, layout, keyMaterial?.NandKey, fat, nodes);
            var keyText = volume.HasNandKey ? "NAND key loaded; file export decrypts clusters" : "NAND key unavailable; metadata browsing works, file export returns encrypted cluster bytes";
            status = $"Mounted Wii NAND SFFS, generation {generation}, {volume.GetRoot().Count:N0} root entries, {layout.Description}. {keyText}.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException or CryptographicException)
        {
            status = $"Wii NAND mount failed: {ex.Message}. {keyStatus}";
            return false;
        }
    }

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var rows = new List<GenericFileSystemEntry>();
        for (var index = 1; index < _nodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_activeNodeIndexes.Contains(index))
            {
                continue;
            }

            var node = _nodes[index];
            if (!node.IsPlausible)
            {
                continue;
            }

            var entry = CreateEntry(node, index, "/$DELETED/" + node.Name, isDeleted: true);
            rows.Add(entry);
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

        using var output = File.Create(destinationPath);
        var remaining = entry.Length;
        foreach (var cluster in EnumerateClusterChain((ushort)entry.Cluster).Take(ClusterCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0)
            {
                break;
            }

            var data = ReadCluster(cluster);
            if (HasNandKey && cluster is >= 0x40 and < SffsStartCluster)
            {
                DecryptClusterInPlace(data);
            }

            var writable = (int)Math.Min(data.Length, remaining);
            output.Write(data, 0, writable);
            remaining -= writable;
            progress?.Invoke(writable);
        }
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return _layout.ClusterToPhysicalOffset((int)cluster);
    }

    private static bool TryDetectLayout(long length, out WiiNandLayout layout)
    {
        if (length == 512L * 1024 * 1024)
        {
            layout = WiiNandLayout.RawNoSpare;
            return true;
        }

        if (length == ClusterCount * PagesPerCluster * (long)RawPageBytes
            || length == ClusterCount * PagesPerCluster * (long)RawPageBytes + 1024)
        {
            layout = WiiNandLayout.WithSpare;
            return true;
        }

        layout = default;
        return false;
    }

    private static byte[] ReadBestSuperblock(string sourcePath, WiiNandLayout layout, out uint generation)
    {
        generation = 0;
        var best = Array.Empty<byte>();
        for (var index = 0; index < SffsSuperblockCount; index++)
        {
            var cluster = SffsStartCluster + index * SffsSuperblockStep;
            var data = ReadClusters(sourcePath, layout, cluster, SffsSuperblockStep);
            if (!data.AsSpan(0, 4).SequenceEqual("SFFS"u8))
            {
                continue;
            }

            var currentGeneration = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
            if (!HasValidRootNode(data))
            {
                continue;
            }

            if (best.Length == 0 || currentGeneration >= generation)
            {
                generation = currentGeneration;
                best = data;
            }
        }

        return best;
    }

    private static bool HasValidRootNode(ReadOnlySpan<byte> superblock)
    {
        var root = ReadNode(superblock.Slice(FstOffset, FstEntrySize));
        return root.Name == "/" && (root.Mode & 3) == NodeDirectory;
    }

    private static ushort[] ReadFat(ReadOnlySpan<byte> superblock)
    {
        var fat = new ushort[ClusterCount];
        for (var index = 0; index < fat.Length; index++)
        {
            fat[index] = BinaryPrimitives.ReadUInt16BigEndian(superblock.Slice(FatOffset + index * 2, 2));
        }

        return fat;
    }

    private static IReadOnlyList<WiiSffsNode> ReadNodes(ReadOnlySpan<byte> superblock)
    {
        var rows = new List<WiiSffsNode>();
        var maxEntries = (superblock.Length - FstOffset) / FstEntrySize;
        for (var index = 0; index < maxEntries; index++)
        {
            var entry = superblock.Slice(FstOffset + index * FstEntrySize, FstEntrySize);
            rows.Add(ReadNode(entry));
        }

        return rows;
    }

    private static WiiSffsNode ReadNode(ReadOnlySpan<byte> entry)
    {
        return new WiiSffsNode(
            ReadName(entry[..0x0C]),
            entry[0x0C],
            entry[0x0D],
            BinaryPrimitives.ReadUInt16BigEndian(entry[0x0E..]),
            BinaryPrimitives.ReadUInt16BigEndian(entry[0x10..]),
            BinaryPrimitives.ReadUInt32BigEndian(entry[0x12..]),
            BinaryPrimitives.ReadUInt32BigEndian(entry[0x16..]),
            BinaryPrimitives.ReadUInt16BigEndian(entry[0x1A..]),
            BinaryPrimitives.ReadUInt32BigEndian(entry[0x1C..]));
    }

    private void LoadChildren(int parentIndex, string parentPath, List<GenericFileSystemEntry> rows, HashSet<int> branch)
    {
        if (parentIndex < 0 || parentIndex >= _nodes.Count || !branch.Add(parentIndex))
        {
            return;
        }

        _activeNodeIndexes.Add(parentIndex);
        var childIndex = _nodes[parentIndex].Sub;
        var guard = 0;
        while (childIndex != EmptyNode && childIndex < _nodes.Count && guard++ < _nodes.Count)
        {
            var child = _nodes[childIndex];
            if (!child.IsPlausible)
            {
                break;
            }

            var path = CombinePath(parentPath, child.Name);
            var entry = CreateEntry(child, childIndex, path, isDeleted: false);
            rows.Add(entry);
            _activeNodeIndexes.Add(childIndex);
            if (entry.IsDirectory)
            {
                LoadChildren(childIndex, path, entry.Children, branch);
            }

            childIndex = child.Sibling;
        }

        if (parentIndex == 0)
        {
            AddKnownRootNodes(rows, branch);
        }

        branch.Remove(parentIndex);
    }

    private void AddKnownRootNodes(List<GenericFileSystemEntry> rows, HashSet<int> branch)
    {
        var existing = rows.Select(row => row.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < Math.Min(_nodes.Count, 256); index++)
        {
            var node = _nodes[index];
            if (!node.IsPlausible || (node.Mode & 3) != NodeDirectory || !KnownRootNames.Contains(node.Name) || !existing.Add(node.Name))
            {
                continue;
            }

            var path = CombinePath("/", node.Name);
            var entry = CreateEntry(node, index, path, isDeleted: false);
            rows.Add(entry);
            _activeNodeIndexes.Add(index);
            LoadChildren(index, path, entry.Children, branch);
        }
    }

    private GenericFileSystemEntry CreateEntry(WiiSffsNode node, int nodeIndex, string path, bool isDeleted)
    {
        var isDirectory = (node.Mode & 3) == NodeDirectory;
        var extents = isDirectory ? [] : BuildPhysicalExtents(node.Sub, node.Size);
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = path,
            Name = node.Name,
            Kind = isDirectory ? "Directory" : "File",
            IsDirectory = isDirectory,
            Length = isDirectory ? 0 : node.Size,
            Offset = extents.Count > 0 ? extents[0].Offset : 0,
            Cluster = isDirectory ? (uint)nodeIndex : node.Sub,
            IsDeleted = isDeleted,
            Attributes = $"mode 0x{node.Mode:X2}, attr 0x{node.Attributes:X2}, uid 0x{node.Uid:X8}, gid 0x{node.Gid:X4}",
            MetadataStatus = isDeleted ? "Unreferenced Wii SFFS node candidate" : "Active Wii SFFS metadata entry",
            Extents = extents
        };
    }

    private List<FileExtent> BuildPhysicalExtents(ushort startCluster, uint size)
    {
        var extents = new List<FileExtent>();
        var remaining = (long)size;
        foreach (var cluster in EnumerateClusterChain(startCluster).Take(ClusterCount))
        {
            if (remaining <= 0)
            {
                break;
            }

            if (_layout.HasSpare)
            {
                for (var page = 0; page < PagesPerCluster && remaining > 0; page++)
                {
                    var pageOffset = _layout.ClusterToPhysicalOffset(cluster) + page * RawPageBytes;
                    var length = Math.Min(PageSize, remaining);
                    extents.Add(new FileExtent(pageOffset, length));
                    remaining -= length;
                }
            }
            else
            {
                var length = Math.Min(ClusterBytes, remaining);
                extents.Add(new FileExtent(_layout.ClusterToPhysicalOffset(cluster), length));
                remaining -= length;
            }
        }

        return extents;
    }

    private IEnumerable<ushort> EnumerateClusterChain(ushort startCluster)
    {
        var seen = new HashSet<ushort>();
        var cluster = startCluster;
        while (cluster < ClusterCount && seen.Add(cluster))
        {
            yield return cluster;
            var next = _fat[cluster];
            if (next is FatEndOfChain or FatEmpty || next >= ClusterCount)
            {
                yield break;
            }

            cluster = next;
        }
    }

    private byte[] ReadCluster(ushort cluster)
    {
        return ReadClusters(SourcePath, _layout, cluster, 1);
    }

    private static byte[] ReadClusters(string sourcePath, WiiNandLayout layout, int firstCluster, int count)
    {
        var result = new byte[count * ClusterBytes];
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        for (var clusterOffset = 0; clusterOffset < count; clusterOffset++)
        {
            var cluster = firstCluster + clusterOffset;
            if (layout.HasSpare)
            {
                for (var page = 0; page < PagesPerCluster; page++)
                {
                    var sourceOffset = layout.ClusterToPhysicalOffset(cluster) + page * RawPageBytes;
                    GenericFileSystemImage.ReadExactly(stream, sourceOffset, result.AsSpan(clusterOffset * ClusterBytes + page * PageSize, PageSize));
                }
            }
            else
            {
                GenericFileSystemImage.ReadExactly(stream, layout.ClusterToPhysicalOffset(cluster), result.AsSpan(clusterOffset * ClusterBytes, ClusterBytes));
            }
        }

        return result;
    }

    private void DecryptClusterInPlace(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = _nandKey!;
        aes.IV = new byte[16];
        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(data, 0, data.Length);
        Buffer.BlockCopy(plain, 0, data, 0, data.Length);
    }

    private static string ReadName(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        if (end >= 0)
        {
            data = data[..end];
        }

        return Encoding.ASCII.GetString(data).Trim();
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

    private readonly record struct WiiNandLayout(bool HasSpare, string Description)
    {
        public static WiiNandLayout RawNoSpare { get; } = new(false, "raw 512 MiB logical dump");

        public static WiiNandLayout WithSpare { get; } = new(true, "BootMii/page-spare dump");

        public long ClusterToPhysicalOffset(int cluster)
        {
            return HasSpare ? cluster * PagesPerCluster * (long)RawPageBytes : cluster * (long)ClusterBytes;
        }
    }

    private readonly record struct WiiSffsNode(
        string Name,
        byte Mode,
        byte Attributes,
        ushort Sub,
        ushort Sibling,
        uint Size,
        uint Uid,
        ushort Gid,
        uint Unknown)
    {
        public bool IsPlausible
        {
            get
            {
                var type = Mode & 3;
                if (type is not (NodeFile or NodeDirectory) || string.IsNullOrWhiteSpace(Name))
                {
                    return false;
                }

                return Name.All(value => value is >= ' ' and <= '~' and not '/' and not '\\');
            }
        }
    }
}
