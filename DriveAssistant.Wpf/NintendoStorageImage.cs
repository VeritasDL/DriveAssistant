using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace FATXTools.Wpf;

internal sealed class NintendoStorageImage : IDisposable
{
    private const int SectorSize = 512;
    private readonly string? _temporarySourcePath;

    private NintendoStorageImage(string sourcePath, string activeSourcePath, string? temporarySourcePath, IReadOnlyList<PartitionModel> partitions)
    {
        SourcePath = sourcePath;
        ActiveSourcePath = activeSourcePath;
        _temporarySourcePath = temporarySourcePath;
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
        using var stream = new FileStream(activePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        var header = ReadHeader(stream, 0x8000);
        var keyStatus = WiiUKeyMaterial.TryLoad(keyPath, out var keys, out var loadStatus)
            ? loadStatus
            : loadStatus;
        var wfsInfo = WiiUWfsInspector.Inspect(activePath, keys);
        var zipWrapped = StartsWith(header, "PK\x03\x04"u8);

        if (LooksLikeWiiDisc(header))
        {
            partitions.Add(CreateWholeImagePartition(activePath, length, "Nintendo Wii optical image", "Mounted as raw Wii disc image; file carving and raw export are available."));
        }
        else if (StartsWith(header, "WBFS"u8))
        {
            partitions.Add(CreateWholeImagePartition(activePath, length, "Nintendo Wii WBFS", "Mounted as raw WBFS container; file carving and raw export are available."));
        }
        else if (wfsInfo.IsValid || LooksLikeWiiUStorage(header) || allowRawWiiUCandidate)
        {
            var status = wfsInfo.IsValid
                ? $"{wfsInfo.Detail}; read-only raw export and partition carving are available in this build"
                : zipWrapped
                    ? $"Detected ZIP-wrapped Wii U dev HDD image. The raw .img must be extracted before WFS validation/browsing; temp space was not sufficient or the archive has no central directory. {keyStatus} Raw carving of the compressed wrapper is not useful."
                    : $"Detected Wii U WFS/dev storage candidate. {keyStatus} {wfsInfo.Detail} Raw carving is available.";
            partitions.Add(CreateWholeImagePartition(activePath, length, "Nintendo Wii U WFS", status));
        }

        if (partitions.Count == 0)
        {
            if (temporaryPath != null)
            {
                TryDeleteTemporary(temporaryPath);
            }

            throw new InvalidDataException("No Nintendo Wii/Wii U storage signatures were found.");
        }

        return new NintendoStorageImage(sourcePath, activePath, temporaryPath, partitions);
    }

    public void Dispose()
    {
        if (!string.IsNullOrWhiteSpace(_temporarySourcePath))
        {
            TryDeleteTemporary(_temporarySourcePath);
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
        return StartsWith(header, "WFS"u8) || StartsWith(header, "SFFS"u8);
    }

    private static byte[] ReadHeader(FileStream stream, int length)
    {
        var buffer = new byte[(int)Math.Min(length, stream.Length)];
        stream.Position = 0;
        _ = stream.Read(buffer, 0, buffer.Length);
        return buffer;
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
        return Path.GetExtension(name).ToLowerInvariant() is ".img" or ".bin" or ".raw" or ".iso" or ".wbfs";
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
