using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace FATXTools.Wpf;

internal static class ManagedPs3StorageImage
{
    private const int SectorSize = 512;
    private const ulong Magic1 = 0x0FACE0FFUL;
    private const ulong Magic2 = 0xDEADFACEUL;
    private const int DiskLabelSize = 0x30;
    private const int PartitionSize = 0x90;

    private static readonly byte[] EncDecSeed00 =
    [
        0xE2, 0xD0, 0x5D, 0x40, 0x71, 0x94, 0x5B, 0x01, 0xC3, 0x6D, 0x51, 0x51, 0xE8, 0x8C, 0xB8, 0x33,
        0x4A, 0xAA, 0x29, 0x80, 0x81, 0xD8, 0xC4, 0x4F, 0x18, 0x5D, 0xC6, 0x60, 0xED, 0x57, 0x56, 0x86
    ];

    private static readonly byte[] EncDecSeed20 =
    [
        0x02, 0x08, 0x32, 0x92, 0xC3, 0x05, 0xD5, 0x38, 0xBC, 0x50, 0xE6, 0x99, 0x71, 0x0C, 0x0A, 0x3E,
        0x55, 0xF5, 0x1C, 0xBA, 0xA5, 0x35, 0xA3, 0x80, 0x30, 0xB6, 0x7F, 0x79, 0xC9, 0x05, 0xBD, 0xA3
    ];

    private static readonly byte[] SbIndivSeed00 =
    [
        0xD9, 0x2D, 0x65, 0xDB, 0x05, 0x7D, 0x49, 0xE1, 0xA6, 0x6F, 0x22, 0x74, 0xB8, 0xBA, 0xC5, 0x08,
        0x83, 0x84, 0x4E, 0xD7, 0x56, 0xCA, 0x79, 0x51, 0x63, 0x62, 0xEA, 0x8A, 0xDA, 0xC6, 0x03, 0x26
    ];

    private static readonly byte[] SbIndivSeed20 =
    [
        0xC3, 0xB3, 0xB5, 0xAA, 0xCC, 0x74, 0xCD, 0x6A, 0x48, 0xEF, 0xAB, 0xF4, 0x4D, 0xCD, 0xF1, 0x6E,
        0x37, 0x9F, 0x55, 0xF5, 0x77, 0x7D, 0x09, 0xFB, 0xEE, 0xDE, 0x07, 0x05, 0x8E, 0x94, 0xBE, 0x08
    ];

