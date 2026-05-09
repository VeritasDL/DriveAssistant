using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FATXTools.Wpf;

internal static class ManagedPs4StorageImage
{
    private const int SectorSize = 512;

    private static readonly Guid UserType = new(0xc638477a, 0xe002, 0x4b57, 0xa4, 0x54, 0xa2, 0x7f, 0xb6, 0x3a, 0x33, 0xa8);
    private static readonly Guid EapUserType = new(0x21e4dfb4, 0x0040, 0x4934, 0xa0, 0x37, 0xea, 0x9d, 0xc0, 0x58, 0xee, 0xa6);
    private static readonly Guid EapVshType = new(0x6e0c5310, 0x8445, 0x4066, 0xb5, 0x71, 0x9b, 0x65, 0xfd, 0xb7, 0x59, 0x35);
    private static readonly Guid UpdateType = new(0xfdb5ede1, 0x73c3, 0x4c43, 0x8c, 0x5b, 0x2d, 0x3d, 0xcf, 0xcd, 0xdf, 0xf8);

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
            if (key.Length > 0 && key.Length < 0x20)
            {
                error = "PS4 EAP HDD keys must contain at least 32 bytes.";
                return false;
            }

            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
            var partitions = ReadOrbisPartitions(stream);
            if (partitions.Count == 0)
            {
                error = "No supported Orbis GPT partitions were found.";
                return false;
            }

            var operations = new ManagedPs4VolumeOperations(partitions, key.Take(0x20).ToArray());
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
                        ?? "No managed PS4 partitions could be indexed.";
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

    private static List<ManagedPs4Partition> ReadOrbisPartitions(FileStream stream)
    {
        var header = new byte[SectorSize];
        if (!ReadExactly(stream, SectorSize, header) || Encoding.ASCII.GetString(header.AsSpan(0, 8)) != "EFI PART")
        {
            return [];
        }

        var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x48, 8));
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x50, 4));
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x54, 4));
        if (entriesLba != 2 || entryCount == 0 || entryCount > 0x80 || entrySize < 0x80 || entrySize > 0x1000)
        {
            return [];
        }

        var rows = new List<ManagedPs4Partition>();
        var entry = new byte[entrySize];
        for (uint index = 0; index < entryCount; index++)
        {
            var offset = checked((long)entriesLba * SectorSize + index * (long)entrySize);
            if (!ReadExactly(stream, offset, entry))
            {
                break;
            }

            var type = new Guid(entry.AsSpan(0, 16));
            if (!TryMapPartition(type, out var name, out var fileSystem))
            {
                continue;
            }

            var firstLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(0x20, 8));
            var lastLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(0x28, 8));
            if (firstLba == 0 || lastLba <= firstLba)
            {
                continue;
            }

            rows.Add(new ManagedPs4Partition(
                name,
                fileSystem,
                firstLba,
                checked(firstLba * SectorSize),
                checked((lastLba - firstLba) * SectorSize),
                checked((ulong)index << 32)));
        }

        return rows
            .OrderBy(row => row.Name switch
            {
                "user" => 0,
                "eap_user" => 1,
                "eap_vsh" => 2,
                "update" => 3,
                _ => 99
            })
            .ToList();
    }

    private static bool TryMapPartition(Guid type, out string name, out ManagedPs4FileSystem fileSystem)
    {
        if (type == UserType)
        {
            name = "user";
            fileSystem = ManagedPs4FileSystem.Ufs2;
            return true;
        }

        if (type == EapUserType)
        {
            name = "eap_user";
            fileSystem = ManagedPs4FileSystem.Ufs2;
            return true;
        }

        if (type == EapVshType)
        {
            name = "eap_vsh";
            fileSystem = ManagedPs4FileSystem.Fat;
            return true;
        }

        if (type == UpdateType)
        {
            name = "update";
            fileSystem = ManagedPs4FileSystem.Fat;
            return true;
        }

        name = string.Empty;
        fileSystem = ManagedPs4FileSystem.Unknown;
        return false;
    }

    internal static bool ReadExactly(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        var readTotal = 0;
        while (readTotal < buffer.Length)
        {
            var read = stream.Read(buffer[readTotal..]);
            if (read == 0)
            {
                return false;
            }

            readTotal += read;
        }

        return true;
    }
}

internal sealed class ManagedPs4VolumeOperations : IPlayStationVolumeOperations
{
    private readonly Dictionary<string, ManagedPs4Partition> _partitions;
    private readonly byte[] _key;

    public ManagedPs4VolumeOperations(IEnumerable<ManagedPs4Partition> partitions, byte[] key)
    {
        _partitions = partitions.ToDictionary(partition => partition.Name, StringComparer.OrdinalIgnoreCase);
        _key = key;
    }

    public string FamilyText => "PlayStation 4 HDD (managed)";

    public string ListFilesJson(string imagePath, string keyPath, PlayStationVolume volume)
    {
        var entries = ReadEntries(imagePath, volume.Name);
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
        if (entry.Extents.Count == 0 && entry.Length > 0)
        {
            throw new InvalidDataException("The selected PS4 file has no managed data extents to export.");
        }

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

        WriteZeroFill(output, buffer, ref remaining);
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

        return $"Decrypted {volume.Name} to {outputPath}";
    }

    public string ListDeletedInodesJson(string imagePath, string keyPath, PlayStationVolume volume)
    {
        var partition = GetPartition(volume.Name);
        if (partition.FileSystem != ManagedPs4FileSystem.Ufs2)
        {
            throw new InvalidOperationException("Selected PlayStation partition is not a readable UFS2 file system.");
        }

        using var reader = OpenReader(imagePath, volume.Name);
        var rows = ManagedPs4UfsReader.ScanDeepMetadata(reader)
            .Select(row => new
            {
                name = row.Name,
                type = row.Kind,
                inode = row.Inode,
                mode = row.Mode,
                links = row.LinkCount,
                size = row.Size,
                blocks = row.Blocks,
                created = FormatUnix(row.Created),
                modified = FormatUnix(row.Modified),
                accessed = FormatUnix(row.Accessed),
                inodeOffset = $"0x{row.InodeOffset:X}",
                direntOffset = row.DirentOffset >= 0 ? $"0x{row.DirentOffset:X}" : "",
                metadataOffset = $"0x{row.MetadataOffset:X}",
                recordLength = row.RecordLength,
                fileType = row.FileType,
                nameLength = row.NameLength,
                isDeleted = row.IsDeleted,
                dataOffsetCount = row.Extents.Count,
                dataRunCount = row.DataRunCount,
                largestRunBytes = row.LargestRunBytes,
                estimatedAllocationUnit = row.EstimatedAllocationUnit,
                fragmentationStatus = row.FragmentationStatus,
                dataRanges = FormatRanges(row.Extents),
                dataOffsets = string.Join(", ", row.Extents.Take(128).Select(extent => $"0x{extent.Offset:X}")),
                metadataStatus = row.MetadataStatus,
                recoverability = row.Recoverability,
                confidence = row.Confidence,
                parentPath = row.ParentPath,
                allocationStatus = row.AllocationStatus,
                correlation = row.Correlation
            });

        return JsonSerializer.Serialize(new { partition = volume.Name, files = rows });
    }

