using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace FATXTools.Wpf;

internal sealed class NintendoStorageImage : IDisposable
{
    private const int SectorSize = 512;
    private const long WiiNandSize = 512L * 1024 * 1024;
    private const long WiiNandWithSpareSize = 4096L * 64 * (2048 + 64);
    private const long DsiNandSize = 256L * 1024 * 1024;
    private const long DsiTrimmedNandSize = 240L * 1024 * 1024;
    private const long DsiUserDataLimit = 240L * 1024 * 1024;
    private const long DsiMainFatOffset = 0x10EE00;
    private const long DsiPhotoFatOffset = 0xCF09A00;
    private const long RvtHBankTableOffset = 0x60000000;
    private const long RvtHScanStep = 0x100000;
    private const long RvtHInitialScanLength = 0x4000000;
    private const long WiiSingleLayerDiscSize = 4_699_979_776;
    private const long GameCubeDiscSize = 1_459_978_240;
    private const int NcsdMediaUnitSize = 0x200;
    private readonly string? _temporarySourcePath;
    private readonly IReadOnlyList<string> _temporaryPartitionPaths;

    private NintendoStorageImage(string sourcePath, string activeSourcePath, string? temporarySourcePath, IReadOnlyList<string> temporaryPartitionPaths, IReadOnlyList<PartitionModel> partitions)
    {
        SourcePath = sourcePath;
        ActiveSourcePath = activeSourcePath;
        _temporarySourcePath = temporarySourcePath;
        _temporaryPartitionPaths = temporaryPartitionPaths;
        Partitions = partitions;
    }

    public string SourcePath { get; }

    public string ActiveSourcePath { get; }

    public IReadOnlyList<PartitionModel> Partitions { get; }

    public static NintendoStorageImage Open(string sourcePath, bool allowRawWiiUCandidate = false, string? keyPath = null)
    {
        var activePath = sourcePath;
        string? temporaryPath = null;
        if (Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)
            && TryExtractSingleImageEntry(sourcePath, out var extractedPath))
        {
            activePath = extractedPath;
            temporaryPath = extractedPath;
        }