    public static bool TryOpen(string imagePath, string keyPath, out PlayStationStorageImage image, out string? error)
    {
        image = null!;
        error = null;

        try
        {
            if (!File.Exists(imagePath))
            {
                error = "Image file does not exist.";
                return false;
            }

            var key = File.Exists(keyPath) ? File.ReadAllBytes(keyPath) : [];
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);

            Ps3CryptoKeys? cryptoKeys = null;
            var mode = Ps3DiskCryptoMode.Plaintext;
            var header = ReadDiskHeader(stream, 0x2000, mode, cryptoKeys);
            if (!HasDiskLabel(header.AsSpan(0, SectorSize)))
            {
                if (key.Length < 0x30)
                {
                    error = "PS3 encrypted HDD images require an EID root key with at least 0x30 bytes. Already-decrypted images can be opened without a key.";
                    return false;
                }

                cryptoKeys = Ps3CryptoKeys.Generate(key);
                mode = Ps3DiskCryptoMode.PhatAtaCbcSwapped;
                header = ReadDiskHeader(stream, 0x2000, mode, cryptoKeys);
                if (!HasDiskLabel(header.AsSpan(0, SectorSize)))
                {
                    mode = Ps3DiskCryptoMode.SlimAtaXtsSwapped;
                    header = ReadDiskHeader(stream, 0x2000, mode, cryptoKeys);
                }
            }

            if (!HasDiskLabel(header.AsSpan(0, SectorSize)))
            {
                error = "No PS3 Cell disklabel was found.";
                return false;
            }

            var partitions = BuildPartitions(stream, header, mode, cryptoKeys);
            if (partitions.Count == 0)
            {
                error = "PS3 Cell disklabel was detected, but no supported partitions were found.";
                return false;
            }

            var operations = new ManagedPs3VolumeOperations(partitions, mode, cryptoKeys);
            var volumes = partitions
                .Select(partition => new PlayStationVolume(imagePath, keyPath, partition.Name, checked((long)partition.Offset), checked((long)partition.Length), operations))
                .ToList();

            foreach (var volume in volumes)
            {
                volume.TryLoad();
            }

            if (!volumes.Any(volume => volume.IsLoaded))
            {
                error = volumes.FirstOrDefault(volume => !string.IsNullOrWhiteSpace(volume.LoadError))?.LoadError
                        ?? "No managed PS3 partitions could be indexed.";
                return false;
            }

            image = new PlayStationStorageImage(imagePath, keyPath, volumes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or ArgumentException or OverflowException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static byte[] ReadDiskHeader(FileStream stream, int length, Ps3DiskCryptoMode mode, Ps3CryptoKeys? keys)
    {
        var buffer = new byte[length];
        if (!ManagedPs4StorageImage.ReadExactly(stream, 0, buffer))
        {
            throw new EndOfStreamException("Could not read PS3 disk header.");
        }

        ApplyDiskCrypto(buffer, 0, mode, keys);
        return buffer;
    }

    private static List<ManagedPs3Partition> BuildPartitions(FileStream stream, byte[] header, Ps3DiskCryptoMode mode, Ps3CryptoKeys? keys)
    {
        var rows = new List<ManagedPs3Partition>();
        var imageLength = stream.Length;
        var entries = ParsePartitionTable(header.AsSpan(DiskLabelSize));
        var hasVflash = mode == Ps3DiskCryptoMode.SlimAtaXtsSwapped || HasNestedVflashLabel(header, mode, keys);
        var hdd0Index = hasVflash ? 1 : 0;
        var hdd1Index = hasVflash ? 2 : 1;

        if (hasVflash && entries.Count > 0)
        {
            rows.AddRange(BuildVflashPartitions(stream, entries[0], mode, keys));
        }

        AddPartition(rows, imageLength, "dev_hdd0", entries.ElementAtOrDefault(hdd0Index), Ps3FileSystem.Ufs2, bigEndian: true, extraVflashCrypto: false);
        AddPartition(rows, imageLength, "dev_hdd1", entries.ElementAtOrDefault(hdd1Index), Ps3FileSystem.Fat, bigEndian: false, extraVflashCrypto: false);
        return rows;
    }

    private static IReadOnlyList<ManagedPs3Partition> BuildVflashPartitions(FileStream stream, Ps3RawPartition vflash, Ps3DiskCryptoMode mode, Ps3CryptoKeys? keys)
    {
        if (vflash.Size == 0 || keys == null && mode != Ps3DiskCryptoMode.Plaintext)
        {
            return [];
        }

        var imageLength = stream.Length;
        var baseSector = vflash.Start;
        var table = ReadAbsolute(stream, checked((long)(baseSector * SectorSize)), 0x1000, mode, keys);
        if (mode != Ps3DiskCryptoMode.Plaintext)
        {
            ApplyVflashCrypto(table, baseSector, keys);
        }

        if (!HasDiskLabel(table))
        {
            return [];
        }

        var rows = new List<ManagedPs3Partition>();
        var entries = ParsePartitionTable(table.AsSpan(DiskLabelSize));
        AddPartition(rows, imageLength, "dev_flash1", OffsetPartition(baseSector, entries.ElementAtOrDefault(1)), Ps3FileSystem.Fat, bigEndian: false, extraVflashCrypto: mode != Ps3DiskCryptoMode.Plaintext);
        AddPartition(rows, imageLength, "dev_flash2", OffsetPartition(baseSector, entries.ElementAtOrDefault(2)), Ps3FileSystem.Fat, bigEndian: false, extraVflashCrypto: mode != Ps3DiskCryptoMode.Plaintext);
        AddPartition(rows, imageLength, "dev_flash3", OffsetPartition(baseSector, entries.ElementAtOrDefault(3)), Ps3FileSystem.Fat, bigEndian: false, extraVflashCrypto: mode != Ps3DiskCryptoMode.Plaintext);
        return rows;
    }

    private static byte[] ReadAbsolute(FileStream stream, long offset, int length, Ps3DiskCryptoMode mode, Ps3CryptoKeys? keys)
    {
        var buffer = new byte[length];
        if (!ManagedPs4StorageImage.ReadExactly(stream, offset, buffer))
        {
            throw new EndOfStreamException("Could not read PS3 disk data.");
        }

        ManagedPs3StorageImage.ApplyDiskCrypto(buffer, (ulong)(offset / SectorSize), mode, keys);
        return buffer;
    }

    private static Ps3RawPartition OffsetPartition(ulong baseSector, Ps3RawPartition partition)
    {
        return partition.Size == 0 ? partition : partition with { Start = baseSector + partition.Start };
    }

    private static void AddPartition(List<ManagedPs3Partition> rows, long imageLength, string name, Ps3RawPartition partition, Ps3FileSystem fileSystem, bool bigEndian, bool extraVflashCrypto)
    {
        if (partition.Size == 0)
        {
            return;
        }

        var offset = checked(partition.Start * SectorSize);
        var length = checked(partition.Size * SectorSize);
        if (offset >= (ulong)Math.Max(0, imageLength))
        {
            return;
        }

        var boundedLength = Math.Min(length, (ulong)imageLength - offset);
        if (boundedLength == 0)
        {
            return;
        }

        rows.Add(new ManagedPs3Partition(name, fileSystem, offset, boundedLength, bigEndian, extraVflashCrypto));
    }

    private static bool HasNestedVflashLabel(byte[] header, Ps3DiskCryptoMode mode, Ps3CryptoKeys? keys)
    {
        var nested = header.AsSpan(0x1000, 0x1000).ToArray();
        if (mode == Ps3DiskCryptoMode.PhatAtaCbcSwapped)
        {
            ApplyVflashCrypto(nested, 8, keys);
        }

        return HasDiskLabel(nested);
    }

    private static List<Ps3RawPartition> ParsePartitionTable(ReadOnlySpan<byte> data)
    {
        var rows = new List<Ps3RawPartition>();
        for (var index = 0; index < 16 && index * PartitionSize + 16 <= data.Length; index++)
        {
            var entry = data.Slice(index * PartitionSize, PartitionSize);
            var start = BinaryPrimitives.ReadUInt64BigEndian(entry);
            var size = BinaryPrimitives.ReadUInt64BigEndian(entry.Slice(8, 8));
            rows.Add(new Ps3RawPartition(start, size));
        }

        return rows;
    }

    private static bool HasDiskLabel(ReadOnlySpan<byte> data)
    {
        return data.Length >= 0x20 &&
               BinaryPrimitives.ReadUInt64BigEndian(data.Slice(0x10, 8)) == Magic1 &&
               BinaryPrimitives.ReadUInt64BigEndian(data.Slice(0x18, 8)) == Magic2;
    }

    internal static void ApplyDiskCrypto(Span<byte> data, ulong startSector, Ps3DiskCryptoMode mode, Ps3CryptoKeys? keys)
    {
        switch (mode)
        {
            case Ps3DiskCryptoMode.Plaintext:
                return;
            case Ps3DiskCryptoMode.PhatAtaCbcSwapped:
                if (keys == null)
                {
                    throw new InvalidOperationException("PS3 ATA keys are unavailable.");
                }

                DecryptCbcSwapped(data, keys.AtaKeys);
                return;
            case Ps3DiskCryptoMode.SlimAtaXtsSwapped:
                if (keys == null)
                {
                    throw new InvalidOperationException("PS3 ATA keys are unavailable.");
                }

                DecryptXtsSwapped(data, startSector, keys.AtaKeys);
                return;
            default:
                throw new InvalidOperationException($"Unsupported PS3 crypto mode {mode}.");
        }
    }

    internal static void ApplyVflashCrypto(Span<byte> data, ulong startSector, Ps3CryptoKeys? keys)
    {
        if (keys == null)
        {
            return;
        }

        DecryptXts(data, startSector, keys.EncDecKeys);
    }

    private static void DecryptCbcSwapped(Span<byte> data, byte[] keyMaterial)
    {
        SwapWords(data);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = keyMaterial.Take(24).ToArray();
        aes.IV = new byte[16];

        for (var offset = 0; offset + SectorSize <= data.Length; offset += SectorSize)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8)) == 0)
            {
                continue;
            }