    public string ExportDeletedInode(string imagePath, string keyPath, PlayStationVolume volume, uint inodeNumber, string outputPath)
    {
        using var reader = OpenReader(imagePath, volume.Name);
        var row = ManagedPs4UfsReader.FindDeletedInode(reader, inodeNumber);
        using var output = File.Create(outputPath);
        var buffer = new byte[1024 * 1024];
        var remaining = (long)Math.Min(row.Size, long.MaxValue);
        foreach (var extent in row.Extents)
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

        WriteZeroFill(output, buffer, ref remaining);

        WriteDeletedInodeSidecar(outputPath, volume.Name, row);

        return $"Exported deleted UFS inode {inodeNumber} to {outputPath} ({new FileInfo(outputPath).Length:N0} bytes).";
    }

    private List<PlayStationFileEntry> ReadEntries(string imagePath, string partitionName)
    {
        var partition = GetPartition(partitionName);
        using var reader = OpenReader(imagePath, partitionName);
        var entries = partition.FileSystem switch
        {
            ManagedPs4FileSystem.Ufs2 => ManagedPs4UfsReader.ReadEntries(reader),
            ManagedPs4FileSystem.Fat => ManagedPs4FatReader.ReadEntries(reader),
            _ => throw new InvalidDataException($"Unsupported PS4 partition type for {partitionName}.")
        };
        return Flatten(entries).ToList();
    }

    private EncryptedPs4PartitionReader OpenReader(string imagePath, string partitionName)
    {
        var partition = GetPartition(partitionName);
        var candidates = _key.Length >= 0x20
            ? new[] { partition.IvOffsetSectors, 0UL }.Distinct().ToArray()
            : [0UL];

        foreach (var sectorBase in candidates)
        {
            var reader = new EncryptedPs4PartitionReader(imagePath, partition, _key, sectorBase);
            try
            {
                if (LooksReadable(reader, partition.FileSystem))
                {
                    return reader;
                }
            }
            catch (IOException)
            {
            }
            catch (InvalidDataException)
            {
            }

            reader.Dispose();
        }

        throw new InvalidDataException($"PS4 partition '{partitionName}' did not decrypt to a supported {partition.FileSystem} filesystem with known IV offsets.");
    }

    private ManagedPs4Partition GetPartition(string partitionName)
    {
        return _partitions.TryGetValue(partitionName, out var partition)
            ? partition
            : throw new InvalidDataException($"PS4 partition '{partitionName}' was not found.");
    }

    private static bool LooksReadable(EncryptedPs4PartitionReader reader, ManagedPs4FileSystem fileSystem)
    {
        return fileSystem switch
        {
            ManagedPs4FileSystem.Ufs2 => LooksLikeUfs2(reader),
            ManagedPs4FileSystem.Fat => LooksLikeFat(reader),
            _ => false
        };
    }

    private static bool LooksLikeUfs2(EncryptedPs4PartitionReader reader)
    {
        var data = reader.ReadBytes(65536, 1500);
        for (var offset = 0; offset + 4 <= data.Length; offset += 4)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)) == 0x19540119)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeFat(EncryptedPs4PartitionReader reader)
    {
        var boot = reader.ReadBytes(0, 512);
        if (boot[510] != 0x55 || boot[511] != 0xAA)
        {
            return false;
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11, 2));
        var sectorsPerCluster = boot[13];
        var fatCount = boot[16];
        var totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19, 2));
        var totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32, 4));
        var sectorsPerFat16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22, 2));
        var sectorsPerFat32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36, 4));
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096) || sectorsPerCluster == 0 || fatCount == 0)
        {
            return false;
        }

        if ((totalSectors16 == 0 && totalSectors32 == 0) || (sectorsPerFat16 == 0 && sectorsPerFat32 == 0))
        {
            return false;
        }

        var type16 = Encoding.ASCII.GetString(boot.AsSpan(54, 8));
        var type32 = Encoding.ASCII.GetString(boot.AsSpan(82, 8));
        return type16.Contains("FAT", StringComparison.OrdinalIgnoreCase)
               || type32.Contains("FAT", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDate(DateTime date)
    {
        return date == DateTime.MinValue ? string.Empty : date.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string FormatUnix(long seconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("O", CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatRanges(IReadOnlyList<FileExtent> extents)
    {
        return string.Join(", ", extents.Take(128).Select(extent => $"0x{extent.Offset:X}+0x{extent.Length:X}"));
    }

    private static void WriteZeroFill(FileStream output, byte[] buffer, ref long remaining)
    {
        if (remaining <= 0)
        {
            return;
        }

        Array.Clear(buffer);
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            output.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
    }

    private static void WriteDeletedInodeSidecar(string outputPath, string partitionName, ManagedUfsRecoveryRecord row)
    {
        var sidecarPath = outputPath + ".metadata.json";
        var hash = File.Exists(outputPath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputPath)))
            : string.Empty;
        var metadata = new
        {
            platform = "PlayStation 4",
            partition = partitionName,
            exportPath = outputPath,
            exportedSha256 = hash,
            exportedBytes = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0,
            row.Name,
            row.Kind,
            row.Inode,
            row.Mode,
            row.LinkCount,
            row.Size,
            row.Blocks,
            row.InodeOffset,
            row.DirentOffset,
            row.ParentPath,
            row.MetadataStatus,
            row.Recoverability,
            row.Confidence,
            row.AllocationStatus,
            row.Correlation,
            dataRanges = FormatRanges(row.Extents),
            dataOffsets = row.Extents.Select(extent => new { extent.Offset, extent.Length }).ToArray()
        };
        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<PlayStationFileEntry> Flatten(IEnumerable<PlayStationFileEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Flatten(entry.Children))
            {
                yield return child;
            }
        }
    }
}

internal sealed class EncryptedPs4PartitionReader : IDisposable
{
    private const int SectorSize = 512;
    private readonly FileStream _stream;
    private readonly ManagedPs4Partition _partition;
    private readonly AesXtsDecryptor? _decryptor;
    private readonly bool _isPlaintext;

    private readonly ulong _sectorBase;

    public EncryptedPs4PartitionReader(string imagePath, ManagedPs4Partition partition, byte[] key, ulong sectorBase)
    {
        _stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        _partition = partition;
        _sectorBase = sectorBase;
        _isPlaintext = key.Length < 0x20;
        _decryptor = _isPlaintext ? null : new AesXtsDecryptor(key.AsSpan(0, 16).ToArray(), key.AsSpan(16, 16).ToArray());
    }

    public ulong Length => _partition.Length;

    public void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || (ulong)offset + (ulong)destination.Length > _partition.Length)
        {
            throw new EndOfStreamException("Read exceeds PS4 partition bounds.");
        }

        var alignedOffset = offset - (offset % SectorSize);
        var prefix = (int)(offset - alignedOffset);
        var alignedLength = AlignUp(prefix + destination.Length, SectorSize);
        var buffer = new byte[alignedLength];
        var absolute = checked((long)_partition.Offset + alignedOffset);
        if (!ManagedPs4StorageImage.ReadExactly(_stream, absolute, buffer))
        {
            throw new EndOfStreamException("Could not read encrypted PS4 partition data.");
        }

        if (!_isPlaintext)
        {
            for (var blockOffset = 0; blockOffset < buffer.Length; blockOffset += SectorSize)
            {
                if (BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(blockOffset, 8)) == 0)
                {
                    continue;
                }

                var sector = _sectorBase + (ulong)((alignedOffset + blockOffset) / SectorSize);
                _decryptor!.DecryptSector(buffer.AsSpan(blockOffset, SectorSize), sector);
            }
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
        _decryptor?.Dispose();
        _stream.Dispose();
    }

    private static int AlignUp(int value, int alignment)
    {
        return (value + alignment - 1) / alignment * alignment;
    }
}

