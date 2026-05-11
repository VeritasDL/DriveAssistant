using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FATXTools.Wpf;

internal static class PlayStationPortableStorageImage
{
    private const int SectorSize = 512;
    private const int PspNandPageSize = 512;
    private const int PspNandSpareSize = 16;
    private const int PspNandPhysicalPageSize = PspNandPageSize + PspNandSpareSize;
    private const int PspNandPagesPerBlock = 32;
    private const int PspNandBlockSize = PspNandPageSize * PspNandPagesPerBlock;
    private const int PspNandPhysicalBlockSize = PspNandPhysicalPageSize * PspNandPagesPerBlock;
    private static readonly long[] PspMappedFatOffsets = [0x0000C000, 0x0180C000, 0x01C0C000, 0x01D0C000];

    public static bool TryOpen(
        string sourcePath,
        long length,
        ReadOnlySpan<byte> header,
        List<string> temporaryPaths,
        out IReadOnlyList<PartitionModel> partitions)
    {
        if (TryOpenVitaPlaintextImage(sourcePath, length, header, out partitions))
        {
            return true;
        }

        var pspSourcePath = sourcePath;
        var pspLength = length;
        if (TryMaterializePspPhysicalNand(sourcePath, length, out var mappedPath, out var mappedLength, out var mapStatus))
        {
            pspSourcePath = mappedPath;
            pspLength = mappedLength;
            temporaryPaths.Add(mappedPath);
        }
        else
        {
            mapStatus = "logical/mapped dump";
        }

        if (TryOpenPspNandImage(pspSourcePath, pspLength, mapStatus, out partitions))
        {
            return true;
        }

        partitions = [];
        return false;
    }

    private static bool TryOpenPspNandImage(string sourcePath, long length, string mapStatus, out IReadOnlyList<PartitionModel> partitions)
    {
        var rows = new List<PartitionModel>();
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        foreach (var candidate in DiscoverPspFat12Candidates(stream, length))
        {
            try
            {
                var volume = Fat12Volume.Open(sourcePath, candidate);
                var status = $"Mounted PSP NAND FAT12 {candidate.Name}, {mapStatus}, {volume.GetRoot().Count:N0} root entries";
                rows.Add(new PartitionModel(volume, status));
            }
            catch
            {
                // Continue probing other PSP flash slots.
            }
        }

        partitions = rows;
        return rows.Count > 0;
    }

    private static IEnumerable<GenericPartitionCandidate> DiscoverPspFat12Candidates(Stream stream, long length)
    {
        var seen = new HashSet<long>();
        foreach (var offset in PspMappedFatOffsets)
        {
            if (offset < length && TryCreateFat12Candidate(stream, offset, (uint)seen.Count + 1, $"PSP flash{seen.Count}", out var candidate) && seen.Add(offset))
            {
                yield return candidate;
            }
        }

        var scanLength = Math.Min(length, 64L * 1024 * 1024);
        for (long offset = 0; offset + SectorSize <= scanLength; offset += SectorSize)
        {
            if (seen.Contains(offset))
            {
                continue;
            }

            if (TryCreateFat12Candidate(stream, offset, (uint)seen.Count + 1, $"PSP FAT12 @ 0x{offset:X}", out var candidate)
                && seen.Add(offset))
            {
                yield return candidate;
            }
        }
    }

    private static bool TryCreateFat12Candidate(Stream stream, long offset, uint index, string name, out GenericPartitionCandidate candidate)
    {
        candidate = new GenericPartitionCandidate(index, Guid.Empty, offset, 0, name);
        Span<byte> boot = stackalloc byte[SectorSize];
        if (!GenericFileSystemImage.ReadExactly(stream, offset, boot) || boot[510] != 0x55 || boot[511] != 0xAA)
        {
            return false;
        }

        var fatType = Encoding.ASCII.GetString(boot.Slice(54, 8));
        if (fatType != "FAT12   ")
        {
            return false;
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
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096)
            || sectorsPerCluster == 0
            || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0
            || reservedSectors == 0
            || fatCount is not (1 or 2)
            || rootEntryCount == 0
            || sectorsPerFat == 0
            || totalSectors == 0)
        {
            return false;
        }