            using var decryptor = aes.CreateDecryptor();
            var sector = data.Slice(offset, SectorSize).ToArray();
            var plain = decryptor.TransformFinalBlock(sector, 0, sector.Length);
            plain.CopyTo(data[offset..]);
        }
    }

    private static void DecryptXtsSwapped(Span<byte> data, ulong startSector, byte[] keyMaterial)
    {
        SwapWords(data);
        DecryptXts(data, startSector, keyMaterial);
    }

    private static void DecryptXts(Span<byte> data, ulong startSector, byte[] keyMaterial)
    {
        using var decryptor = new AesXtsDecryptor(keyMaterial.AsSpan(0, 16).ToArray(), keyMaterial.AsSpan(0x20, 16).ToArray());
        for (var offset = 0; offset + SectorSize <= data.Length; offset += SectorSize)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8)) == 0)
            {
                continue;
            }

            decryptor.DecryptSector(data.Slice(offset, SectorSize), startSector + (ulong)(offset / SectorSize));
        }
    }

    private static void SwapWords(Span<byte> data)
    {
        for (var offset = 0; offset + 1 < data.Length; offset += 2)
        {
            (data[offset], data[offset + 1]) = (data[offset + 1], data[offset]);
        }
    }

    private static byte[] GenerateKey(byte[] eidRootKey, byte[] seed)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = eidRootKey.AsSpan(0, 16).ToArray();
        aes.IV = eidRootKey.AsSpan(0x20, 16).ToArray();
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(seed, 0, seed.Length);
    }

    internal sealed record Ps3CryptoKeys(byte[] AtaKeys, byte[] EncDecKeys)
    {
        public static Ps3CryptoKeys Generate(byte[] eidRootKey)
        {
            var ata = GenerateKey(eidRootKey, SbIndivSeed00)
                .Concat(GenerateKey(eidRootKey, SbIndivSeed20))
                .ToArray();
            var encDec = GenerateKey(eidRootKey, EncDecSeed00)
                .Concat(GenerateKey(eidRootKey, EncDecSeed20))
                .ToArray();
            return new Ps3CryptoKeys(ata, encDec);
        }
    }
}