internal sealed class AesXtsDecryptor : IDisposable
{
    private readonly Aes _dataAes;
    private readonly Aes _tweakAes;
    private readonly ICryptoTransform _dataDecryptor;
    private readonly ICryptoTransform _tweakEncryptor;

    public AesXtsDecryptor(byte[] dataKey, byte[] tweakKey)
    {
        _dataAes = CreateAes(dataKey);
        _tweakAes = CreateAes(tweakKey);
        _dataDecryptor = _dataAes.CreateDecryptor();
        _tweakEncryptor = _tweakAes.CreateEncryptor();
    }

    public void DecryptSector(Span<byte> sector, ulong sectorNumber)
    {
        Span<byte> tweak = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(tweak, sectorNumber);
        Transform(_tweakEncryptor, tweak);

        Span<byte> block = stackalloc byte[16];
        for (var offset = 0; offset < sector.Length; offset += 16)
        {
            for (var index = 0; index < 16; index++)
            {
                block[index] = (byte)(sector[offset + index] ^ tweak[index]);
            }

            Transform(_dataDecryptor, block);
            for (var index = 0; index < 16; index++)
            {
                sector[offset + index] = (byte)(block[index] ^ tweak[index]);
            }

            MultiplyTweak(tweak);
        }
    }

    public void Dispose()
    {
        _dataDecryptor.Dispose();
        _tweakEncryptor.Dispose();
        _dataAes.Dispose();
        _tweakAes.Dispose();
    }

    private static Aes CreateAes(byte[] key)
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        return aes;
    }

    private static void Transform(ICryptoTransform transform, Span<byte> block)
    {
        var input = block.ToArray();
        var output = new byte[16];
        transform.TransformBlock(input, 0, input.Length, output, 0);
        output.CopyTo(block);
    }

    private static void MultiplyTweak(Span<byte> tweak)
    {
        var carryIn = 0;
        var carryOut = 0;
        for (var index = 0; index < tweak.Length; index++)
        {
            carryOut = (tweak[index] >> 7) & 1;
            tweak[index] = (byte)(((tweak[index] << 1) + carryIn) & 0xFF);
            carryIn = carryOut;
        }

        if (carryOut != 0)
        {
            tweak[0] ^= 0x87;
        }
    }
}

internal static class ManagedPs4FatReader
{
    public static List<PlayStationFileEntry> ReadEntries(EncryptedPs4PartitionReader reader)
    {
        var boot = reader.ReadBytes(0, 512);
        if (boot[510] != 0x55 || boot[511] != 0xAA)
        {
            throw new InvalidDataException("Invalid FAT boot sector.");
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11, 2));
        var sectorsPerCluster = boot[13];
        var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14, 2));
        var fatCount = boot[16];
        var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17, 2));
        var totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19, 2));
        var sectorsPerFat16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22, 2));
        var totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32, 4));
        var sectorsPerFat32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36, 4));
        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(44, 4));

        if (bytesPerSector == 0 || sectorsPerCluster == 0 || fatCount == 0)
        {
            throw new InvalidDataException("Invalid FAT geometry.");
        }

        var sectorsPerFat = sectorsPerFat16 != 0 ? sectorsPerFat16 : sectorsPerFat32;
        var totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;
        var clusterSize = bytesPerSector * sectorsPerCluster;
        var rootDirectorySectors = ((rootEntries * 32) + bytesPerSector - 1) / bytesPerSector;
        var rootDirectoryOffset = (reservedSectors + fatCount * sectorsPerFat) * bytesPerSector;
        var dataOffset = rootDirectoryOffset + rootDirectorySectors * bytesPerSector;
        var dataSectors = totalSectors - (reservedSectors + fatCount * sectorsPerFat + rootDirectorySectors);
        var clusterCount = dataSectors / sectorsPerCluster;
        var isFat32 = clusterCount >= 65525;

        var fatBytes = checked((int)(sectorsPerFat * bytesPerSector));
        var fat = reader.ReadBytes(reservedSectors * bytesPerSector, fatBytes);
        var entries = isFat32
            ? ReadDirectory(reader, fat, dataOffset, clusterSize, rootCluster, "/", true, 0, DateTime.MinValue)
            : ReadFixedRootDirectory(reader, fat, dataOffset, clusterSize, rootDirectoryOffset, rootEntries, "/");
        return entries;
    }

    private static List<PlayStationFileEntry> ReadFixedRootDirectory(
        EncryptedPs4PartitionReader reader,
        byte[] fat,
        long dataOffset,
        int clusterSize,
        long rootDirectoryOffset,
        int rootEntries,
        string path)
    {
        var data = reader.ReadBytes(rootDirectoryOffset, rootEntries * 32);
        return ParseDirectory(reader, fat, dataOffset, clusterSize, data, rootDirectoryOffset, path, false);
    }

    private static List<PlayStationFileEntry> ReadDirectory(
        EncryptedPs4PartitionReader reader,
        byte[] fat,
        long dataOffset,
        int clusterSize,
        uint firstCluster,
        string path,
        bool fat32,
        long directoryOffset,
        DateTime inheritedTime)
    {
        var clusters = GetClusterChain(fat, firstCluster, fat32);
        using var output = new MemoryStream();
        foreach (var cluster in clusters)
        {
            output.Write(reader.ReadBytes(ClusterToOffset(dataOffset, clusterSize, cluster), clusterSize));
        }

        return ParseDirectory(reader, fat, dataOffset, clusterSize, output.ToArray(), directoryOffset, path, fat32);
    }

    private static List<PlayStationFileEntry> ParseDirectory(
        EncryptedPs4PartitionReader reader,
        byte[] fat,
        long dataOffset,
        int clusterSize,
        byte[] data,
        long directoryOffset,
        string path,
        bool fat32)
    {
        var entries = new List<PlayStationFileEntry>();
        var lfnParts = new List<string>();
        for (var offset = 0; offset + 32 <= data.Length; offset += 32)
        {
            var entry = data.AsSpan(offset, 32);
            if (entry[0] == 0x00)
            {
                break;
            }

            if (entry[0] == 0xE5)
            {
                lfnParts.Clear();
                continue;
            }

            var attributes = entry[11];
            if (attributes == 0x0F)
            {
                lfnParts.Insert(0, DecodeLongName(entry));
                continue;
            }

            if ((attributes & 0x08) != 0)
            {
                lfnParts.Clear();
                continue;
            }

            var name = string.Concat(lfnParts).TrimEnd('\0', '\uffff');
            lfnParts.Clear();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = DecodeShortName(entry);
            }

            if (name is "." or ".." || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var isDirectory = (attributes & 0x10) != 0;
            var cluster = ((uint)BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(20, 2)) << 16)
                          | BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(26, 2));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(28, 4));
            var childPath = Combine(path, name);
            var clusters = cluster >= 2 ? GetClusterChain(fat, cluster, fat32) : [];
            var extents = isDirectory ? Array.Empty<FileExtent>() : BuildExtents(dataOffset, clusterSize, clusters, size);
            var modified = DecodeFatDateTime(entry);
            var item = CreateEntry(
                childPath,
                name,
                isDirectory ? "Directory" : "File",
                isDirectory ? 0 : size,
                DecodeFatDateTime(entry, createTime: true),
                modified,
                DecodeFatDate(entry),
                directoryOffset + offset,
                extents,
                string.Empty,
                $"0x{directoryOffset + offset:X}",
                string.Empty);

            if (isDirectory && cluster >= 2)
            {
                item.Children.AddRange(ReadDirectory(reader, fat, dataOffset, clusterSize, cluster, childPath, fat32, ClusterToOffset(dataOffset, clusterSize, cluster), modified));
            }

            entries.Add(item);
        }

        return entries;
    }

    private static List<uint> GetClusterChain(byte[] fat, uint firstCluster, bool fat32)
    {
        var chain = new List<uint>();
        var seen = new HashSet<uint>();
        var cluster = firstCluster;
        while (cluster >= 2 && seen.Add(cluster))
        {
            var index = fat32 ? cluster * 4 : cluster * 2;
            if (index + (fat32 ? 4 : 2) > fat.Length)
            {
                break;
            }

            chain.Add(cluster);
            var next = fat32
                ? BinaryPrimitives.ReadUInt32LittleEndian(fat.AsSpan((int)index, 4)) & 0x0FFFFFFF
                : BinaryPrimitives.ReadUInt16LittleEndian(fat.AsSpan((int)index, 2));
            if (fat32 ? next >= 0x0FFFFFF8 : next >= 0xFFF8)
            {
                break;
            }

            if (next == 0)
            {
                break;
            }

            cluster = next;
        }

        return chain;
    }

    private static IReadOnlyList<FileExtent> BuildExtents(long dataOffset, int clusterSize, IReadOnlyList<uint> clusters, long length)
    {
        var extents = new List<FileExtent>();
        var remaining = length;
        foreach (var group in CollapseClusters(clusters))
        {
            var bytes = Math.Min(remaining, group.Count * (long)clusterSize);
            if (bytes <= 0)
            {
                break;
            }

            extents.Add(new FileExtent(ClusterToOffset(dataOffset, clusterSize, group.Start), bytes));
            remaining -= bytes;
        }

        return extents;
    }

    private static IEnumerable<(uint Start, int Count)> CollapseClusters(IReadOnlyList<uint> clusters)
    {
        if (clusters.Count == 0)
        {
            yield break;
        }

        var start = clusters[0];
        var previous = start;
        var count = 1;
        for (var index = 1; index < clusters.Count; index++)
        {
            if (clusters[index] == previous + 1)
            {
                previous = clusters[index];
                count++;
                continue;
            }

            yield return (start, count);
            start = previous = clusters[index];
            count = 1;
        }

        yield return (start, count);
    }

    private static long ClusterToOffset(long dataOffset, int clusterSize, uint cluster)
    {
        return dataOffset + (cluster - 2L) * clusterSize;
    }

    private static string DecodeLongName(ReadOnlySpan<byte> entry)
    {
        Span<byte> raw = stackalloc byte[26];
        entry.Slice(1, 10).CopyTo(raw);
        entry.Slice(14, 12).CopyTo(raw[10..]);
        entry.Slice(28, 4).CopyTo(raw[22..]);
        return Encoding.Unicode.GetString(raw).TrimEnd('\0', '\uffff');
    }

    private static string DecodeShortName(ReadOnlySpan<byte> entry)
    {
        var name = Encoding.ASCII.GetString(entry[..8]).Trim();
        var extension = Encoding.ASCII.GetString(entry.Slice(8, 3)).Trim();
        return string.IsNullOrWhiteSpace(extension) ? name : $"{name}.{extension}";
    }

    private static DateTime DecodeFatDateTime(ReadOnlySpan<byte> entry, bool createTime = false)
    {
        var timeOffset = createTime ? 14 : 22;
        var dateOffset = createTime ? 16 : 24;
        return DecodeFatDateTime(
            BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(dateOffset, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(timeOffset, 2)));
    }

    private static DateTime DecodeFatDate(ReadOnlySpan<byte> entry)
    {
        return DecodeFatDateTime(BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(18, 2)), 0);
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

    private static string Combine(string path, string name)
    {
        return path.TrimEnd('/') + "/" + name;
    }

    private static PlayStationFileEntry CreateEntry(
        string path,
        string name,
        string type,
        long length,
        DateTime created,
        DateTime modified,
        DateTime accessed,
        long offset,
        IReadOnlyList<FileExtent> extents,
        string inodeOffsets,
        string direntOffsets,
        string blocktableOffsets)
    {
        return ManagedPs4UfsReader.CreateEntry(path, name, type, length, created, modified, accessed, offset, extents, inodeOffsets, direntOffsets, blocktableOffsets);
    }
}