        var length = new FileInfo(activePath).Length;
        var partitions = new List<PartitionModel>();
        var temporaryPartitionPaths = new List<string>();
        using var stream = new FileStream(activePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        var header = ReadHeader(stream, 0x8000);
        var keyStatus = WiiUKeyMaterial.TryLoad(keyPath, out var keys, out var loadStatus)
            ? loadStatus
            : loadStatus;
        var wiiKeyStatus = WiiKeyMaterial.TryLoad(keyPath, out _, out var wiiLoadStatus)
            ? wiiLoadStatus
            : wiiLoadStatus;
        var wfsInfo = WiiUWfsInspector.Inspect(activePath, keys);
        var zipWrapped = StartsWith(header, "PK\x03\x04"u8);

        if (LooksLikeWiiDisc(header))
        {
            var status = $"Mounted as raw Wii disc image; file carving and raw export are available. {wiiKeyStatus}";
            partitions.Add(CreateWholeImagePartition(activePath, length, "Nintendo Wii optical image", status));
        }
        else if (StartsWith(header, "WBFS"u8))
        {
            var status = $"Mounted as raw WBFS container; file carving and raw export are available. {wiiKeyStatus}";
            partitions.Add(CreateWholeImagePartition(activePath, length, "Nintendo Wii WBFS", status));
        }
        else if (TryOpenNdsRom(activePath, length, out var ndsVolume, out var ndsStatus))
        {
            partitions.Add(new PartitionModel(ndsVolume, ndsStatus));
        }
        else if (LooksLikeFat16(header))
        {
            var candidate = new GenericPartitionCandidate(0, Guid.Empty, 0, length, "Nintendo CTR/DSi FAT16 image", SectorSize);
            var volume = Fat16Volume.Open(activePath, candidate);
            partitions.Add(new PartitionModel(volume, $"Mounted Nintendo FAT16 image, {volume.GetRoot().Count:N0} root entries. Metadata scan, deleted FAT entries, carving, and export are available."));
        }
        else if (NintendoNandCrypto.TryOpen3dsNand(activePath, keyPath, partitions, temporaryPartitionPaths, out _)
                 || TryCreate3dsNcsdPartitions(activePath, length, header, partitions))
        {
            // NCSD/CCI/3DS NAND partition table was added.
        }
        else if (LooksLikeNcch(header))
        {
            partitions.Add(CreateWholeImagePartition(activePath, length, "Nintendo 3DS NCCH", "Mounted as raw NCCH/CXI/CFA container; export and file carving are available. Encrypted sections require external keys/tools."));
        }
        else if (TryCreateRvtHPartitions(activePath, stream, length, partitions))
        {
            // RVT-H banks were detected and added.
        }
        else if (LooksLikeWiiNand(length))
        {
            if (WiiNandVolume.TryOpen(activePath, keyPath, out var wiiNandVolume, out var wiiNandStatus))
            {
                partitions.Add(new PartitionModel(wiiNandVolume, wiiNandStatus));
            }
            else
            {
                AddWiiNandPartitions(activePath, length, partitions, $"{wiiKeyStatus} {wiiNandStatus}");
            }
        }
        else if (LooksLikeDsiNand(length))
        {
            if (!NintendoNandCrypto.TryOpenDsiNand(activePath, partitions, temporaryPartitionPaths, out _))
            {
                AddDsiNandPartitions(activePath, length, partitions);
            }
        }
        else if (wfsInfo.IsValid || LooksLikeWiiUStorage(header) || allowRawWiiUCandidate)
        {
            if (WiiUWfsVolume.TryOpen(activePath, keyPath, out var wfsVolume, out var wfsStatus))
            {
                partitions.Add(new PartitionModel(wfsVolume, wfsStatus));
            }
            else
            {
                var status = wfsInfo.IsValid
                    ? $"{wfsInfo.Detail}; managed WFS mount unavailable: {wfsStatus}. Raw export and partition carving are available."
                    : zipWrapped
                        ? $"Detected ZIP-wrapped Wii U dev HDD image. The raw .img must be extracted before WFS validation/browsing; temp space was not sufficient or the archive has no central directory. {keyStatus} Raw carving of the compressed wrapper is not useful."
                        : $"Detected Wii U WFS/dev storage candidate. {keyStatus} {wfsInfo.Detail} Managed WFS mount unavailable: {wfsStatus}. Raw carving is available.";
                partitions.Add(CreateWholeImagePartition(activePath, length, "Nintendo Wii U WFS", status));
            }
        }

        if (partitions.Count == 0)
        {
            if (temporaryPath != null)
            {
                TryDeleteTemporary(temporaryPath);
            }

            foreach (var path in temporaryPartitionPaths)
            {
                TryDeleteTemporary(path);
            }

            throw new InvalidDataException("No Nintendo Wii/Wii U/DS/3DS storage signatures were found.");
        }

        return new NintendoStorageImage(sourcePath, activePath, temporaryPath, temporaryPartitionPaths, partitions);
    }

    public void Dispose()
    {
        if (!string.IsNullOrWhiteSpace(_temporarySourcePath))
        {
            TryDeleteTemporary(_temporarySourcePath);
        }

        foreach (var path in _temporaryPartitionPaths)
        {
            TryDeleteTemporary(path);
        }
    }

    private static PartitionModel CreateWholeImagePartition(string sourcePath, long length, string family, string status)
    {
        var candidate = new GenericPartitionCandidate(0, Guid.Empty, 0, length, family, SectorSize);
        var volume = new RawConsoleVolume(sourcePath, candidate, family, status, CreateWholeImageRoot(sourcePath, length, family));
        return new PartitionModel(volume, status);
    }

    private static IEnumerable<GenericFileSystemEntry> CreateWholeImageRoot(string sourcePath, long length, string family)
    {
        var partition = new GenericPartitionCandidate(0, Guid.Empty, 0, length, family, SectorSize);
        var volume = new RawConsoleVolume(sourcePath, partition, family, "Raw image export");
        yield return new GenericFileSystemEntry
        {
            Volume = volume,
            Path = "/" + Path.GetFileName(sourcePath),
            Name = Path.GetFileName(sourcePath),
            Kind = "Raw Image",
            IsDirectory = false,
            Length = length,
            Offset = 0,
            Cluster = 0,
            Attributes = "raw",
            MetadataStatus = "Whole image raw export entry",
            Extents = [new FileExtent(0, length)]
        };
    }