internal sealed class ManagedPs3VolumeOperations : IPlayStationVolumeOperations
{
    private readonly Dictionary<string, ManagedPs3Partition> _partitions;
    private readonly Ps3DiskCryptoMode _mode;
    private readonly ManagedPs3StorageImage.Ps3CryptoKeys? _keys;

    public ManagedPs3VolumeOperations(IEnumerable<ManagedPs3Partition> partitions, Ps3DiskCryptoMode mode, ManagedPs3StorageImage.Ps3CryptoKeys? keys)
    {
        _partitions = partitions.ToDictionary(partition => partition.Name, StringComparer.OrdinalIgnoreCase);
        _mode = mode;
        _keys = keys;
    }

    public string FamilyText => "PlayStation 3 HDD (managed)";

    public string ListFilesJson(string imagePath, string keyPath, PlayStationVolume volume)
    {
        using var reader = OpenReader(imagePath, volume.Name);
        var entries = GetPartition(volume.Name).FileSystem switch
        {
            Ps3FileSystem.Ufs2 => ManagedPs4UfsReader.ReadEntries(reader),
            Ps3FileSystem.Fat => ManagedPs4FatReader.ReadEntries(reader),
            _ => throw new InvalidDataException($"Unsupported PS3 filesystem for {volume.Name}.")
        };

        var rows = entries.Select(entry => new
        {
            path = entry.Path,
            name = entry.Name,
            type = entry.Type,
            size = (ulong)Math.Max(0, entry.Length),
            created = FormatDate(entry.Created),
            modified = FormatDate(entry.Modified),
            accessed = FormatDate(entry.Accessed),
            dataOffsetCount = entry.DataOffsetCount,
            dataRunCount = entry.DataRunCount,
            largestRunBytes = (ulong)Math.Max(0, entry.LargestRunBytes),
            estimatedAllocationUnit = (ulong)Math.Max(0, entry.EstimatedAllocationUnit),
            fragmentationStatus = entry.FragmentationStatus,
            dataRanges = entry.DataRanges,
            dataOffsets = entry.DataOffsets,
            inodeOffsets = entry.InodeOffsets,
            direntOffsets = entry.DirentOffsets,
            blocktableOffsets = entry.BlocktableOffsets
        });

        return JsonSerializer.Serialize(new { partition = volume.Name, files = rows });
    }

