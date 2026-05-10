using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FATXTools.Wpf;

internal sealed class LegacyConsoleStorageImage : IDisposable
{
    private const int SectorSize = 512;
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

        if (!TryClassify(sourcePath, info.Length, header, out var family, out var status))
        {
            throw new InvalidDataException("No supported legacy console/devkit media signature was found.");
        }

        var candidate = new GenericPartitionCandidate(0, Guid.Empty, 0, info.Length, family, SectorSize);
        var volume = new RawConsoleVolume(sourcePath, candidate, family, status, CreateRawEntry(sourcePath, candidate, family));
        return new LegacyConsoleStorageImage(sourcePath, [new PartitionModel(volume, status)]);
    }

    public void Dispose()
    {
    }

    private static IEnumerable<GenericFileSystemEntry> CreateRawEntry(string sourcePath, GenericPartitionCandidate candidate, string family)
    {
        var volume = new RawConsoleVolume(sourcePath, candidate, family, "Raw image export");
        yield return new GenericFileSystemEntry
        {
            Volume = volume,
            Path = "/" + Path.GetFileName(sourcePath),
            Name = Path.GetFileName(sourcePath),
            Kind = "Raw Image",
            IsDirectory = false,
            Length = candidate.Length,
            Offset = candidate.Offset,
            Cluster = 0,
            Attributes = "raw",
            MetadataStatus = "Whole legacy/devkit media raw export entry",
            Extents = [new FileExtent(candidate.Offset, candidate.Length)]
        };
    }

    private static bool TryClassify(string path, long length, ReadOnlySpan<byte> header, out string family, out string status)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (LooksLikePs1MemoryCard(header, length) || extension is ".mcr" or ".mcd" or ".psx")
        {
            family = "Sony PlayStation memory card";
            status = "PS1/DTL memory-card image detected; raw export and carving are available. Save-block parsing is planned.";
            return true;
        }

        if (LooksLikePs2MemoryCard(header) || extension == ".ps2")
        {
            family = "Sony PlayStation 2 memory card";
            status = "PS2 TOOL/TEST-compatible memory-card image detected; raw export and carving are available. Save-block parsing is planned.";
            return true;
        }

        if (LooksLikeDreamcastIpBin(header))
        {
            family = "Sega Dreamcast Katana GD-ROM boot sector";
            status = "Dreamcast/Katana IP.BIN boot sector detected; raw export and carving are available.";
            return true;
        }

        if (extension is ".gdi" or ".cdi")
        {
            family = "Sega Dreamcast Katana GD-ROM image";
            status = "Dreamcast/Katana GD-ROM descriptor/container detected by extension; raw export and carving are available. Track-aware GDI/CDI browsing is planned.";
            return true;
        }

        if (extension is ".vmu" or ".vms" or ".dci")
        {
            family = "Sega Dreamcast VMU save media";
            status = "Dreamcast VMU/save media detected by extension; raw export and carving are available. VMU filesystem parsing is planned.";
            return true;
        }

        if (LooksLikeN64Rom(header) || extension is ".z64" or ".n64" or ".v64")
        {
            family = "Nintendo 64 Partner-N64 ROM image";
            status = "Nintendo 64 ROM/devkit cartridge image detected; raw export and carving are available.";
            return true;
        }

        if (extension is ".sra" or ".eep" or ".fla")
        {
            family = "Nintendo 64 Controller Pak/save media";
            status = "Nintendo 64 save media detected by extension; raw export and carving are available.";
            return true;
        }

        if (LooksLikeGameBoyRom(header) || extension is ".gb" or ".gbc" or ".gba")
        {
            family = "Nintendo Game Boy development ROM image";
            status = "Game Boy/Game Boy Color/Game Boy Advance ROM image detected; raw export and carving are available.";
            return true;
        }

        if (extension == ".sav")
        {
            family = "Nintendo handheld save media";
            status = "Nintendo handheld save image detected by extension; raw export and carving are available.";
            return true;
        }

        family = string.Empty;
        status = string.Empty;
        return false;
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
}