    private static bool LooksLikeWiiDisc(ReadOnlySpan<byte> header)
    {
        if (header.Length < 0x20)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(header[0x18..]);
        return magic is 0x5D1C9EA3 or 0xC2339F3D;
    }

    private static bool LooksLikeWiiUStorage(ReadOnlySpan<byte> header)
    {
        return StartsWith(header, "WFS"u8) || StartsWith(header, "SFFS"u8) || StartsWith(header, "WUX0"u8);
    }

    private static bool LooksLikeNcch(ReadOnlySpan<byte> header)
    {
        return header.Length >= 0x104 && StartsWith(header[0x100..], "NCCH"u8);
    }

    private static bool LooksLikeFat16(ReadOnlySpan<byte> header)
    {
        return header.Length >= 0x200
               && header[510] == 0x55
               && header[511] == 0xAA
               && Encoding.ASCII.GetString(header.Slice(54, 8)) == "FAT16   ";
    }

    private static bool LooksLikeNcsd(ReadOnlySpan<byte> header)
    {
        return header.Length >= 0x104 && StartsWith(header[0x100..], "NCSD"u8);
    }

    private static bool LooksLikeWiiNand(long length)
    {
        return length is WiiNandSize or WiiNandWithSpareSize;
    }

    private static bool LooksLikeDsiNand(long length)
    {
        return length is DsiNandSize or DsiTrimmedNandSize or DsiTrimmedNandSize + 64;
    }

    private static bool TryOpenNdsRom(string sourcePath, long length, out NdsRomVolume volume, out string status)
    {
        volume = null!;
        status = string.Empty;
        try
        {
            if (NdsRomVolume.TryOpen(sourcePath, length, out volume, out status))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            status = ex.Message;
        }

        return false;
    }

    private static bool TryCreate3dsNcsdPartitions(string sourcePath, long length, ReadOnlySpan<byte> header, List<PartitionModel> partitions)
    {
        if (!LooksLikeNcsd(header))
        {
            return false;
        }

        var found = false;
        for (var index = 0; index < 8; index++)
        {
            var entryOffset = 0x120 + index * 8;
            if (entryOffset + 8 > header.Length)
            {
                break;
            }

            var offsetUnits = BinaryPrimitives.ReadUInt32LittleEndian(header[entryOffset..]);
            var lengthUnits = BinaryPrimitives.ReadUInt32LittleEndian(header[(entryOffset + 4)..]);
            if (offsetUnits == 0 || lengthUnits == 0)
            {
                continue;
            }

            var offset = offsetUnits * (long)NcsdMediaUnitSize;
            var partitionLength = lengthUnits * (long)NcsdMediaUnitSize;
            if (offset < 0 || partitionLength <= 0 || offset >= length)
            {
                continue;
            }

            partitionLength = Math.Min(partitionLength, length - offset);
            var candidate = new GenericPartitionCandidate((uint)(index + 1), Guid.Empty, offset, partitionLength, $"NCSD partition {index}", SectorSize);
            var status = "3DS NCSD partition detected; raw export and partition-scoped carving are available. Contents may be encrypted.";
            partitions.Add(new PartitionModel(new RawConsoleVolume(sourcePath, candidate, "Nintendo 3DS NCSD", status, CreateRawEntry(sourcePath, candidate, "NCSD Partition")), status));
            found = true;
        }

        if (!found)
        {
            partitions.Add(CreateWholeImagePartition(sourcePath, length, "Nintendo 3DS NCSD", "3DS NCSD/CCI/NAND header detected, but no valid partition entries were found; raw export and carving are available."));
        }

        return true;
    }