    public void CopyFile(string imagePath, string keyPath, PlayStationVolume volume, PlayStationFileEntry entry, string outputPath)
    {
        using var reader = OpenReader(imagePath, volume.Name);
        using var output = File.Create(outputPath);
        var buffer = new byte[1024 * 1024];
        var remaining = entry.Length;
        foreach (var extent in entry.Extents)
        {
            var readable = Math.Min(extent.Length, remaining);
            var offset = extent.Offset;
            while (readable > 0 && remaining > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, Math.Min(readable, remaining));
                reader.Read(offset, buffer.AsSpan(0, chunk));
                output.Write(buffer, 0, chunk);
                offset += chunk;
                readable -= chunk;
                remaining -= chunk;
            }
        }
    }

    public string DecryptToFile(string imagePath, string keyPath, PlayStationVolume volume, string outputPath)
    {
        using var reader = OpenReader(imagePath, volume.Name);
        using var output = File.Create(outputPath);
        var buffer = new byte[1024 * 1024];
        ulong offset = 0;
        while (offset < reader.Length)
        {
            var chunk = (int)Math.Min((ulong)buffer.Length, reader.Length - offset);
            reader.Read((long)offset, buffer.AsSpan(0, chunk));
            output.Write(buffer, 0, chunk);
            offset += (ulong)chunk;
        }

        return outputPath;
    }

    public string ListDeletedInodesJson(string imagePath, string keyPath, PlayStationVolume volume)
    {
        if (GetPartition(volume.Name).FileSystem != Ps3FileSystem.Ufs2)
        {
            return JsonSerializer.Serialize(new { partition = volume.Name, files = Array.Empty<object>() });
        }

        using var reader = OpenReader(imagePath, volume.Name);
        var rows = ManagedPs4UfsReader.ScanDeepMetadata(reader).Select(row => new
        {
            name = row.Name,
            type = row.Kind,
            inode = row.Inode,
            size = row.Size,
            created = row.Created,
            modified = row.Modified,
            accessed = row.Accessed,
            metadataOffset = $"0x{row.MetadataOffset:X}",
            inodeOffset = row.InodeOffset >= 0 ? $"0x{row.InodeOffset:X}" : string.Empty,
            direntOffset = row.DirentOffset >= 0 ? $"0x{row.DirentOffset:X}" : string.Empty,
            recordLength = row.RecordLength,
            fileType = row.FileType,
            nameLength = row.NameLength,
            isDeleted = row.IsDeleted,
            parentPath = row.ParentPath,
            dataOffsetCount = row.Extents.Count,
            dataRunCount = row.DataRunCount,
            largestRunBytes = row.LargestRunBytes,
            fragmentationStatus = row.FragmentationStatus,
            dataRanges = FormatRanges(row.Extents),
            dataOffsets = string.Join(", ", row.Extents.Take(128).Select(extent => $"0x{extent.Offset:X}")),
            metadataStatus = row.MetadataStatus
        });

        return JsonSerializer.Serialize(new { partition = volume.Name, files = rows });
    }

    public string ExportDeletedInode(string imagePath, string keyPath, PlayStationVolume volume, uint inodeNumber, string outputPath)
    {
        if (GetPartition(volume.Name).FileSystem != Ps3FileSystem.Ufs2)
        {
            throw new InvalidOperationException("Deleted inode export is only available for PS3 UFS2 partitions.");
        }

        using var reader = OpenReader(imagePath, volume.Name);
        var record = ManagedPs4UfsReader.FindDeletedInode(reader, inodeNumber);
        using var output = File.Create(outputPath);
        var buffer = new byte[1024 * 1024];
        var remaining = (long)Math.Min(record.Size, long.MaxValue);
        foreach (var extent in record.Extents)
        {
            var readable = Math.Min(extent.Length, remaining);
            var offset = extent.Offset;
            while (readable > 0 && remaining > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, Math.Min(readable, remaining));
                reader.Read(offset, buffer.AsSpan(0, chunk));
                output.Write(buffer, 0, chunk);
                offset += chunk;
                readable -= chunk;
                remaining -= chunk;
            }
        }

        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            Array.Clear(buffer, 0, chunk);
            output.Write(buffer, 0, chunk);
            remaining -= chunk;
        }

        return outputPath;
    }

    private ManagedPs3Partition GetPartition(string name)
    {
        return _partitions.TryGetValue(name, out var partition)
            ? partition
            : throw new InvalidDataException($"PS3 partition '{name}' was not found.");
    }

    private ManagedPs3PartitionReader OpenReader(string imagePath, string name)
    {
        return new ManagedPs3PartitionReader(imagePath, GetPartition(name), _mode, _keys);
    }

    private static string FormatDate(DateTime date)
    {
        return date == DateTime.MinValue ? string.Empty : date.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatRanges(IReadOnlyList<FileExtent> extents)
    {
        return string.Join(", ", extents.Take(128).Select(extent => $"0x{extent.Offset:X}+0x{extent.Length:X}"));
    }
}