internal static class ManagedPs4UfsReader
{
    private const int SuperBlockOffset = 65536;
    private const int SuperBlockLength = 1500;
    private const int InodeSize = 256;
    private const int RootInode = 2;
    private const int NdAddr = 12;
    private const int NiAddr = 3;
    private const ushort Ifmt = 0xF000;
    private const ushort Ifdir = 0x4000;
    private const ushort Ifreg = 0x8000;
    private const int Ufs2Magic = 0x19540119;
    private const int CgMagic = 0x090255;

    private static readonly IReadOnlyDictionary<byte, string> DirentTypes = new Dictionary<byte, string>
    {
        [0] = "Unknown",
        [1] = "FIFO",
        [2] = "Character Device",
        [4] = "Directory",
        [6] = "Block Device",
        [8] = "File",
        [10] = "Symbolic Link",
        [12] = "Socket",
        [14] = "Whiteout"
    };

    public static List<PlayStationFileEntry> ReadEntries(EncryptedPs4PartitionReader reader)
    {
        var super = ReadSuperblock(reader);
        var root = ReadInode(reader, super, RootInode);
        if ((root.Mode & Ifmt) != Ifdir)
        {
            return [];
        }

        var rows = new List<PlayStationFileEntry>();
        LoadDirectory(reader, super, root, "/", rows, new HashSet<uint> { RootInode });
        return rows;
    }