    private static bool TryCreateRvtHPartitions(string sourcePath, FileStream stream, long length, List<PartitionModel> partitions)
    {
        if (length <= RvtHBankTableOffset)
        {
            return false;
        }

        Span<byte> probe = stackalloc byte[0x20];
        var bankIndex = 1U;
        var bankRows = new List<PartitionModel>();
        var scanEnd = Math.Min(length, RvtHBankTableOffset + RvtHScanStep + RvtHInitialScanLength);
        for (var offset = RvtHBankTableOffset + RvtHScanStep; offset + probe.Length <= scanEnd; offset += RvtHScanStep)
        {
            if (!GenericFileSystemImage.ReadExactly(stream, offset, probe) || !LooksLikeWiiDisc(probe))
            {
                continue;
            }

            var magic = BinaryPrimitives.ReadUInt32BigEndian(probe[0x18..]);
            var discLength = magic == 0xC2339F3D ? GameCubeDiscSize : WiiSingleLayerDiscSize;
            discLength = Math.Min(discLength, length - offset);
            var name = magic == 0xC2339F3D ? $"RVT-H GameCube bank {bankIndex}" : $"RVT-H Wii bank {bankIndex}";
            var candidate = new GenericPartitionCandidate(bankIndex, Guid.Empty, offset, discLength, name, SectorSize);
            var status = "RVT-H disc bank detected from disc header; raw export and partition-scoped carving are available.";
            bankRows.Add(new PartitionModel(new RawConsoleVolume(sourcePath, candidate, "Nintendo Wii RVT-H", status, CreateRawEntry(sourcePath, candidate, "RVT-H Disc Bank")), status));
            bankIndex++;
        }

        if (bankRows.Count == 0)
        {
            return false;
        }

        var bankTableLength = Math.Min(RvtHScanStep, length - RvtHBankTableOffset);
        var tableCandidate = new GenericPartitionCandidate(0, Guid.Empty, RvtHBankTableOffset, bankTableLength, "RVT-H bank table", SectorSize);
        var tableStatus = "Nintendo Wii RVT-H bank table area; raw export is available.";
        partitions.Add(new PartitionModel(new RawConsoleVolume(sourcePath, tableCandidate, "Nintendo Wii RVT-H", tableStatus, CreateRawEntry(sourcePath, tableCandidate, "RVT-H Metadata")), tableStatus));
        partitions.AddRange(bankRows);
        return true;
    }

    private static void AddWiiNandPartitions(string sourcePath, long length, List<PartitionModel> partitions, string keyStatus)
    {
        AddRawPartition(sourcePath, partitions, "Nintendo Wii NAND", "Wii NAND boot blocks", 0, Math.Min(length, 0x100000), $"Wii NAND boot area; raw export and carving are available. {keyStatus}");
        AddRawPartition(sourcePath, partitions, "Nintendo Wii NAND", "Wii NAND SFFS encrypted data", 0x100000, Math.Max(0, Math.Min(length, 0x1FC00000) - 0x100000), $"Wii NAND SFFS data area; file contents are normally encrypted with per-console NAND keys. {keyStatus}");
        AddRawPartition(sourcePath, partitions, "Nintendo Wii NAND", "Wii NAND SFFS metadata", Math.Max(0, length - 0x200000), Math.Min(length, 0x200000), $"Wii NAND SFFS metadata/superblock area; raw export and carving are available. {keyStatus}");
    }

    private static void AddDsiNandPartitions(string sourcePath, long length, List<PartitionModel> partitions)
    {
        AddRawPartition(sourcePath, partitions, "Nintendo DSi NAND", "DSi NAND boot/stage2", 0, Math.Min(length, DsiMainFatOffset), "DSi NAND boot/stage2 area; most sectors are console-key encrypted.");
        AddRawPartition(sourcePath, partitions, "Nintendo DSi NAND", "DSi main FAT32 candidate", DsiMainFatOffset, Math.Max(0, Math.Min(length, DsiPhotoFatOffset) - DsiMainFatOffset), "DSi main FAT32 partition candidate at the documented NAND offset; encrypted dumps need console keys.");
        AddRawPartition(sourcePath, partitions, "Nintendo DSi NAND", "DSi photo FAT32 candidate", DsiPhotoFatOffset, Math.Max(0, Math.Min(length, DsiUserDataLimit) - DsiPhotoFatOffset), "DSi photo FAT32 partition candidate at the documented NAND offset; encrypted dumps need console keys.");
        AddRawPartition(sourcePath, partitions, "Nintendo DSi NAND", "DSi reserved/wear leveling", DsiUserDataLimit, Math.Max(0, length - DsiUserDataLimit), "DSi reserved wear-leveling area.");
    }