internal sealed class ManagedPs3PartitionReader : IPlayStationPartitionReader
{
    private const int SectorSize = 512;
    private readonly FileStream _stream;
    private readonly ManagedPs3Partition _partition;
    private readonly Ps3DiskCryptoMode _mode;
    private readonly ManagedPs3StorageImage.Ps3CryptoKeys? _keys;

    public ManagedPs3PartitionReader(string imagePath, ManagedPs3Partition partition, Ps3DiskCryptoMode mode, ManagedPs3StorageImage.Ps3CryptoKeys? keys)
    {
        _stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        _partition = partition;
        _mode = mode;
        _keys = keys;
    }

    public ulong Length => _partition.Length;

    public bool BigEndian => _partition.BigEndian;

    public void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || (ulong)offset + (ulong)destination.Length > _partition.Length)
        {
            throw new EndOfStreamException("Read exceeds PS3 partition bounds.");
        }

        var alignedOffset = offset - (offset % SectorSize);
        var prefix = (int)(offset - alignedOffset);
        var alignedLength = AlignUp(prefix + destination.Length, SectorSize);
        var buffer = new byte[alignedLength];
        var absolute = checked((long)_partition.Offset + alignedOffset);
        if (!ManagedPs4StorageImage.ReadExactly(_stream, absolute, buffer))
        {
            throw new EndOfStreamException("Could not read PS3 partition data.");
        }

        var startSector = (ulong)(absolute / SectorSize);
        ManagedPs3StorageImage.ApplyDiskCrypto(buffer, startSector, _mode, _keys);
        if (_partition.ExtraVflashCrypto)
        {
            ManagedPs3StorageImage.ApplyVflashCrypto(buffer, startSector, _keys);
        }

        buffer.AsSpan(prefix, destination.Length).CopyTo(destination);
    }

    public byte[] ReadBytes(long offset, int count)
    {
        var buffer = new byte[count];
        Read(offset, buffer);
        return buffer;
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    private static int AlignUp(int value, int alignment)
    {
        return (value + alignment - 1) / alignment * alignment;
    }
}

internal sealed record ManagedPs3Partition(string Name, Ps3FileSystem FileSystem, ulong Offset, ulong Length, bool BigEndian, bool ExtraVflashCrypto);

internal readonly record struct Ps3RawPartition(ulong Start, ulong Size);

internal enum Ps3DiskCryptoMode
{
    Plaintext,
    PhatAtaCbcSwapped,
    SlimAtaXtsSwapped
}

internal enum Ps3FileSystem
{
    Ufs2,
    Fat
}