    public static List<ManagedUfsRecoveryRecord> ScanDeepMetadata(EncryptedPs4PartitionReader reader)
    {
        var super = ReadSuperblock(reader);
        var totalInodes = ValidateSuperblock(super);
        var allocationMap = UfsAllocationMap.TryRead(reader, super);
        var inodeRecords = ScanInodeTable(reader, super, totalInodes, allocationMap);
        var deletedByInode = inodeRecords
            .Where(row => row.Inode > 0)
            .GroupBy(row => row.Inode)
            .ToDictionary(group => group.Key, group => group.First());

        var rows = new List<ManagedUfsRecoveryRecord>();
        rows.AddRange(inodeRecords);

        var root = ReadInode(reader, super, RootInode);
        if ((root.Mode & Ifmt) == Ifdir)
        {
            ScanDirectoryForMetadata(
                reader,
                super,
                root,
                "/",
                deletedByInode,
                allocationMap,
                rows,
                new HashSet<uint> { RootInode },
                includeActiveRows: false);
        }

        foreach (var directory in inodeRecords.Where(row => row.FileType == 4 && row.Extents.Count > 0))
        {
            try
            {
                var inode = ReadInode(reader, directory.InodeOffset);
                ScanDirectoryForMetadata(
                    reader,
                    super,
                    inode,
                    $"/<deleted-directory-inode-{directory.Inode}>",
                    deletedByInode,
                    allocationMap,
                    rows,
                    new HashSet<uint>(),
                    includeActiveRows: true);
            }
            catch
            {
                // Corrupt orphan directory contents are still useful if the inode row was emitted.
            }
        }

        return rows
            .GroupBy(row => $"{row.Kind}:{row.MetadataOffset:X}:{row.Inode}:{row.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(row => row.IsDeleted)
            .ThenBy(row => row.ParentPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.MetadataOffset)
            .ToList();
    }

    private static List<ManagedUfsRecoveryRecord> ScanInodeTable(
        EncryptedPs4PartitionReader reader,
        UfsSuperblock super,
        ulong totalInodes,
        UfsAllocationMap? allocationMap)
    {
        var rows = new List<ManagedUfsRecoveryRecord>();
        var seen = new HashSet<ulong>();
        for (uint inodeNumber = 2; inodeNumber < totalInodes; inodeNumber++)
        {
            var inodeOffset = InodeOffset(super, inodeNumber);
            if ((ulong)inodeOffset + InodeSize > reader.Length)
            {
                continue;
            }

            var inode = ReadInode(reader, inodeOffset);
            var modeType = inode.Mode & Ifmt;
            if (inode.LinkCount != 0 || inode.Size > reader.Length || modeType is not (Ifreg or Ifdir))
            {
                continue;
            }

            var extents = CollectExtents(reader, super, inode);
            if (modeType == Ifreg && (inode.Size == 0 || extents.Count == 0))
            {
                continue;
            }

            var signature = ((ulong)(extents.FirstOrDefault()?.Offset ?? inodeOffset) << 16) ^ inode.Size ^ inodeNumber;
            if (!seen.Add(signature))
            {
                continue;
            }

            var allocationStatus = allocationMap?.DescribeExtents(extents) ?? "Allocation bitmap unavailable";
            var recoverability = ClassifyRecoverability(modeType == Ifdir, extents, allocationStatus, inode.Size, inode.LinkCount, inodeAllocated: allocationMap?.IsInodeAllocated(inodeNumber));
            rows.Add(new ManagedUfsRecoveryRecord(
                $"inode_{inodeNumber}",
                modeType == Ifdir ? "Deleted UFS directory inode" : "Deleted UFS inode",
                inodeNumber,
                inode.Mode,
                inode.LinkCount,
                inode.Size,
                inode.Blocks,
                inode.BirthTime,
                inode.ModifiedTime,
                inode.AccessedTime,
                inodeOffset,
                -1,
                inodeOffset,
                0,
                modeType == Ifdir ? (byte)4 : (byte)8,
                0,
                true,
                string.Empty,
                extents,
                CountRuns(extents),
                extents.Count == 0 ? 0 : extents.Max(extent => extent.Length),
                extents.Count == 0 ? 0 : EstimateAllocationUnit(extents),
                extents.Count <= 1 ? "Contiguous" : $"Fragmented into {CountRuns(extents):N0} runs",
                BuildMetadataStatus(modeType == Ifdir ? "Deleted directory inode" : "Deleted file inode", recoverability, allocationStatus, "No surviving filename found yet"),
                recoverability,
                ConfidenceFor(recoverability),
                allocationStatus,
                "Inode table scan"));
        }

        return rows;
    }

    public static ManagedUfsRecoveryRecord FindDeletedInode(EncryptedPs4PartitionReader reader, uint inodeNumber)
    {
        return ScanDeepMetadata(reader).FirstOrDefault(row => row.Inode == inodeNumber && row.Extents.Count > 0)
               ?? throw new InvalidDataException("Requested UFS inode is not a recoverable deleted regular file.");
    }

    internal static PlayStationFileEntry CreateEntry(
        string path,
        string name,
        string type,
        long length,
        DateTime created,
        DateTime modified,
        DateTime accessed,
        long offset,
        IReadOnlyList<FileExtent> extents,
        string inodeOffsets,
        string direntOffsets,
        string blocktableOffsets)
    {
        return new PlayStationFileEntry(
            null!,
            path,
            name,
            type,
            length,
            created,
            modified,
            accessed,
            extents.Count,
            CountRuns(extents),
            extents.Count == 0 ? 0 : extents.Max(extent => extent.Length),
            extents.Count == 0 ? 0 : EstimateAllocationUnit(extents),
            type.Equals("Directory", StringComparison.OrdinalIgnoreCase) ? "Directory" : extents.Count <= 1 ? "Contiguous" : $"Fragmented into {CountRuns(extents):N0} runs",
            FormatRanges(extents),
            string.Join(", ", extents.Take(128).Select(extent => $"0x{extent.Offset:X}")),
            inodeOffsets,
            direntOffsets,
            blocktableOffsets,
            extents);
    }

    private static void LoadDirectory(
        EncryptedPs4PartitionReader reader,
        UfsSuperblock super,
        UfsInode directory,
        string path,
        List<PlayStationFileEntry> rows,
        HashSet<uint> visited)
    {
        var data = ReadInodeData(reader, super, directory);
        var cursor = 0;
        var guard = 0;
        while (cursor + 8 <= data.Length && cursor < (long)directory.Size && guard++ < 100000)
        {
            var record = data.AsSpan(cursor);
            var inodeNumber = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2));
            var fileType = record[6];
            var nameLength = record[7];
            if (inodeNumber == 0 || recordLength < 8 + nameLength || recordLength > data.Length - cursor || recordLength % 4 != 0)
            {
                break;
            }

            var name = Encoding.ASCII.GetString(record.Slice(8, nameLength));
            if (name is not "." and not "..")
            {
                var inode = ReadInode(reader, super, inodeNumber);
                var modeType = inode.Mode & Ifmt;
                var isDirectory = fileType == 4 || modeType == Ifdir;
                var extents = isDirectory ? Array.Empty<FileExtent>() : CollectExtents(reader, super, inode);
                var childPath = path.TrimEnd('/') + "/" + name;
                var inodeOffset = InodeOffset(super, inodeNumber);
                var direntOffset = FindDirentOffset(super, directory, cursor);
                var item = CreateEntry(
                    childPath,
                    name,
                    isDirectory ? "Directory" : "File",
                    isDirectory ? 0 : (long)Math.Min(inode.Size, long.MaxValue),
                    ToDate(inode.BirthTime),
                    ToDate(inode.ModifiedTime),
                    ToDate(inode.AccessedTime),
                    extents.FirstOrDefault()?.Offset ?? direntOffset,
                    extents,
                    $"0x{inodeOffset:X}",
                    $"0x{direntOffset:X}",
                    string.Empty);

                if (isDirectory && visited.Add(inodeNumber))
                {
                    LoadDirectory(reader, super, inode, childPath, item.Children, visited);
                }

                rows.Add(item);
            }

            cursor += recordLength;
        }
    }

    private static void ScanDirectoryForMetadata(
        EncryptedPs4PartitionReader reader,
        UfsSuperblock super,
        UfsInode directory,
        string parentPath,
        IReadOnlyDictionary<uint, ManagedUfsRecoveryRecord> deletedByInode,
        UfsAllocationMap? allocationMap,
        List<ManagedUfsRecoveryRecord> rows,
        HashSet<uint> visited,
        bool includeActiveRows)
    {
        foreach (var block in ReadDirectoryBlocks(reader, super, directory))
        {
            var cursor = 0;
            var guard = 0;
            while (cursor + 8 <= block.Data.Length && guard++ < 100000)
            {
                var recordOffset = block.Offset + cursor;
                if (!TryParseDirent(block.Data.AsSpan(cursor), block.Data.Length - cursor, out var record))
                {
                    break;
                }

                AddDirentRecord(
                    reader,
                    super,
                    record,
                    recordOffset,
                    parentPath,
                    deletedByInode,
                    allocationMap,
                    rows,
                    includeActiveRows,
                    "Reachable directory block");

                var minimum = AlignDirentLength(record.NameLength);
                if (record.RecordLength > minimum + 8)
                {
                    ScanDirentSlack(
                        block.Data,
                        cursor + minimum,
                        record.RecordLength - minimum,
                        block.Offset,
                        parentPath,
                        deletedByInode,
                        allocationMap,
                        rows);
                }

                if (record.Inode > 0 && record.Name is not "." and not "..")
                {
                    try
                    {
                        var inode = ReadInode(reader, super, record.Inode);
                        if ((inode.Mode & Ifmt) == Ifdir && visited.Add(record.Inode))
                        {
                            ScanDirectoryForMetadata(
                                reader,
                                super,
                                inode,
                                parentPath.TrimEnd('/') + "/" + record.Name,
                                deletedByInode,
                                allocationMap,
                                rows,
                                visited,
                                includeActiveRows);
                        }
                    }
                    catch
                    {
                    }
                }

                cursor += record.RecordLength;
            }
        }
    }

    private static void AddDirentRecord(
        EncryptedPs4PartitionReader reader,
        UfsSuperblock super,
        UfsDirentRecord record,
        long recordOffset,
        string parentPath,
        IReadOnlyDictionary<uint, ManagedUfsRecoveryRecord> deletedByInode,
        UfsAllocationMap? allocationMap,
        List<ManagedUfsRecoveryRecord> rows,
        bool includeActiveRows,
        string source)
    {
        if (string.IsNullOrWhiteSpace(record.Name) || record.Name is "." or "..")
        {
            return;
        }

        if (record.Inode == 0)
        {
            rows.Add(CreateNameOnlyRecord(record, recordOffset, parentPath, "Deleted UFS dirent candidate", "Deleted dirent has no inode pointer", source));
            return;
        }

        if (deletedByInode.TryGetValue(record.Inode, out var inodeRecord))
        {
            rows.Add(inodeRecord with
            {
                Name = record.Name,
                Kind = record.FileType == 4 ? "Deleted UFS directory dirent" : "Deleted UFS dirent with inode",
                DirentOffset = recordOffset,
                MetadataOffset = recordOffset,
                RecordLength = record.RecordLength,
                FileType = record.FileType,
                NameLength = record.NameLength,
                ParentPath = parentPath,
                MetadataStatus = BuildMetadataStatus("Deleted dirent correlated to orphan inode", inodeRecord.Recoverability, inodeRecord.AllocationStatus, $"Dirent source: {source}"),
                Correlation = $"Dirent name points to deleted inode {record.Inode:N0}"
            });
            return;
        }

        bool? inodeAllocated = allocationMap?.IsInodeAllocated(record.Inode);
        if (inodeAllocated == false)
        {
            rows.Add(CreateNameOnlyRecord(record, recordOffset, parentPath, "Stale UFS dirent candidate", "Dirent points to a currently-free inode", source));
            return;
        }

        if (includeActiveRows)
        {
            rows.Add(CreateNameOnlyRecord(record, recordOffset, parentPath, "Active UFS dirent in orphan directory", "Active-looking dirent found inside deleted directory contents", source, isDeleted: false));
        }
    }

    private static void ScanDirentSlack(
        byte[] directoryBlock,
        int relativeStart,
        int length,
        long blockOffset,
        string parentPath,
        IReadOnlyDictionary<uint, ManagedUfsRecoveryRecord> deletedByInode,
        UfsAllocationMap? allocationMap,
        List<ManagedUfsRecoveryRecord> rows)
    {
        var end = Math.Min(directoryBlock.Length, relativeStart + length);
        for (var cursor = relativeStart; cursor + 8 <= end; cursor += 4)
        {
            if (!TryParseDirent(directoryBlock.AsSpan(cursor), end - cursor, out var stale) ||
                stale.Name is "." or "..")
            {
                continue;
            }

            var offset = blockOffset + cursor;
            if (stale.Inode > 0 && deletedByInode.TryGetValue(stale.Inode, out var inodeRecord))
            {
                rows.Add(inodeRecord with
                {
                    Name = stale.Name,
                    Kind = "UFS dirent slack with inode",
                    DirentOffset = offset,
                    MetadataOffset = offset,
                    RecordLength = stale.RecordLength,
                    FileType = stale.FileType,
                    NameLength = stale.NameLength,
                    ParentPath = parentPath,
                    MetadataStatus = BuildMetadataStatus("Directory slack name correlated to orphan inode", inodeRecord.Recoverability, inodeRecord.AllocationStatus, "Directory slack scan"),
                    Correlation = $"Slack dirent points to deleted inode {stale.Inode:N0}"
                });
            }
            else
            {
                var reason = stale.Inode == 0
                    ? "Slack dirent has no inode pointer"
                    : allocationMap?.IsInodeAllocated(stale.Inode) == false
                        ? "Slack dirent points to a currently-free inode"
                        : "Slack dirent could not be correlated to a deleted inode";
                rows.Add(CreateNameOnlyRecord(stale, offset, parentPath, "UFS dirent slack candidate", reason, "Directory slack scan"));
            }
        }
    }

    private static ManagedUfsRecoveryRecord CreateNameOnlyRecord(
        UfsDirentRecord record,
        long offset,
        string parentPath,
        string kind,
        string reason,
        string source,
        bool isDeleted = true)
    {
        var recoverability = record.Inode == 0 ? "Name only" : "Low confidence";
        return new ManagedUfsRecoveryRecord(
            record.Name,
            kind,
            record.Inode,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            -1,
            offset,
            offset,
            record.RecordLength,
            record.FileType,
            record.NameLength,
            isDeleted,
            parentPath,
            [],
            0,
            0,
            0,
            recoverability,
            BuildMetadataStatus(reason, recoverability, "No data extents", source),
            recoverability,
            ConfidenceFor(recoverability),
            "No data extents",
            source);
    }

    private static IReadOnlyList<UfsDirectoryBlock> ReadDirectoryBlocks(EncryptedPs4PartitionReader reader, UfsSuperblock super, UfsInode directory)
    {
        var blocks = new List<UfsDirectoryBlock>();
        var extents = CollectExtents(reader, super, directory);
        var remaining = (long)Math.Min(directory.Size, long.MaxValue);
        foreach (var extent in extents)
        {
            var readable = Math.Min(extent.Length, remaining);
            var offset = extent.Offset;
            while (readable > 0)
            {
                var chunk = (int)Math.Min(super.BlockSize, readable);
                blocks.Add(new UfsDirectoryBlock(offset, reader.ReadBytes(offset, chunk)));
                offset += chunk;
                readable -= chunk;
                remaining -= chunk;
            }
        }

        return blocks;
    }

    private static bool TryParseDirent(ReadOnlySpan<byte> data, int remaining, out UfsDirentRecord record)
    {
        record = default;
        if (remaining < 8)
        {
            return false;
        }

        var inode = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));
        var fileType = data[6];
        var nameLength = data[7];
        if (!DirentTypes.ContainsKey(fileType) ||
            recordLength < AlignDirentLength(nameLength) ||
            recordLength > remaining ||
            recordLength % 4 != 0 ||
            nameLength == 0 ||
            nameLength > 255 ||
            8 + nameLength > recordLength)
        {
            return false;
        }

        var nameBytes = data.Slice(8, nameLength);
        foreach (var value in nameBytes)
        {
            if (value < 0x20 || value > 0x7E || value == (byte)'/')
            {
                return false;
            }
        }

        record = new UfsDirentRecord(
            inode,
            recordLength,
            fileType,
            nameLength,
            Encoding.ASCII.GetString(nameBytes));
        return true;
    }

    private static byte[] ReadInodeData(EncryptedPs4PartitionReader reader, UfsSuperblock super, UfsInode inode)
    {
        var extents = CollectExtents(reader, super, inode);
        using var output = new MemoryStream();
        var remaining = (long)Math.Min(inode.Size, int.MaxValue);
        var buffer = new byte[super.BlockSize];
        foreach (var extent in extents)
        {
            var readable = Math.Min(extent.Length, remaining);
            var offset = extent.Offset;
            while (readable > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, readable);
                reader.Read(offset, buffer.AsSpan(0, chunk));
                output.Write(buffer, 0, chunk);
                offset += chunk;
                readable -= chunk;
                remaining -= chunk;
            }
        }

        return output.ToArray();
    }

    private static UfsSuperblock ReadSuperblock(EncryptedPs4PartitionReader reader)
    {
        var data = reader.ReadBytes(SuperBlockOffset, SuperBlockLength);
        var magicOffset = FindMagicOffset(data);
        if (magicOffset < 0)
        {
            throw new InvalidDataException("Selected PlayStation partition is not a readable UFS2 file system.");
        }

        return new UfsSuperblock(
            ReadInt32(data, 8),
            ReadInt32(data, 12),
            ReadInt32(data, 16),
            ReadInt32(data, 20),
            ReadUInt32(data, 44),
            ReadInt32(data, 48),
            ReadInt32(data, 52),
            ReadInt32(data, 56),
            ReadInt32(data, 96),
            ReadInt32(data, 100),
            ReadInt32(data, 116),
            ReadUInt32(data, 120),
            ReadInt32(data, 140),
            ReadUInt32(data, 184),
            ReadInt32(data, 188));
    }

    private static int FindMagicOffset(byte[] data)
    {
        for (var offset = 0; offset + 4 <= data.Length; offset += 4)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)) == Ufs2Magic)
            {
                return offset;
            }
        }

        return -1;
    }

    private static ulong ValidateSuperblock(UfsSuperblock super)
    {
        if (super.CylinderGroupCount == 0 || super.InodesPerGroup == 0 || super.FragmentSize <= 0 || super.BlockSize <= 0 ||
            super.BlockSize > 1024 * 1024 || super.IndirectCount <= 0 ||
            super.IndirectCount > super.BlockSize / 8 || super.CylinderGroupCount > 100000 ||
            super.InodesPerGroup > 10000000)
        {
            throw new InvalidDataException("UFS2 superblock values are outside expected bounds.");
        }

        var total = (ulong)super.CylinderGroupCount * super.InodesPerGroup;
        if (total == 0 || total > 100000000)
        {
            throw new InvalidDataException("UFS2 inode count is outside the bounded scan range.");
        }

        return total;
    }

    private static UfsInode ReadInode(EncryptedPs4PartitionReader reader, UfsSuperblock super, uint inodeNumber)
    {
        return ReadInode(reader, InodeOffset(super, inodeNumber));
    }

    private static UfsInode ReadInode(EncryptedPs4PartitionReader reader, long offset)
    {
        var data = reader.ReadBytes(offset, InodeSize);
        var direct = new long[NdAddr];
        var indirect = new long[NiAddr];
        for (var index = 0; index < NdAddr; index++)
        {
            direct[index] = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(112 + index * 8, 8));
        }

        for (var index = 0; index < NiAddr; index++)
        {
            indirect[index] = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(208 + index * 8, 8));
        }

        return new UfsInode(
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2)),
            BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(2, 2)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(24, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(32, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(40, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(56, 8)),
            direct,
            indirect);
    }

    private static long InodeOffset(UfsSuperblock super, uint inodeNumber)
    {
        var cg = inodeNumber / super.InodesPerGroup;
        var cgStart = (long)super.FragmentsPerGroup * cg;
        var cgInodeStart = cgStart + super.InodeBlock;
        var inodeBlock = cgInodeStart + ((((inodeNumber % super.InodesPerGroup) / super.InodesPerBlock)) << super.FragmentShift);
        return ((long)inodeBlock << super.FsbtodbShift) * 512 + (inodeNumber % super.InodesPerBlock) * InodeSize;
    }

    private static long FindDirentOffset(UfsSuperblock super, UfsInode directory, int cursor)
    {
        var blocks = directory.DirectBlocks.Where(block => block > 0).ToArray();
        if (blocks.Length == 0)
        {
            return cursor;
        }

        var blockIndex = Math.Min(blocks.Length - 1, cursor / super.BlockSize);
        return blocks[blockIndex] * super.FragmentSize + (cursor - blockIndex * super.FragmentSize);
    }

    private static IReadOnlyList<FileExtent> CollectExtents(EncryptedPs4PartitionReader reader, UfsSuperblock super, UfsInode inode)
    {
        var extents = new List<FileExtent>();
        var remaining = (long)Math.Min(inode.Size, long.MaxValue);
        foreach (var block in inode.DirectBlocks)
        {
            if (block <= 0 || remaining <= 0)
            {
                break;
            }

            AppendBlock(super, reader.Length, extents, block, ref remaining);
        }

        for (var level = 1; level <= inode.IndirectBlocks.Count; level++)
        {
            if (inode.IndirectBlocks[level - 1] > 0 && remaining > 0)
            {
                CollectIndirect(reader, super, inode.IndirectBlocks[level - 1], level, extents, ref remaining);
            }
        }

        return extents;
    }

    private static void CollectIndirect(EncryptedPs4PartitionReader reader, UfsSuperblock super, long tableBlock, int level, List<FileExtent> extents, ref long remaining)
    {
        if (tableBlock <= 0 || level <= 0 || remaining <= 0)
        {
            return;
        }

        var tableOffset = tableBlock * super.FragmentSize;
        if (tableOffset < 0 || (ulong)tableOffset + (ulong)super.BlockSize > reader.Length)
        {
            return;
        }

        var data = reader.ReadBytes(tableOffset, super.BlockSize);
        var count = Math.Min(super.IndirectCount, data.Length / 8);
        for (var index = 0; index < count && remaining > 0; index++)
        {
            var block = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(index * 8, 8));
            if (block <= 0)
            {
                break;
            }

            if (level == 1)
            {
                AppendBlock(super, reader.Length, extents, block, ref remaining);
            }
            else
            {
                CollectIndirect(reader, super, block, level - 1, extents, ref remaining);
            }
        }
    }

    private static void AppendBlock(UfsSuperblock super, ulong partitionLength, List<FileExtent> extents, long block, ref long remaining)
    {
        var offset = block * super.FragmentSize;
        if (offset <= 0 || (ulong)offset >= partitionLength)
        {
            return;
        }

        var length = Math.Min(Math.Min(super.BlockSize, remaining), (long)Math.Min((ulong)long.MaxValue, partitionLength - (ulong)offset));
        if (length <= 0)
        {
            return;
        }

        if (extents.Count > 0 && extents[^1].Offset + extents[^1].Length == offset)
        {
            extents[^1] = extents[^1] with { Length = extents[^1].Length + length };
        }
        else
        {
            extents.Add(new FileExtent(offset, length));
        }

        remaining -= length;
    }

    private static DateTime ToDate(long seconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static int CountRuns(IReadOnlyList<FileExtent> extents)
    {
        return extents.Count;
    }

    private static long EstimateAllocationUnit(IReadOnlyList<FileExtent> extents)
    {
        return extents.Count == 0 ? 0 : extents.Min(extent => extent.Length);
    }

    private static string FormatRanges(IReadOnlyList<FileExtent> extents)
    {
        return string.Join(", ", extents.Take(128).Select(extent => $"0x{extent.Offset:X}+0x{extent.Length:X}"));
    }

    private static int AlignDirentLength(int nameLength)
    {
        return (8 + nameLength + 3) & ~3;
    }

    private static string ClassifyRecoverability(bool isDirectory, IReadOnlyList<FileExtent> extents, string allocationStatus, ulong size, short linkCount, bool? inodeAllocated)
    {
        if (extents.Count == 0)
        {
            return isDirectory ? "Directory metadata" : "Name only";
        }

        if (inodeAllocated == true && linkCount == 0)
        {
            return "Suspicious";
        }

        if (allocationStatus.Contains("all data blocks still free", StringComparison.OrdinalIgnoreCase))
        {
            return isDirectory ? "Deleted directory" : "High";
        }

        if (allocationStatus.Contains("some data blocks reallocated", StringComparison.OrdinalIgnoreCase))
        {
            return "Partial";
        }

        if (allocationStatus.Contains("all data blocks appear reallocated", StringComparison.OrdinalIgnoreCase))
        {
            return "Overwritten";
        }

        return size == 0 ? "Metadata only" : "Medium";
    }

    private static double ConfidenceFor(string recoverability)
    {
        return recoverability switch
        {
            "High" => 0.95,
            "Deleted directory" => 0.85,
            "Medium" => 0.72,
            "Partial" => 0.55,
            "Low confidence" => 0.35,
            "Name only" => 0.25,
            "Overwritten" => 0.15,
            _ => 0.5
        };
    }

    private static string BuildMetadataStatus(string summary, string recoverability, string allocationStatus, string correlation)
    {
        return $"{summary}; recoverability: {recoverability}; allocation: {allocationStatus}; {correlation}";
    }

    private sealed class UfsAllocationMap
    {
        private readonly Dictionary<uint, byte[]> _inodeUsed = [];
        private readonly Dictionary<uint, byte[]> _blockFree = [];
        private readonly UfsSuperblock _super;

        private UfsAllocationMap(UfsSuperblock super)
        {
            _super = super;
        }

        public static UfsAllocationMap? TryRead(EncryptedPs4PartitionReader reader, UfsSuperblock super)
        {
            try
            {
                var map = new UfsAllocationMap(super);
                var cgBytes = Math.Clamp(super.CylinderGroupSize, 256, super.BlockSize);
                for (uint cg = 0; cg < super.CylinderGroupCount; cg++)
                {
                    var cgOffset = (long)(super.FragmentsPerGroup * cg + super.CylinderBlock) * super.FragmentSize;
                    if (cgOffset < 0 || (ulong)cgOffset + (ulong)cgBytes > reader.Length)
                    {
                        continue;
                    }

                    var data = reader.ReadBytes(cgOffset, cgBytes);
                    if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4)) != CgMagic)
                    {
                        continue;
                    }

                    var iusedOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(92, 4));
                    var freeOff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(96, 4));
                    if (iusedOff > 0 && iusedOff < data.Length)
                    {
                        map._inodeUsed[cg] = data[(int)iusedOff..];
                    }

                    if (freeOff > 0 && freeOff < data.Length)
                    {
                        map._blockFree[cg] = data[(int)freeOff..];
                    }
                }

                return map._inodeUsed.Count > 0 || map._blockFree.Count > 0 ? map : null;
            }
            catch
            {
                return null;
            }
        }

        public bool? IsInodeAllocated(uint inode)
        {
            var cg = inode / _super.InodesPerGroup;
            var local = inode % _super.InodesPerGroup;
            return TryGetBit(_inodeUsed, cg, local);
        }

        public string DescribeExtents(IReadOnlyList<FileExtent> extents)
        {
            if (extents.Count == 0)
            {
                return "No data extents";
            }

            var known = 0;
            var free = 0;
            var allocated = 0;
            foreach (var extent in extents)
            {
                var firstFragment = (ulong)Math.Max(0, extent.Offset / _super.FragmentSize);
                var fragmentCount = (ulong)Math.Max(1, (extent.Length + _super.FragmentSize - 1) / _super.FragmentSize);
                var sampleCount = Math.Min(fragmentCount, 256UL);
                var step = Math.Max(1UL, fragmentCount / sampleCount);
                for (ulong sampled = 0; sampled < fragmentCount; sampled += step)
                {
                    var fragment = firstFragment + sampled;
                    var cg = (uint)(fragment / (ulong)_super.FragmentsPerGroup);
                    var local = (uint)(fragment % (ulong)_super.FragmentsPerGroup);
                    var isFree = TryGetBit(_blockFree, cg, local);
                    if (isFree == null)
                    {
                        continue;
                    }

                    known++;
                    if (isFree.Value)
                    {
                        free++;
                    }
                    else
                    {
                        allocated++;
                    }
                }
            }

            if (known == 0)
            {
                return "Allocation bitmap unavailable";
            }

            if (allocated == 0)
            {
                return "all data blocks still free";
            }

            if (free == 0)
            {
                return "all data blocks appear reallocated";
            }

            return "some data blocks reallocated";
        }

        private static bool? TryGetBit(IReadOnlyDictionary<uint, byte[]> maps, uint cg, uint bit)
        {
            if (!maps.TryGetValue(cg, out var data))
            {
                return null;
            }

            var byteIndex = bit / 8;
            var bitIndex = (int)(bit % 8);
            if (byteIndex >= data.Length)
            {
                return null;
            }

            return (data[byteIndex] & (1 << bitIndex)) != 0;
        }
    }

    private static int ReadInt32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    private static uint ReadUInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
}