    private static void AddRawPartition(string sourcePath, List<PartitionModel> partitions, string family, string name, long offset, long length, string status)
    {
        if (length <= 0)
        {
            return;
        }

        var candidate = new GenericPartitionCandidate((uint)(partitions.Count + 1), Guid.Empty, offset, length, name, SectorSize);
        partitions.Add(new PartitionModel(new RawConsoleVolume(sourcePath, candidate, family, status, CreateRawEntry(sourcePath, candidate, "Raw Region")), status));
    }

    private static byte[] ReadHeader(FileStream stream, int length)
    {
        var buffer = new byte[(int)Math.Min(length, stream.Length)];
        stream.Position = 0;
        _ = stream.Read(buffer, 0, buffer.Length);
        return buffer;
    }

    private static IEnumerable<GenericFileSystemEntry> CreateRawEntry(string sourcePath, GenericPartitionCandidate partition, string kind)
    {
        var volume = new RawConsoleVolume(sourcePath, partition, partition.Name, "Raw region export");
        yield return new GenericFileSystemEntry
        {
            Volume = volume,
            Path = "/" + partition.Name,
            Name = partition.Name,
            Kind = kind,
            IsDirectory = false,
            Length = partition.Length,
            Offset = partition.Offset,
            Cluster = 0,
            Attributes = "raw",
            MetadataStatus = "Raw export entry",
            Extents = [new FileExtent(partition.Offset, partition.Length)]
        };
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
    {
        return value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);
    }

    private static bool TryExtractSingleImageEntry(string archivePath, out string extractedPath)
    {
        extractedPath = string.Empty;
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.Entries
                .Where(item => item.Length > 0)
                .OrderByDescending(item => IsImageExtension(item.Name))
                .ThenByDescending(item => item.Length)
                .FirstOrDefault();
            if (entry == null)
            {
                return false;
            }

            if (entry.Length > 0 && !HasEnoughTemporarySpace(entry.Length))
            {
                return false;
            }

            extractedPath = Path.Combine(Path.GetTempPath(), $"drive-assistant-nintendo-{Guid.NewGuid():N}{Path.GetExtension(entry.Name)}");
            entry.ExtractToFile(extractedPath);
            return true;
        }
        catch (InvalidDataException)
        {
            return TryExtractLocalZipEntry(archivePath, out extractedPath);
        }
    }

    private static bool TryExtractLocalZipEntry(string archivePath, out string extractedPath)
    {
        extractedPath = string.Empty;
        try
        {
            using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[30];
            if (input.Read(header) != header.Length || BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034B50)
            {
                return false;
            }

            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var nameBytes = new byte[nameLength];
            if (input.Read(nameBytes) != nameBytes.Length)
            {
                return false;
            }

            var entryName = System.Text.Encoding.UTF8.GetString(nameBytes);
            if (!IsImageExtension(entryName))
            {
                return false;
            }

            var extra = new byte[extraLength];
            if (extra.Length > 0 && input.Read(extra) != extra.Length)
            {
                return false;
            }

            var uncompressedSize = TryReadZip64UncompressedSize(extra);
            if (uncompressedSize > 0 && !HasEnoughTemporarySpace(uncompressedSize))
            {
                return false;
            }

            extractedPath = Path.Combine(Path.GetTempPath(), $"drive-assistant-nintendo-{Guid.NewGuid():N}{Path.GetExtension(entryName)}");
            using var output = new FileStream(extractedPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            if (method == 0)
            {
                input.CopyTo(output);
                return true;
            }

            if (method != 8)
            {
                TryDeleteTemporary(extractedPath);
                extractedPath = string.Empty;
                return false;
            }

            using var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false);
            deflate.CopyTo(output);
            return true;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(extractedPath))
            {
                TryDeleteTemporary(extractedPath);
            }

            extractedPath = string.Empty;
            return false;
        }
    }

    private static long TryReadZip64UncompressedSize(ReadOnlySpan<byte> extra)
    {
        var offset = 0;
        while (offset + 4 <= extra.Length)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(extra[offset..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[(offset + 2)..]);
            offset += 4;
            if (offset + size > extra.Length)
            {
                break;
            }

            if (tag == 0x0001 && size >= 8)
            {
                var value = BinaryPrimitives.ReadUInt64LittleEndian(extra[offset..]);
                return value <= long.MaxValue ? (long)value : long.MaxValue;
            }

            offset += size;
        }

        return 0;
    }

    private static bool HasEnoughTemporarySpace(long requiredBytes)
    {
        var root = Path.GetPathRoot(Path.GetTempPath());
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var drive = new DriveInfo(root);
        return drive.AvailableFreeSpace > requiredBytes + 1024L * 1024 * 1024;
    }

    private static bool IsImageExtension(string name)
    {
        return Path.GetExtension(name).ToLowerInvariant() is ".img" or ".bin" or ".raw" or ".iso" or ".wbfs" or ".wud" or ".wux" or ".nds" or ".dsi" or ".3ds" or ".cci" or ".cxi" or ".cfa" or ".csu" or ".app";
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}