        candidate = new GenericPartitionCandidate(index, Guid.Empty, offset, (long)totalSectors * bytesPerSector, name);
        return true;
    }

    private static bool TryMaterializePspPhysicalNand(string sourcePath, long length, out string mappedPath, out long mappedLength, out string status)
    {
        mappedPath = string.Empty;
        mappedLength = 0;
        status = string.Empty;
        if (length < PspNandPhysicalBlockSize || length % PspNandPhysicalBlockSize != 0)
        {
            return false;
        }

        var physicalBlockCount = checked((int)(length / PspNandPhysicalBlockSize));
        var blocksByLogical = new SortedDictionary<ushort, long>();
        using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess))
        {
            Span<byte> spare = stackalloc byte[PspNandSpareSize];
            for (var physicalBlock = 0; physicalBlock < physicalBlockCount; physicalBlock++)
            {
                var spareOffset = physicalBlock * (long)PspNandPhysicalBlockSize + PspNandPageSize;
                if (!GenericFileSystemImage.ReadExactly(input, spareOffset, spare))
                {
                    return false;
                }

                var marker = spare[4];
                var valid = spare[5];
                var logicalBlock = BinaryPrimitives.ReadUInt16LittleEndian(spare[6..]);
                if (marker == 0x00 && valid == 0xFF && logicalBlock != 0xFFFF && !blocksByLogical.ContainsKey(logicalBlock))
                {
                    blocksByLogical.Add(logicalBlock, physicalBlock * (long)PspNandPhysicalBlockSize);
                }
            }
        }

        if (blocksByLogical.Count < 2)
        {
            return false;
        }

        mappedPath = Path.Combine(Path.GetTempPath(), $"drive-assistant-psp-nand-{Guid.NewGuid():N}.bin");
        using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess))
        using (var output = new FileStream(mappedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess))
        {
            var block = new byte[PspNandBlockSize];
            foreach (var pair in blocksByLogical)
            {
                var outputOffset = pair.Key * (long)PspNandBlockSize;
                for (var page = 0; page < PspNandPagesPerBlock; page++)
                {
                    if (!GenericFileSystemImage.ReadExactly(input, pair.Value + page * (long)PspNandPhysicalPageSize, block.AsSpan(page * PspNandPageSize, PspNandPageSize)))
                    {
                        return false;
                    }
                }

                output.Position = outputOffset;
                output.Write(block, 0, block.Length);
            }

            mappedLength = output.Length;
        }

        status = "physical 512+16 NAND remapped through spare logical-block metadata";
        return true;
    }

    private static bool TryOpenVitaPlaintextImage(string sourcePath, long length, ReadOnlySpan<byte> header, out IReadOnlyList<PartitionModel> partitions)
    {
        var rows = new List<PartitionModel>();
        var magic = Encoding.ASCII.GetBytes("Sony Computer Entertainment Inc.");
        if (header.Length < SectorSize || !header[..magic.Length].SequenceEqual(magic) || header[510] != 0x55 || header[511] != 0xAA)
        {
            partitions = [];
            return false;
        }

        for (var index = 0; index < 0x10; index++)
        {
            var entryOffset = 0x50 + index * 0x11;
            var entry = header.Slice(entryOffset, 0x11);
            var startBlock = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var code = entry[8];
            var type = entry[9];
            var active = entry[10];
            if (startBlock == 0 || blockCount == 0)
            {
                continue;
            }

            var offset = (long)startBlock * SectorSize;
            var partitionLength = (long)blockCount * SectorSize;
            if (offset < 0 || offset >= length)
            {
                continue;
            }

            partitionLength = Math.Min(partitionLength, length - offset);
            var name = VitaPartitionName(code, index);
            var candidate = new GenericPartitionCandidate((uint)(index + 1), Guid.Empty, offset, partitionLength, name);
            switch (type)
            {
                case 0x06:
                    TryAddMounted(rows, () => Fat16Volume.Open(sourcePath, candidate), $"Mounted PS Vita plaintext FAT16 {name}, active=0x{active:X2}");
                    break;
                case 0x07:
                    TryAddMounted(rows, () => ExFatVolume.Open(sourcePath, candidate), $"Mounted PS Vita plaintext exFAT {name}, active=0x{active:X2}");
                    break;
                case 0xDA:
                    rows.Add(new PartitionModel(CreateRawVolume(sourcePath, candidate, "Sony PS Vita raw partition", $"PS Vita raw partition {name}, active=0x{active:X2}"), $"Detected PS Vita raw partition {name}"));
                    break;
            }
        }

        partitions = rows;
        return rows.Count > 0;
    }

    private static void TryAddMounted<TVolume>(List<PartitionModel> rows, Func<TVolume> factory, string status)
        where TVolume : GenericFileSystemVolume
    {
        try
        {
            var volume = factory();
            rows.Add(new PartitionModel(volume, $"{status}, {volume.GetRoot().Count:N0} root entries"));
        }
        catch
        {
            // The Vita partition table can also describe encrypted partitions; leave them unmounted.
        }
    }

    private static RawConsoleVolume CreateRawVolume(string sourcePath, GenericPartitionCandidate candidate, string family, string status)
    {
        var volume = new RawConsoleVolume(sourcePath, candidate, family, status);
        volume.AddRootEntries([
            new GenericFileSystemEntry
            {
                Volume = volume,
                Path = "/" + candidate.Name + ".bin",
                Name = candidate.Name + ".bin",
                Kind = "Raw partition",
                Length = candidate.Length,
                Offset = candidate.Offset,
                Attributes = "raw",
                MetadataStatus = status,
                Extents = [new FileExtent(candidate.Offset, candidate.Length)]
            }
        ]);
        return volume;
    }

    private static string VitaPartitionName(byte code, int index)
    {
        return code switch
        {
            0x03 => "os0",
            0x04 => "vs0",
            0x05 => "vd0",
            0x06 => "tm0",
            0x07 => "ur0",
            0x08 => "ux0",
            0x09 => "gro0",
            0x0A => "grw0",
            0x0B => "ud0",
            0x0C => "sa0",
            0x0E => "pd0",
            _ => $"partition-{index + 1:00}"
        };
    }
}