internal sealed record ManagedPs4Partition(string Name, ManagedPs4FileSystem FileSystem, ulong FirstLba, ulong Offset, ulong Length, ulong IvOffsetSectors);

internal enum ManagedPs4FileSystem
{
    Unknown,
    Ufs2,
    Fat
}

internal sealed record UfsSuperblock(
    int SuperBlock,
    int CylinderBlock,
    int InodeBlock,
    int DataBlock,
    uint CylinderGroupCount,
    int BlockSize,
    int FragmentSize,
    int FragmentsPerBlock,
    int FragmentShift,
    int FsbtodbShift,
    int IndirectCount,
    uint InodesPerBlock,
    int CylinderGroupSize,
    uint InodesPerGroup,
    int FragmentsPerGroup);

internal sealed record UfsInode(
    ushort Mode,
    short LinkCount,
    ulong Size,
    ulong Blocks,
    long AccessedTime,
    long ModifiedTime,
    long BirthTime,
    IReadOnlyList<long> DirectBlocks,
    IReadOnlyList<long> IndirectBlocks);

internal readonly record struct UfsDirentRecord(
    uint Inode,
    ushort RecordLength,
    byte FileType,
    byte NameLength,
    string Name);

internal sealed record UfsDirectoryBlock(long Offset, byte[] Data);

internal sealed record ManagedUfsRecoveryRecord(
    string Name,
    string Kind,
    uint Inode,
    ushort Mode,
    short LinkCount,
    ulong Size,
    ulong Blocks,
    long Created,
    long Modified,
    long Accessed,
    long InodeOffset,
    long DirentOffset,
    long MetadataOffset,
    ushort RecordLength,
    byte FileType,
    byte NameLength,
    bool IsDeleted,
    string ParentPath,
    IReadOnlyList<FileExtent> Extents,
    int DataRunCount,
    long LargestRunBytes,
    long EstimatedAllocationUnit,
    string FragmentationStatus,
    string MetadataStatus,
    string Recoverability,
    double Confidence,
    string AllocationStatus,
    string Correlation);
