using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FATXTools.Wpf;

internal sealed class LegacyConsoleStorageImage : IDisposable
{
    private const int SectorSize = 512;
    private const int Ps1MemoryCardSize = 128 * 1024;
    private const int Ps1BlockSize = 8 * 1024;
    private const int Ps1DirectoryFrameSize = 128;
    private static readonly byte[] NintendoLogo =
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83,
        0x00, 0x0C, 0x00, 0x0D, 0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
        0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99, 0xBB, 0xBB, 0x67, 0x63,
        0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E
    ];

    private LegacyConsoleStorageImage(string sourcePath, IReadOnlyList<PartitionModel> partitions)
    {
        SourcePath = sourcePath;
        Partitions = partitions;
    }

    public string SourcePath { get; }

    public IReadOnlyList<PartitionModel> Partitions { get; }

    public static LegacyConsoleStorageImage Open(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Legacy console image was not found.", sourcePath);
        }

        var header = new byte[Math.Min(0x4000, Math.Max(0, (int)Math.Min(info.Length, 0x4000)))];
        using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess))
        {
            stream.Read(header, 0, header.Length);
        }

        if (!TryCreatePartitions(sourcePath, info.Length, header, out var partitions))
        {
            throw new InvalidDataException("No supported legacy console/devkit media signature was found.");
        }

        return new LegacyConsoleStorageImage(sourcePath, partitions);
    }

    public void Dispose()
    {
    }

    private static PartitionModel CreatePartition(string sourcePath, long length, string family, string status, Action<RawConsoleVolume>? configure = null)
    {
        var candidate = new GenericPartitionCandidate(0, Guid.Empty, 0, length, family, SectorSize);
        var volume = new RawConsoleVolume(sourcePath, candidate, family, status);
        configure?.Invoke(volume);
        if (volume.GetRoot().Count == 0)
        {
            volume.AddRootEntries([CreateFileEntry(volume, "/" + Path.GetFileName(sourcePath), Path.GetFileName(sourcePath), "Raw Image", 0, length, "raw", "Whole legacy/devkit media raw export entry")]);
        }

        return new PartitionModel(volume, status);
    }

    private static bool TryCreatePartitions(string path, long length, ReadOnlySpan<byte> header, out IReadOnlyList<PartitionModel> partitions)
    {
        var rows = new List<PartitionModel>();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".cue" or ".iso" or ".bin" or ".gdi"
            && CdIso9660Volume.TryOpen(path, out var cdVolume, out var cdStatus))
        {
            rows.Add(new PartitionModel(cdVolume, cdStatus));
        }

        if (SgiIrixStorageImage.TryOpen(path, length, header, out var sgiPartitions))
        {
            rows.AddRange(sgiPartitions);
        }

        if (TryCreatePartition(path, length, header, out var partition))
        {
            rows.Insert(0, partition);
        }

        partitions = rows;
        return rows.Count > 0;
    }

    private static bool TryCreatePartition(string path, long length, ReadOnlySpan<byte> header, out PartitionModel partition)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".cue")
        {
            partition = null!;
            return false;
        }

        if (LooksLikePs1MemoryCard(header, length) || extension is ".mcr" or ".mcd" or ".psx")
        {
            var status = "PS1/DTL memory-card image detected; directory save-block entries, raw export, and carving are available.";
            partition = CreatePartition(path, length, "Sony PlayStation memory card", status, volume => AddPs1MemoryCardEntries(volume, path, length));
            return true;
        }

        if (LooksLikePs2MemoryCard(header) || extension == ".ps2")
        {
            var status = "PS2 TOOL/TEST-compatible memory-card image detected; raw export and carving are available. Save-block parsing is planned.";
            partition = CreatePartition(path, length, "Sony PlayStation 2 memory card", status);
            return true;
        }

        if (LooksLikeDreamcastIpBin(header))
        {
            var status = "Dreamcast/Katana IP.BIN boot sector detected; raw export and carving are available.";
            partition = CreatePartition(path, length, "Sega Dreamcast Katana GD-ROM boot sector", status);
            return true;
        }

        if (LooksLikeDreamcastCim(header) || extension == ".cim")
        {
            var status = "Dreamcast/Katana CIM/GD image container detected; raw export and carving are available. CIM container filesystem extraction is still limited.";
            partition = CreatePartition(path, length, "Sega Dreamcast Katana CIM image", status);
            return true;
        }

        if (LooksLikeDreamcastFlashDump(header))
        {
            var status = $"Dreamcast/Katana flash partition dump detected; {FormatBytes(length)} raw export and carving are available.";
            var partitionNumber = header.Length >= 18 ? header[16] : (byte)0;
            partition = CreatePartition(path, length, "Sega Dreamcast Katana flash dump", status, volume => AddDreamcastFlashEntries(volume, path, length, partitionNumber));
            return true;
        }

        if (extension == ".gdi")
        {
            var status = "Dreamcast/Katana GD-ROM GDI descriptor detected; track rows are browsable and sidecar track files can be exported when present.";
            partition = CreatePartition(path, length, "Sega Dreamcast Katana GD-ROM image", status, volume => AddDreamcastGdiEntries(volume, path, length));
            return true;
        }

        if (extension == ".cdi")
        {
            var status = "Dreamcast/Katana CDI container detected by extension; raw export and carving are available. DiscJuggler container filesystem parsing is still limited.";
            partition = CreatePartition(path, length, "Sega Dreamcast Katana GD-ROM image", status);
            return true;
        }

        if (extension is ".vmu" or ".vms" or ".dci")
        {
            var status = "Dreamcast VMU/save media detected by extension; raw export and carving are available. VMU filesystem parsing is still limited.";
            partition = CreatePartition(path, length, "Sega Dreamcast VMU save media", status);
            return true;
        }

        if (LooksLikeN64Rom(header) || extension is ".z64" or ".n64" or ".v64" or ".rom")
        {
            var status = DescribeN64Rom(header, length);
            partition = CreatePartition(path, length, "Nintendo 64 Partner-N64 ROM image", status, volume => AddHeaderAndRawEntries(volume, path, length, "n64-header.bin", 0x1000, "N64 ROM Header", "Nintendo 64 boot/header region"));
            return true;
        }

        if (extension is ".sra" or ".eep" or ".fla" or ".mpk")
        {
            var status = DescribeN64Save(extension, length);
            partition = CreatePartition(path, length, "Nintendo 64 Controller Pak/save media", status);
            return true;
        }

        if (LooksLikeGameBoyRom(header) || extension is ".gb" or ".gbc" or ".gba")
        {
            var status = extension == ".gba" ? DescribeGbaRom(header) : DescribeGameBoyRom(header);
            partition = CreatePartition(path, length, "Nintendo Game Boy development ROM image", status, volume => AddHeaderAndRawEntries(volume, path, length, extension == ".gba" ? "gba-header.bin" : "gb-header.bin", extension == ".gba" ? 0xC0 : 0x150, "Cartridge Header", "Nintendo handheld cartridge metadata header"));
            return true;
        }

        if (extension == ".sav")
        {
            var status = $"Nintendo handheld save image detected by extension; {FormatBytes(length)} raw save export and carving are available.";
            partition = CreatePartition(path, length, "Nintendo handheld save media", status);
            return true;
        }

        if (LooksLikeSegaSaturnDisc(header))
        {
            var status = "Sega Saturn disc/system area detected; system-area export and raw carving are available. ISO9660 browsing depends on a plain data track image.";
            partition = CreatePartition(path, length, "Sega Saturn development disc image", status, volume => AddHeaderAndRawEntries(volume, path, length, "saturn-system-area.bin", 0x10000, "Saturn System Area", "Sega Saturn IP.BIN/system-area header"));
            return true;
        }

        partition = null!;
        return false;
    }

    private static void AddPs1MemoryCardEntries(RawConsoleVolume volume, string sourcePath, long length)
    {
        volume.AddRootEntries([CreateFileEntry(volume, "/" + Path.GetFileName(sourcePath), Path.GetFileName(sourcePath), "Raw Image", 0, length, "raw", "Whole PS1 memory-card raw export entry")]);
        if (length < Ps1MemoryCardSize)
        {
            return;
        }

        var directory = new byte[15 * Ps1DirectoryFrameSize];
        using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess))
        {
            stream.Position = Ps1DirectoryFrameSize;
            if (stream.Read(directory, 0, directory.Length) != directory.Length)
            {
                return;
            }
        }

        for (var block = 1; block <= 15; block++)
        {
            var frame = directory.AsSpan((block - 1) * Ps1DirectoryFrameSize, Ps1DirectoryFrameSize);
            var active = IsActivePs1DirectoryFrame(frame);
            var deleted = IsDeletedPs1DirectoryFrame(frame);
            if (!active && !deleted)
            {
                continue;
            }

            var name = ReadAscii(frame.Slice(0x0A, 20));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"block_{block:D2}.psxsave";
            }

            var blocks = Math.Max(1, (int)frame[4]);
            var offset = block * Ps1BlockSize;
            var saveLength = Math.Min(blocks * Ps1BlockSize, Math.Max(0, length - offset));
            if (saveLength <= 0)
            {
                continue;
            }

            var entry = CreateFileEntry(
                volume,
                "/" + name,
                name,
                "PS1 Save Block",
                offset,
                saveLength,
                $"directory state 0x{frame[0]:X2}; blocks {blocks}",
                deleted ? "Deleted PS1 memory-card directory entry" : "Active PS1 memory-card directory entry",
                deleted);
            if (deleted)
            {
                volume.AddDeletedEntries([entry]);
            }
            else
            {
                volume.AddRootEntries([entry]);
            }
        }
    }

    private static void AddDreamcastGdiEntries(RawConsoleVolume volume, string sourcePath, long length)
    {
        volume.AddRootEntries([CreateFileEntry(volume, "/" + Path.GetFileName(sourcePath), Path.GetFileName(sourcePath), "GDI Descriptor", 0, length, "raw descriptor", "Dreamcast GDI text descriptor")]);

        if (!TryParseGdi(sourcePath, out var tracks))
        {
            return;
        }

        var trackDirectory = CreateDirectoryEntry(volume, "/tracks", "tracks", "GDI Tracks", $"Parsed {tracks.Count:N0} GDI track descriptors");
        volume.AddRootEntries([trackDirectory]);
        foreach (var track in tracks)
        {
            var entryName = $"track{track.Number:D2}_{track.FileName}";
            var trackPath = CombinePath(trackDirectory.Path, entryName);
            var sidecarPath = Path.Combine(Path.GetDirectoryName(sourcePath) ?? string.Empty, track.FileName);
            var exists = File.Exists(sidecarPath);
            var trackLength = exists ? new FileInfo(sidecarPath).Length : 0;
            var entry = CreateFileEntry(
                volume,
                trackPath,
                entryName,
                "GDI Track",
                0,
                trackLength,
                $"LBA {track.Lba}; type {track.TrackType}; sector {track.SectorSize}; offset {track.Offset}",
                exists ? "Dreamcast GDI sidecar track file" : "GDI sidecar track file is missing");
            trackDirectory.Children.Add(entry);
            if (exists)
            {
                volume.RegisterExternalSource(entry, sidecarPath);
            }
        }
    }

    private static void AddDreamcastFlashEntries(RawConsoleVolume volume, string sourcePath, long length, byte partitionNumber)
    {
        var partitionName = partitionNumber > 0 ? $"partition-p{partitionNumber}.bin" : "flash-partition.bin";
        volume.AddRootEntries(
        [
            CreateFileEntry(volume, "/" + partitionName, partitionName, "Dreamcast Flash Partition", 0, length, "raw flash", "Katana flash partition raw export"),
            CreateFileEntry(volume, "/" + Path.GetFileName(sourcePath), Path.GetFileName(sourcePath), "Raw Image", 0, length, "raw", "Whole Dreamcast flash dump raw export entry")
        ]);
    }

    private static void AddHeaderAndRawEntries(RawConsoleVolume volume, string sourcePath, long length, string headerName, long headerLength, string headerKind, string headerStatus)
    {
        volume.AddRootEntries(
        [
            CreateFileEntry(volume, "/" + headerName, headerName, headerKind, 0, Math.Min(length, headerLength), "metadata", headerStatus),
            CreateFileEntry(volume, "/" + Path.GetFileName(sourcePath), Path.GetFileName(sourcePath), "Raw Image", 0, length, "raw", "Whole legacy/devkit media raw export entry")
        ]);
    }

    private static GenericFileSystemEntry CreateDirectoryEntry(RawConsoleVolume volume, string path, string name, string kind, string status)
    {
        return new GenericFileSystemEntry
        {
            Volume = volume,
            Path = path,
            Name = name,
            Kind = kind,
            IsDirectory = true,
            Length = 0,
            Offset = 0,
            Cluster = 0,
            Attributes = "directory",
            MetadataStatus = status,
            Extents = []
        };
    }

    private static GenericFileSystemEntry CreateFileEntry(RawConsoleVolume volume, string path, string name, string kind, long offset, long length, string attributes, string status, bool isDeleted = false)
    {
        return new GenericFileSystemEntry
        {
            Volume = volume,
            Path = path,
            Name = isDeleted ? $"_{name.TrimStart('_')}" : name,
            Kind = kind,
            IsDirectory = false,
            Length = length,
            Offset = offset,
            Cluster = 0,
            IsDeleted = isDeleted,
            Attributes = attributes,
            MetadataStatus = status,
            Extents = length > 0 ? [new FileExtent(offset, length)] : []
        };
    }

    private static bool IsActivePs1DirectoryFrame(ReadOnlySpan<byte> frame)
    {
        return frame.Length >= Ps1DirectoryFrameSize
               && frame[0] is 0x51 or 0x52 or 0x53
               && IsMostlyPrintable(frame.Slice(0x0A, 20));
    }

    private static bool IsDeletedPs1DirectoryFrame(ReadOnlySpan<byte> frame)
    {
        return frame.Length >= Ps1DirectoryFrameSize
               && frame[0] is 0xA1 or 0xA2 or 0xA3
               && IsMostlyPrintable(frame.Slice(0x0A, 20));
    }

    private static bool TryParseGdi(string sourcePath, out List<GdiTrack> tracks)
    {
        tracks = [];
        try
        {
            var lines = File.ReadAllLines(sourcePath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
            if (lines.Count < 2 || !int.TryParse(lines[0], out var expectedTracks) || expectedTracks <= 0)
            {
                return false;
            }

            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6
                    || !int.TryParse(parts[0], out var number)
                    || !int.TryParse(parts[1], out var lba)
                    || !int.TryParse(parts[2], out var trackType)
                    || !int.TryParse(parts[3], out var sectorSize)
                    || !int.TryParse(parts[^1], out var offset))
                {
                    return false;
                }

                var fileName = string.Join(' ', parts.Skip(4).Take(parts.Length - 5));
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    return false;
                }

                tracks.Add(new GdiTrack(number, lba, trackType, sectorSize, fileName, offset));
            }

            return tracks.Count == expectedTracks;
        }
        catch (IOException)
        {
            tracks = [];
            return false;
        }
    }

    private static bool LooksLikePs1MemoryCard(ReadOnlySpan<byte> header, long length)
    {
        return length >= 128 * 1024 && header.Length >= 2 && header[0] == (byte)'M' && header[1] == (byte)'C';
    }

    private static bool LooksLikePs2MemoryCard(ReadOnlySpan<byte> header)
    {
        return header.Length >= 27 && Encoding.ASCII.GetString(header[..27]) == "Sony PS2 Memory Card Format";
    }

    private static bool LooksLikeDreamcastIpBin(ReadOnlySpan<byte> header)
    {
        return header.Length >= 16 && Encoding.ASCII.GetString(header[..16]) == "SEGA SEGAKATANA ";
    }

    private static bool LooksLikeDreamcastCim(ReadOnlySpan<byte> header)
    {
        return header.Length >= 12
               && Encoding.ASCII.GetString(header[..4]) == "CIMF"
               && Encoding.ASCII.GetString(header.Slice(8, 4)) == "GDIM";
    }

    private static bool LooksLikeDreamcastFlashDump(ReadOnlySpan<byte> header)
    {
        return header.Length >= 16 && Encoding.ASCII.GetString(header[..16]) == "KATANA_FLASH____";
    }

    private static bool LooksLikeN64Rom(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        return magic is 0x80371240 or 0x37804012 or 0x40123780;
    }

    private static bool LooksLikeGameBoyRom(ReadOnlySpan<byte> header)
    {
        return header.Length >= 0x134 && header.Slice(0x104, NintendoLogo.Length).SequenceEqual(NintendoLogo);
    }

    private static bool LooksLikeSegaSaturnDisc(ReadOnlySpan<byte> header)
    {
        return header.Length >= 0x20 && Encoding.ASCII.GetString(header.Slice(0x10, 15)) == "SEGA SEGASATURN";
    }

    private static string DescribeN64Rom(ReadOnlySpan<byte> header, long length)
    {
        Span<byte> normalized = stackalloc byte[Math.Min(header.Length, 0x40)];
        NormalizeN64Header(header, normalized);
        var title = normalized.Length >= 0x34 ? ReadAscii(normalized.Slice(0x20, 20)) : string.Empty;
        var code = normalized.Length >= 0x3F ? ReadAscii(normalized.Slice(0x3B, 4)) : string.Empty;
        var order = header.Length >= 4 && header[0] == 0x80 ? "big-endian" : header.Length >= 4 && header[0] == 0x37 ? "byte-swapped" : "little-endian";
        var identity = string.IsNullOrWhiteSpace(title) ? "Nintendo 64 ROM/devkit cartridge image" : $"Nintendo 64 ROM/devkit cartridge image: {title}";
        if (!string.IsNullOrWhiteSpace(code))
        {
            identity += $" [{code}]";
        }

        return $"{identity}; {order} header, {FormatBytes(length)} image. Header export, raw export, and carving are available.";
    }

    private static string DescribeN64Save(string extension, long length)
    {
        var kind = extension switch
        {
            ".eep" => "EEPROM save",
            ".sra" => "SRAM save",
            ".fla" => "FlashRAM save",
            ".mpk" => "Controller Pak image",
            _ => "save image"
        };
        return $"Nintendo 64 {kind} detected; {FormatBytes(length)} raw export and carving are available.";
    }

    private static string DescribeGameBoyRom(ReadOnlySpan<byte> header)
    {
        var title = header.Length >= 0x144 ? ReadAscii(header.Slice(0x134, 16)) : string.Empty;
        var cartridgeType = header.Length > 0x147 ? DescribeGameBoyCartridgeType(header[0x147]) : "unknown mapper";
        var romSize = header.Length > 0x148 ? DescribeGameBoyRomSize(header[0x148]) : "unknown ROM size";
        var ramSize = header.Length > 0x149 ? DescribeGameBoyRamSize(header[0x149]) : "unknown RAM size";
        return $"Game Boy/Game Boy Color ROM image detected{(string.IsNullOrWhiteSpace(title) ? string.Empty : $": {title}")}; {cartridgeType}, {romSize}, {ramSize}. Header export, raw export, and carving are available.";
    }

    private static string DescribeGbaRom(ReadOnlySpan<byte> header)
    {
        var title = header.Length >= 0xAC ? ReadAscii(header.Slice(0xA0, 12)) : string.Empty;
        var code = header.Length >= 0xB0 ? ReadAscii(header.Slice(0xAC, 4)) : string.Empty;
        var saveType = DetectGbaSaveType(header);
        var identity = string.IsNullOrWhiteSpace(title) ? "Game Boy Advance ROM image detected" : $"Game Boy Advance ROM image detected: {title}";
        if (!string.IsNullOrWhiteSpace(code))
        {
            identity += $" [{code}]";
        }

        return $"{identity}; save marker {saveType}. Header export, raw export, and carving are available.";
    }

    private static void NormalizeN64Header(ReadOnlySpan<byte> header, Span<byte> normalized)
    {
        header[..normalized.Length].CopyTo(normalized);
        if (header.Length < 4)
        {
            return;
        }

        if (header[0] == 0x37 && header[1] == 0x80)
        {
            for (var index = 0; index + 1 < normalized.Length; index += 2)
            {
                (normalized[index], normalized[index + 1]) = (normalized[index + 1], normalized[index]);
            }
        }
        else if (header[0] == 0x40 && header[1] == 0x12)
        {
            for (var index = 0; index + 3 < normalized.Length; index += 4)
            {
                (normalized[index], normalized[index + 3]) = (normalized[index + 3], normalized[index]);
                (normalized[index + 1], normalized[index + 2]) = (normalized[index + 2], normalized[index + 1]);
            }
        }
    }

    private static string DetectGbaSaveType(ReadOnlySpan<byte> header)
    {
        var text = Encoding.ASCII.GetString(header);
        if (text.Contains("EEPROM_V", StringComparison.Ordinal))
        {
            return "EEPROM";
        }

        if (text.Contains("FLASH1M_V", StringComparison.Ordinal) || text.Contains("FLASH_V", StringComparison.Ordinal))
        {
            return "Flash";
        }

        if (text.Contains("SRAM_V", StringComparison.Ordinal))
        {
            return "SRAM";
        }

        return "not found";
    }

    private static string DescribeGameBoyCartridgeType(byte value)
    {
        return value switch
        {
            0x00 => "ROM only",
            0x01 => "MBC1",
            0x02 => "MBC1+RAM",
            0x03 => "MBC1+RAM+BATTERY",
            0x05 => "MBC2",
            0x06 => "MBC2+BATTERY",
            0x08 => "ROM+RAM",
            0x09 => "ROM+RAM+BATTERY",
            0x0F => "MBC3+TIMER+BATTERY",
            0x10 => "MBC3+TIMER+RAM+BATTERY",
            0x11 => "MBC3",
            0x12 => "MBC3+RAM",
            0x13 => "MBC3+RAM+BATTERY",
            0x19 => "MBC5",
            0x1A => "MBC5+RAM",
            0x1B => "MBC5+RAM+BATTERY",
            0x1C => "MBC5+RUMBLE",
            0x1D => "MBC5+RUMBLE+RAM",
            0x1E => "MBC5+RUMBLE+RAM+BATTERY",
            _ => $"mapper 0x{value:X2}"
        };
    }

    private static string DescribeGameBoyRomSize(byte value)
    {
        return value switch
        {
            <= 0x08 => $"{32 << value} KiB ROM",
            0x52 => "1.1 MiB ROM",
            0x53 => "1.2 MiB ROM",
            0x54 => "1.5 MiB ROM",
            _ => $"ROM size code 0x{value:X2}"
        };
    }

    private static string DescribeGameBoyRamSize(byte value)
    {
        return value switch
        {
            0x00 => "no cartridge RAM",
            0x02 => "8 KiB RAM",
            0x03 => "32 KiB RAM",
            0x04 => "128 KiB RAM",
            0x05 => "64 KiB RAM",
            _ => $"RAM size code 0x{value:X2}"
        };
    }

    private static bool IsMostlyPrintable(ReadOnlySpan<byte> data)
    {
        var printable = 0;
        foreach (var value in data)
        {
            if (value == 0)
            {
                continue;
            }

            if (value is >= 0x20 and <= 0x7E)
            {
                printable++;
            }
        }

        return printable > 0;
    }

    private static string ReadAscii(ReadOnlySpan<byte> data)
    {
        return Encoding.ASCII.GetString(data).TrimEnd('\0', ' ');
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024d):0.##} MiB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0.##} KiB" : $"{bytes} bytes";
    }

    private static string CombinePath(string parent, string name)
    {
        return parent == "/" ? "/" + name : parent.TrimEnd('/') + "/" + name;
    }

    private readonly record struct GdiTrack(int Number, int Lba, int TrackType, int SectorSize, string FileName, int Offset);
}