internal sealed class NdsRomVolume : GenericFileSystemVolume
{
    private const int HeaderSize = 0x200;
    private const int SectorSize = 512;
    private const int DirectoryIdBase = 0xF000;
    private readonly List<GenericFileSystemEntry> _root = [];

    private NdsRomVolume(string sourcePath, GenericPartitionCandidate partition)
        : base(sourcePath, partition, "Nintendo DS NitroFS")
    {
    }

    public override long ClusterSize => SectorSize;

    public override long UsedSpace => Walk(_root).Where(entry => !entry.IsDirectory).Sum(entry => entry.Length);

    public static bool TryOpen(string sourcePath, long length, out NdsRomVolume volume, out string status)
    {
        volume = null!;
        status = string.Empty;
        if (length < HeaderSize)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        if (!GenericFileSystemImage.ReadExactly(stream, 0, header) || !LooksLikeNdsHeader(header, length))
        {
            return false;
        }

        var fntOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[0x40..]);
        var fntSize = BinaryPrimitives.ReadUInt32LittleEndian(header[0x44..]);
        var fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[0x48..]);
        var fatSize = BinaryPrimitives.ReadUInt32LittleEndian(header[0x4C..]);
        if (!RangeValid(fntOffset, fntSize, length) || !RangeValid(fatOffset, fatSize, length) || fatSize % 8 != 0)
        {
            return false;
        }

        var candidate = new GenericPartitionCandidate(0, Guid.Empty, 0, length, ReadTitle(header), SectorSize);
        volume = new NdsRomVolume(sourcePath, candidate);
        volume.LoadNitroFs(stream, fntOffset, fntSize, fatOffset, fatSize);
        status = $"Mounted Nintendo DS NitroFS ROM, {volume.GetRoot().Count:N0} root entries";
        return true;
    }

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        progress?.Report(100);
        return [];
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return Offset + cluster * ClusterSize;
    }

    private void LoadNitroFs(FileStream stream, uint fntOffset, uint fntSize, uint fatOffset, uint fatSize)
    {
        var fnt = new byte[fntSize];
        var fat = new byte[fatSize];
        if (!GenericFileSystemImage.ReadExactly(stream, fntOffset, fnt) || !GenericFileSystemImage.ReadExactly(stream, fatOffset, fat))
        {
            return;
        }

        var directoryCount = BinaryPrimitives.ReadUInt16LittleEndian(fnt.AsSpan(6));
        if (directoryCount == 0 || directoryCount > 4096 || directoryCount * 8 > fnt.Length)
        {
            return;
        }

        var directories = new NdsDirectory[directoryCount];
        for (var index = 0; index < directories.Length; index++)
        {
            var entry = fnt.AsSpan(index * 8, 8);
            directories[index] = new NdsDirectory(
                BinaryPrimitives.ReadUInt32LittleEndian(entry),
                BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]));
        }

        LoadDirectory(DirectoryIdBase, "/", _root, fnt, fat, directories, new HashSet<int>());
    }

    private void LoadDirectory(
        int directoryId,
        string path,
        List<GenericFileSystemEntry> rows,
        ReadOnlySpan<byte> fnt,
        ReadOnlySpan<byte> fat,
        IReadOnlyList<NdsDirectory> directories,
        HashSet<int> visited)
    {
        var index = directoryId - DirectoryIdBase;
        if (index < 0 || index >= directories.Count || !visited.Add(index))
        {
            return;
        }

        var directory = directories[index];
        var offset = checked((int)directory.SubtableOffset);
        var fileId = directory.FirstFileId;
        while (offset >= 0 && offset < fnt.Length)
        {
            var typeAndLength = fnt[offset++];
            if (typeAndLength == 0)
            {
                return;
            }

            var isDirectory = (typeAndLength & 0x80) != 0;
            var nameLength = typeAndLength & 0x7F;
            if (nameLength == 0 || offset + nameLength > fnt.Length)
            {
                return;
            }

            var name = Encoding.ASCII.GetString(fnt.Slice(offset, nameLength));
            offset += nameLength;
            if (isDirectory)
            {
                if (offset + 2 > fnt.Length)
                {
                    return;
                }

                var childId = BinaryPrimitives.ReadUInt16LittleEndian(fnt[offset..]);
                offset += 2;
                var child = CreateDirectoryEntry(name, CombinePath(path, name));
                rows.Add(child);
                LoadDirectory(childId, child.Path, child.Children, fnt, fat, directories, visited);
                continue;
            }

            var fatEntryOffset = fileId * 8;
            fileId++;
            if (fatEntryOffset + 8 > fat.Length)
            {
                continue;
            }

            var start = BinaryPrimitives.ReadUInt32LittleEndian(fat.Slice(fatEntryOffset, 4));
            var end = BinaryPrimitives.ReadUInt32LittleEndian(fat.Slice(fatEntryOffset + 4, 4));
            if (end <= start || end > Length)
            {
                continue;
            }

            rows.Add(CreateFileEntry(name, CombinePath(path, name), start, end - start, fileId - 1));
        }
    }

    private GenericFileSystemEntry CreateDirectoryEntry(string name, string path)
    {
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = path,
            Name = name,
            Kind = "Directory",
            IsDirectory = true,
            Length = 0,
            Offset = 0,
            Cluster = 0,
            Attributes = "NitroFS directory",
            MetadataStatus = "Active Nintendo DS NitroFS directory entry",
            Extents = []
        };
    }

    private GenericFileSystemEntry CreateFileEntry(string name, string path, uint offset, uint length, int fileId)
    {
        return new GenericFileSystemEntry
        {
            Volume = this,
            Path = path,
            Name = name,
            Kind = "File",
            IsDirectory = false,
            Length = length,
            Offset = offset,
            Cluster = (uint)fileId,
            Attributes = $"NitroFS file id {fileId}",
            MetadataStatus = "Active Nintendo DS NitroFS file entry",
            Extents = [new FileExtent(offset, length)]
        };
    }

    private static bool LooksLikeNdsHeader(ReadOnlySpan<byte> header, long length)
    {
        if (header.Length < HeaderSize)
        {
            return false;
        }

        var fntOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[0x40..]);
        var fntSize = BinaryPrimitives.ReadUInt32LittleEndian(header[0x44..]);
        var fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[0x48..]);
        var fatSize = BinaryPrimitives.ReadUInt32LittleEndian(header[0x4C..]);
        var romSize = BinaryPrimitives.ReadUInt32LittleEndian(header[0x80..]);
        if (!RangeValid(fntOffset, fntSize, length) || !RangeValid(fatOffset, fatSize, length) || fatSize == 0 || fatSize % 8 != 0)
        {
            return false;
        }

        if (romSize != 0 && romSize > length + 0x100000)
        {
            return false;
        }

        return IsAscii(header[..0x10]);
    }

    private static bool RangeValid(uint offset, uint size, long length)
    {
        return offset >= HeaderSize && size > 0 && offset <= length && offset + (long)size <= length;
    }

    private static bool IsAscii(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            if (value == 0)
            {
                continue;
            }

            if (value < 0x20 || value > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadTitle(ReadOnlySpan<byte> header)
    {
        var title = Encoding.ASCII.GetString(header[..12]).TrimEnd('\0', ' ');
        var code = Encoding.ASCII.GetString(header.Slice(12, 4)).TrimEnd('\0', ' ');
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Nintendo DS ROM";
        }

        return string.IsNullOrWhiteSpace(code) ? title : $"{title} [{code}]";
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

    private readonly record struct NdsDirectory(uint SubtableOffset, ushort FirstFileId, ushort ParentId);
}
