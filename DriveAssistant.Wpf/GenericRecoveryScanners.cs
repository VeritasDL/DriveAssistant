using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FATX.Analyzers.Signatures;

namespace FATXTools.Wpf;

public sealed record GenericCarvedFile(
    string Name,
    string Kind,
    string SourcePath,
    long SourceOffset,
    long DisplayOffset,
    long Size,
    string Source,
    string Detail)
{
    public bool HasFileData => Size > 0 && File.Exists(SourcePath);
}

public sealed record GenericCarverMatch(string Name, string Extension, long Size, string Detail);

internal sealed record XvdContainerMetadata(byte HeaderType, string Classification, string? DisplayName, string Detail);

public sealed class GenericFileCarver
{
    private const int HeaderBufferSize = 0x300;
    private const int ScanChunkSize = 0x4000000;
    private readonly string _sourcePath;
    private readonly long _scanStart;
    private readonly long _scanLength;
    private readonly long _displayBaseOffset;
    private readonly long _interval;
    private readonly string _sourceName;
    private readonly ScanProfile _scanProfile;
    private readonly List<CustomSignatureDefinition> _customSignatures = [];

    public GenericFileCarver(string sourcePath, long scanStart, long scanLength, long displayBaseOffset, long interval, string sourceName, ScanProfile scanProfile = ScanProfile.Balanced)
    {
        _sourcePath = sourcePath;
        _scanStart = Math.Max(0, scanStart);
        _scanLength = Math.Max(0, scanLength);
        _displayBaseOffset = displayBaseOffset;
        _interval = Math.Max(1, interval);
        _sourceName = sourceName;
        _scanProfile = scanProfile;
    }

    public void SetCustomSignatures(IEnumerable<CustomSignatureDefinition>? definitions)
    {
        _customSignatures.Clear();
        if (definitions == null)
        {
            return;
        }

        foreach (var definition in definitions)
        {
            if (definition == null || !definition.Enabled)
            {
                continue;
            }

            try
            {
                _ = definition.GetHeaderBytes();
                _customSignatures.Add(definition);
            }
            catch
            {
                // Invalid user entries are ignored by the generic raw carver.
            }
        }
    }

    public List<GenericCarvedFile> Analyze(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var rows = new List<GenericCarvedFile>();
        using var stream = new FileStream(_sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var readableLength = Math.Max(0, Math.Min(_scanLength, stream.Length - _scanStart));
        var steps = Math.Max(1L, readableLength / _interval);
        var progressEvery = Math.Max(1L, steps / 500);
        var buffer = new byte[ScanChunkSize + HeaderBufferSize];

        for (long chunkRelative = 0; chunkRelative < readableLength;)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkLength = Math.Min(ScanChunkSize, readableLength - chunkRelative);
            var readLength = (int)Math.Min(buffer.Length, readableLength - chunkRelative);
            stream.Position = _scanStart + chunkRelative;
            var read = stream.Read(buffer, 0, readLength);
            if (read <= 0)
            {
                break;
            }

            var processLength = Math.Min(chunkLength, read);
            for (long localRelative = 0; localRelative < processLength; localRelative += _interval)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relative = chunkRelative + localRelative;
                var absoluteOffset = _scanStart + relative;
                var headerLength = (int)Math.Min(HeaderBufferSize, read - localRelative);
                if (headerLength <= 0)
                {
                    break;
                }

                GenericCarverMatch? match;
                try
                {
                    match = TryMatch(stream, absoluteOffset, readableLength - relative, buffer.AsSpan((int)localRelative, headerLength));
                    match ??= TryMatchCustom(readableLength - relative, buffer.AsSpan((int)localRelative, headerLength));
                }
                catch
                {
                    match = null;
                }

                if (match != null)
                {
                    var file = new GenericCarvedFile(
                        BuildFileName(relative, match),
                        match.Extension.TrimStart('.').ToUpperInvariant(),
                        _sourcePath,
                        absoluteOffset,
                        _displayBaseOffset + relative,
                        Math.Min(match.Size, readableLength - relative),
                        _sourceName,
                        match.Detail);
                    rows.Add(file);
                    AddNestedContainerRows(rows, stream, file, match, cancellationToken);
                }

                var step = relative / _interval;
                if (step % progressEvery == 0)
                {
                    progress?.Report((int)Math.Min(int.MaxValue, step));
                }
            }

            chunkRelative += processLength;
        }

        progress?.Report((int)Math.Min(int.MaxValue, steps));
        return rows;
    }

    private void AddNestedContainerRows(List<GenericCarvedFile> rows, FileStream stream, GenericCarvedFile outerFile, GenericCarverMatch outerMatch, CancellationToken cancellationToken)
    {
        if (_scanProfile == ScanProfile.Fast || outerFile.Size <= 0)
        {
            return;
        }

        if (!outerMatch.Extension.Equals(".xvd", StringComparison.OrdinalIgnoreCase)
            && !outerMatch.Extension.Equals(".xvc", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var scanLimit = _scanProfile == ScanProfile.Exhaustive
            ? outerFile.Size
            : Math.Min(outerFile.Size, 0x10000000L);
        var nestedRows = FindNestedFileSystems(
            stream,
            outerFile.SourceOffset,
            scanLimit,
            outerFile.DisplayOffset,
            $"{_sourceName} > {outerFile.Name}",
            cancellationToken);
        rows.AddRange(nestedRows);
    }

    private static IReadOnlyList<GenericCarvedFile> FindNestedFileSystems(
        FileStream stream,
        long containerOffset,
        long scanLength,
        long displayBaseOffset,
        string sourceName,
        CancellationToken cancellationToken)
    {
        const long firstCandidateOffset = 0x1000;
        const int probeLength = 0x200;
        const long alignment = 0x1000;

        var rows = new List<GenericCarvedFile>();
        var seen = new HashSet<long>();
        var probe = new byte[probeLength];
        for (var relative = firstCandidateOffset; relative + probeLength <= scanLength; relative += alignment)
        {
            cancellationToken.ThrowIfCancellationRequested();

            stream.Position = containerOffset + relative;
            var read = stream.Read(probe, 0, probe.Length);
            if (read < 0x5A)
            {
                break;
            }

            var match = TryMatchNestedFileSystem(probe.AsSpan(0, read));
            if (match == null || !seen.Add(relative))
            {
                continue;
            }

            var size = Math.Max(0, scanLength - relative);
            rows.Add(new GenericCarvedFile(
                $"{match.Value.Kind.ToLowerInvariant()}_inside_xvd_{relative:X16}.img",
                match.Value.Kind,
                stream.Name,
                containerOffset + relative,
                displayBaseOffset + relative,
                size,
                sourceName,
                $"{match.Value.Detail}; nested inside XVD/XVC container; outer container and nested result are kept as separate rows"));
        }

        return rows;
    }

    private static (string Kind, string Detail)? TryMatchNestedFileSystem(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 0x5A && StartsWith(header[3..], "NTFS    "u8))
        {
            return ("NTFS", "Nested NTFS filesystem boot sector");
        }

        if (header.Length >= 0x5A && StartsWith(header[3..], "EXFAT   "u8))
        {
            return ("EXFAT", "Nested exFAT filesystem boot sector");
        }

        if (header.Length >= 0x5A && StartsWith(header[82..], "FAT32   "u8))
        {
            return ("FAT32", "Nested FAT32 filesystem boot sector");
        }

        if (header.Length >= 4 && StartsWith(header, "FATX"u8))
        {
            return ("FATX", "Nested FATX filesystem header");
        }

        return null;
    }

    private static string BuildFileName(long offset, GenericCarverMatch match)
    {
        var baseName = string.IsNullOrWhiteSpace(match.Name)
            ? $"carved_{offset:X16}"
            : Path.GetFileNameWithoutExtension(match.Name);
        return $"{baseName}_{offset:X16}{match.Extension}";
    }

    private static GenericCarverMatch? TryMatch(FileStream stream, long absoluteOffset, long remainingLength, ReadOnlySpan<byte> header)
    {
        return TryMatchPlayStation(header, stream, absoluteOffset, remainingLength)
               ?? TryMatchXbox(header, stream, absoluteOffset, remainingLength)
               ?? TryMatchCommon(header, stream, absoluteOffset, remainingLength);
    }

    private GenericCarverMatch? TryMatchCustom(long remainingLength, ReadOnlySpan<byte> header)
    {
        foreach (var definition in _customSignatures)
        {
            var headerOffset = definition.HeaderOffset;
            if (headerOffset < 0 || headerOffset > int.MaxValue || headerOffset >= header.Length)
            {
                continue;
            }

            var magic = definition.GetHeaderBytes();
            var start = (int)headerOffset;
            if (magic.Length == 0 || start + magic.Length > header.Length)
            {
                continue;
            }

            if (!header.Slice(start, magic.Length).SequenceEqual(magic))
            {
                continue;
            }

            var extension = string.IsNullOrWhiteSpace(definition.Extension) ? ".bin" : definition.Extension;
            if (!extension.StartsWith(".", StringComparison.Ordinal))
            {
                extension = "." + extension;
            }

            var size = definition.MaxSearchLength > 0
                ? Math.Min(remainingLength, Math.Max(definition.MaxSearchLength, headerOffset + magic.Length))
                : Math.Min(remainingLength, headerOffset + magic.Length);
            var label = string.IsNullOrWhiteSpace(definition.Description)
                ? "Custom file carver signature"
                : definition.Description;
            var detail = string.IsNullOrWhiteSpace(definition.Platform)
                ? label
                : $"{definition.Platform}: {label}";
            return new GenericCarverMatch(definition.Name ?? string.Empty, extension, size, detail);
        }

        return null;
    }

    private static GenericCarverMatch? TryMatchPlayStation(ReadOnlySpan<byte> header, FileStream stream, long absoluteOffset, long remainingLength)
    {
        if (StartsWith(header, "SCE\0"u8) && header.Length >= 0x20)
        {
            var headerLength = ReadUInt64BigEndian(header[0x10..]);
            var bodyLength = ReadUInt64BigEndian(header[0x18..]);
            var size = checked((long)Math.Min((ulong)long.MaxValue, headerLength + bodyLength));
            if (size > 0)
            {
                return new GenericCarverMatch(string.Empty, ".self", size, "PlayStation SELF/PRX executable");
            }
        }

        if (StartsWith(header, "\x7F"u8) && header.Length >= 4 && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
        {
            return new GenericCarverMatch(string.Empty, ".elf", EstimateUnknownSize(remainingLength), "ELF executable; size is estimated");
        }

        if (StartsWith(header, "SCEUF\0\0"u8))
        {
            return new GenericCarverMatch(string.Empty, ".pup", EstimateUnknownSize(remainingLength), "PlayStation PUP update package; size is estimated");
        }

        if (StartsWith(header, "\0PSF"u8) && header.Length >= 0x14)
        {
            var keyTableStart = ReadUInt32LittleEndian(header[8..]);
            var dataTableStart = ReadUInt32LittleEndian(header[12..]);
            var entryCount = ReadUInt32LittleEndian(header[16..]);
            var size = TryGetSfoSize(stream, absoluteOffset, keyTableStart, dataTableStart, entryCount, remainingLength);
            if (size > 0)
            {
                return new GenericCarverMatch("PARAM", ".sfo", size, "PlayStation SFO metadata");
            }
        }

        if (StartsWith(header, "\x7F"u8) && header.Length >= 4 && header[1] == (byte)'C' && header[2] == (byte)'N' && header[3] == (byte)'T')
        {
            var size = TryGetPs4PkgSize(header, remainingLength);
            return new GenericCarverMatch(string.Empty, ".pkg", size > 0 ? size : EstimateUnknownSize(remainingLength), "PS4 package (PKG)");
        }

        if (StartsWith(header, "PFSC"u8))
        {
            return new GenericCarverMatch(string.Empty, ".pfs", EstimateUnknownSize(remainingLength), "PS4 PFS container; size is estimated");
        }

        if (header.Length >= 4 && ReadUInt32BigEndian(header) == 0xDCA24D00)
        {
            var size = header.Length >= 0x10 ? checked((long)ReadUInt64BigEndian(header[8..]) + 0x40) : 0;
            if (size > 0)
            {
                return new GenericCarverMatch(string.Empty, ".trp", size, "PS3 trophy package");
            }
        }

        if (StartsWith(header, "BIK"u8) && header.Length >= 8)
        {
            var size = ReadUInt32LittleEndian(header[4..]) + 8L;
            return new GenericCarverMatch(string.Empty, ".bik", size, "Bink video");
        }

        if (StartsWith(header, "NPD\0"u8) && header.Length >= 0xA0)
        {
            var npdType = header[0x90] & 0x0F;
            var extension = npdType switch
            {
                0 => ".edat",
                1 => ".sdat",
                _ => ".npd"
            };
            var dataLength = checked((long)Math.Min((ulong)long.MaxValue, ReadUInt64BigEndian(header[0x98..])));
            var size = dataLength > 0 ? Math.Min(remainingLength, 0x100 + dataLength) : EstimateUnknownSize(remainingLength);
            return new GenericCarverMatch(string.Empty, extension, size, "PlayStation NPD/EDAT/SDAT content");
        }

        return null;
    }

    private static GenericCarverMatch? TryMatchXbox(ReadOnlySpan<byte> header, FileStream stream, long absoluteOffset, long remainingLength)
    {
        var pdb = TryMatchPdb(header, remainingLength);
        if (pdb != null)
        {
            return pdb;
        }

        var pe = TryMatchPe(header, stream, absoluteOffset, remainingLength);
        if (pe != null)
        {
            return pe;
        }

        if (TryGetXbox360XexMagic(header, out var xexMagic) && header.Length >= 0x20)
        {
            var securityOffset = ReadUInt32BigEndian(header[0x10..]);
            var size = TryGetXexSize(stream, absoluteOffset, securityOffset, remainingLength);
            return new GenericCarverMatch(
                string.Empty,
                ".xex",
                size > 0 ? size : EstimateUnknownSize(remainingLength),
                $"Xbox 360 XEX executable ({xexMagic})");
        }

        if (StartsWith(header, "XBEH"u8) && header.Length >= 0x154)
        {
            var xbe = TryGetXbe(stream, absoluteOffset, remainingLength, header);
            return new GenericCarverMatch(xbe.Name, ".xbe", xbe.Size > 0 ? xbe.Size : EstimateUnknownSize(remainingLength), "Original Xbox executable");
        }

        if ((StartsWith(header, "LIVE"u8) || StartsWith(header, "PIRS"u8) || StartsWith(header, "CON "u8)) && header.Length >= 0x400)
        {
            var magic = Encoding.ASCII.GetString(header[..4]);
            return new GenericCarverMatch(string.Empty, ".stfs", EstimateUnknownSize(remainingLength), $"Xbox 360 STFS package ({magic.Trim()})");
        }

        if (StartsWith(header, "head"u8) && header.Length >= 0x0C)
        {
            var mapSize = TryGetMapSize(stream, absoluteOffset, remainingLength, header);
            if (mapSize > 0)
            {
                return new GenericCarverMatch($"map_{absoluteOffset:X}", ".map", mapSize, "Xbox map file");
            }
        }

        if (header.Length >= 0x209 && StartsWith(header[0x200..], "MSFT-XVD"u8))
        {
            var size = TryGetXvdSize(stream, absoluteOffset, remainingLength);
            var metadata = TryReadXvdMetadata(stream, absoluteOffset, size);
            return new GenericCarverMatch(string.Empty, ".xvd", size, metadata?.Detail ?? "Xbox One/Series XVD package");
        }

        if (StartsWith(header, "crdi-xvc"u8))
        {
            return new GenericCarverMatch(string.Empty, ".xvc", EstimateUnknownSize(remainingLength), "Xbox One/Series XVC container; size is estimated");
        }

        return null;
    }

    private static (long Size, string Name) TryGetXbe(FileStream stream, long absoluteOffset, long remainingLength, ReadOnlySpan<byte> header)
    {
        var baseAddress = ReadUInt32LittleEndian(header[0x104..]);
        var fileSize = ReadUInt32LittleEndian(header[0x10C..]);
        var debugFileNameOffset = ReadUInt32LittleEndian(header[0x150..]);
        var name = string.Empty;

        if (baseAddress > 0 && debugFileNameOffset > baseAddress)
        {
            var nameOffset = debugFileNameOffset - baseAddress;
            if (nameOffset < remainingLength)
            {
                name = ReadAsciiNullTerminated(stream, absoluteOffset + nameOffset, 260);
            }
        }

        var size = fileSize > 0 && fileSize <= remainingLength ? fileSize : 0;
        return (size, name);
    }

    private static long TryGetMapSize(FileStream stream, long absoluteOffset, long remainingLength, ReadOnlySpan<byte> header)
    {
        var fileSize = ReadUInt32LittleEndian(header[8..]);
        if (fileSize < 0x1000 || fileSize > remainingLength)
        {
            return 0;
        }

        Span<byte> tail = stackalloc byte[4];
        stream.Position = absoluteOffset + fileSize - 4;
        if (stream.Read(tail) == tail.Length && StartsWith(tail, "foot"u8))
        {
            return fileSize;
        }

        return 0;
    }

    private static GenericCarverMatch? TryMatchPdb(ReadOnlySpan<byte> header, long remainingLength)
    {
        const string pdbMagic = "Microsoft C/C++ MSF 7.00\r\n\u001ADS\0\0\0";
        if (header.Length < 0x2C || !Encoding.ASCII.GetString(header[..0x20]).Equals(pdbMagic, StringComparison.Ordinal))
        {
            return null;
        }

        var blockSize = ReadUInt32LittleEndian(header[0x20..]);
        var blockCount = ReadUInt32LittleEndian(header[0x28..]);
        var size = blockSize > 0 && blockCount > 0
            ? Math.Min(remainingLength, checked((long)blockSize * blockCount))
            : EstimateUnknownSize(remainingLength);
        return new GenericCarverMatch(string.Empty, ".pdb", size, "Microsoft Program Database symbols");
    }

    private static GenericCarverMatch? TryMatchPe(ReadOnlySpan<byte> header, FileStream stream, long absoluteOffset, long remainingLength)
    {
        if (header.Length < 0x40 || header[0] != (byte)'M' || header[1] != (byte)'Z')
        {
            return null;
        }

        var peOffset = ReadUInt32LittleEndian(header[0x3C..]);
        if (peOffset < 0x40 || peOffset > 0x100000 || peOffset + 0x18 > remainingLength)
        {
            return null;
        }

        Span<byte> coff = stackalloc byte[0x18];
        stream.Position = absoluteOffset + peOffset;
        if (stream.Read(coff) != coff.Length || coff[0] != (byte)'P' || coff[1] != (byte)'E' || coff[2] != 0 || coff[3] != 0)
        {
            return null;
        }

        var machine = ReadUInt16LittleEndian(coff[4..]);
        var sectionCount = ReadUInt16LittleEndian(coff[6..]);
        var optionalHeaderSize = ReadUInt16LittleEndian(coff[20..]);
        if (sectionCount == 0 || sectionCount > 128 || optionalHeaderSize > 0x1000)
        {
            return null;
        }

        var sectionTableOffset = absoluteOffset + peOffset + 0x18 + optionalHeaderSize;
        var sectionTableSize = sectionCount * 0x28;
        if (sectionTableOffset < absoluteOffset || sectionTableOffset + sectionTableSize > absoluteOffset + remainingLength)
        {
            return null;
        }

        var section = new byte[0x28];
        long fileSize = 0;
        for (var index = 0; index < sectionCount; index++)
        {
            stream.Position = sectionTableOffset + (index * 0x28L);
            if (stream.Read(section, 0, section.Length) != section.Length)
            {
                return null;
            }

            var rawSize = ReadUInt32LittleEndian(section.AsSpan(0x10));
            var rawOffset = ReadUInt32LittleEndian(section.AsSpan(0x14));
            if (rawOffset > 0 && rawSize > 0)
            {
                fileSize = Math.Max(fileSize, rawOffset + rawSize);
            }
        }

        if (fileSize <= 0)
        {
            fileSize = EstimateUnknownSize(remainingLength);
        }

        var arch = machine switch
        {
            0x014c => "x86",
            0x8664 => "x64",
            0x01F2 => "PowerPC",
            _ => $"machine 0x{machine:X4}"
        };
        return new GenericCarverMatch(string.Empty, ".exe", Math.Min(fileSize, remainingLength), $"PE executable ({arch})");
    }

    private static GenericCarverMatch? TryMatchCommon(ReadOnlySpan<byte> header, FileStream stream, long absoluteOffset, long remainingLength)
    {
        if (StartsWith(header, "\x89PNG\r\n\x1A\n"u8))
        {
            var size = TryGetPngSize(stream, absoluteOffset, remainingLength);
            return new GenericCarverMatch(string.Empty, ".png", size > 0 ? size : 8, "PNG image");
        }

        if (StartsWith(header, "RIFF"u8) && header.Length >= 8)
        {
            var size = ReadUInt32LittleEndian(header[4..]) + 8L;
            return new GenericCarverMatch(string.Empty, ".riff", Math.Min(size, remainingLength), "RIFF container");
        }

        if (StartsWith(header, "PK\x03\x04"u8))
        {
            var size = TryGetZipSize(stream, absoluteOffset, remainingLength);
            return new GenericCarverMatch(string.Empty, ".zip", size > 0 ? size : EstimateUnknownSize(remainingLength), "ZIP container");
        }

        return null;
    }

    private static long TryGetSfoSize(FileStream stream, long absoluteOffset, uint keyTableStart, uint dataTableStart, uint entryCount, long remainingLength)
    {
        if (entryCount > 4096 || dataTableStart > remainingLength)
        {
            return 0;
        }

        var lastDataOffset = 0U;
        var lastDataSize = 0U;
        var entry = new byte[0x10];
        for (var index = 0U; index < entryCount; index++)
        {
            stream.Position = absoluteOffset + 0x14 + (index * 0x10);
            if (stream.Read(entry, 0, entry.Length) != entry.Length)
            {
                break;
            }

            var dataSize = ReadUInt32LittleEndian(entry.AsSpan(4));
            var dataOffset = ReadUInt32LittleEndian(entry.AsSpan(12));
            if (dataOffset >= lastDataOffset)
            {
                lastDataOffset = dataOffset;
                lastDataSize = dataSize;
            }
        }

        var size = (long)dataTableStart + lastDataOffset + lastDataSize;
        return size > 0 && size <= remainingLength ? size : 0;
    }

    private static long TryGetPs4PkgSize(ReadOnlySpan<byte> header, long remainingLength)
    {
        foreach (var offset in new[] { 0x18, 0x20, 0x28 })
        {
            if (header.Length < offset + 8)
            {
                continue;
            }

            var candidate = checked((long)Math.Min((ulong)long.MaxValue, ReadUInt64BigEndian(header[offset..])));
            if (candidate >= 0x400 && candidate <= remainingLength)
            {
                return candidate;
            }
        }

        return 0;
    }

    private static long TryGetXexSize(FileStream stream, long absoluteOffset, uint securityOffset, long remainingLength)
    {
        if (securityOffset == 0 || securityOffset + 8 > remainingLength)
        {
            return 0;
        }

        Span<byte> buffer = stackalloc byte[8];
        stream.Position = absoluteOffset + securityOffset + 4;
        if (stream.Read(buffer) != buffer.Length)
        {
            return 0;
        }

        var size = ReadUInt32BigEndian(buffer);
        return size > 0 && size <= remainingLength ? size : 0;
    }

    private static bool TryGetXbox360XexMagic(ReadOnlySpan<byte> header, out string magic)
    {
        magic = string.Empty;

        if (header.Length < 4 || header[0] != (byte)'X' || header[1] != (byte)'E' || header[2] != (byte)'X')
        {
            return false;
        }

        var known = header[3] switch
        {
            (byte)'0' or (byte)'?' or (byte)'-' or (byte)'%' or (byte)'1' or (byte)'2' => true,
            _ => false
        };

        if (!known)
        {
            return false;
        }

        magic = Encoding.ASCII.GetString(header[..4]);
        return true;
    }

    private static long TryGetXvdSize(FileStream stream, long absoluteOffset, long remainingLength)
    {
        var nextStart = FindNextXvdStart(stream, absoluteOffset, remainingLength);
        if (nextStart > absoluteOffset)
        {
            return Math.Min(remainingLength, nextStart - absoluteOffset);
        }

        // Without a next container bound or filesystem extent, keep the old conservative
        // export size so the carver does not claim the rest of a large disk image.
        return Math.Min(remainingLength, 0x1000000);
    }

    private static long FindNextXvdStart(FileStream stream, long absoluteOffset, long remainingLength)
    {
        const long minimumGap = 0x1000;
        const int signatureOffset = 0x200;
        const int scanChunkSize = 0x400000;
        const int overlapSize = 0x300;

        if (remainingLength <= minimumGap + signatureOffset + 8)
        {
            return -1;
        }

        var scanEnd = absoluteOffset + remainingLength;
        var position = absoluteOffset + minimumGap;
        var buffer = new byte[scanChunkSize + overlapSize];
        while (position < scanEnd)
        {
            var windowStart = position == absoluteOffset + minimumGap
                ? position
                : Math.Max(absoluteOffset + minimumGap, position - overlapSize);
            var readable = (int)Math.Min(buffer.Length, scanEnd - windowStart);
            if (readable <= 0)
            {
                break;
            }

            stream.Position = windowStart;
            var read = stream.Read(buffer, 0, readable);
            if (read <= 0)
            {
                break;
            }

            for (var index = 0; index <= read - 8; index++)
            {
                if (!StartsWith(buffer.AsSpan(index, read - index), "MSFT-XVD"u8))
                {
                    continue;
                }

                var candidateStart = windowStart + index - signatureOffset;
                if (candidateStart > absoluteOffset &&
                    candidateStart >= absoluteOffset + minimumGap &&
                    candidateStart < scanEnd &&
                    candidateStart % 0x1000 == 0)
                {
                    return candidateStart;
                }
            }

            position += scanChunkSize;
        }

        return -1;
    }

    private static string BuildXvdDetail(byte type, string? displayName)
    {
        var kind = type switch
        {
            0x00 => "system",
            0x04 => "dev storage container",
            0x06 => "dev title/content container",
            0x09 => "system",
            0x10 => "system",
            0x41 => "retail",
            0x43 => "dev sandbox",
            _ => "unknown"
        };
        var detail = $"Xbox One/Series XVD package; header type 0x{type:X2} ({kind})";
        return string.IsNullOrWhiteSpace(displayName)
            ? detail
            : $"{detail}; display name: {displayName}";
    }

    internal static XvdContainerMetadata? TryReadXvdMetadata(Stream stream, long absoluteOffset, long size)
    {
        Span<byte> header = stackalloc byte[0x300];
        var read = ReadAt(stream, absoluteOffset, header);
        if (read < 0x209 || !StartsWith(header[0x200..read], "MSFT-XVD"u8))
        {
            return null;
        }

        var type = header[0x208];
        var displayName = TryFindXvdDisplayName(stream, absoluteOffset, size);
        var detail = BuildXvdDetail(type, displayName);
        var start = detail.IndexOf('(');
        var end = detail.IndexOf(')', start + 1);
        var classification = start >= 0 && end > start
            ? detail[(start + 1)..end]
            : "unknown";
        return new XvdContainerMetadata(type, classification, displayName, detail);
    }

    private static string? TryFindXvdDisplayName(Stream stream, long absoluteOffset, long size)
    {
        const int scanChunkSize = 0x400000;
        const int overlapSize = 0x4000;
        const long maxScanLength = 0x4000000;

        var scanLength = Math.Min(size, maxScanLength);
        if (scanLength <= 0)
        {
            return null;
        }

        var buffer = new byte[scanChunkSize + overlapSize];
        for (long relative = 0; relative < scanLength; relative += scanChunkSize)
        {
            var windowStart = relative == 0
                ? absoluteOffset
                : absoluteOffset + Math.Max(0, relative - overlapSize);
            var readable = (int)Math.Min(buffer.Length, absoluteOffset + scanLength - windowStart);
            if (readable <= 0)
            {
                break;
            }

            var read = ReadAt(stream, windowStart, buffer.AsSpan(0, readable));
            if (read <= 0)
            {
                break;
            }

            var value = TryFindDisplayNameInXmlAsciiRuns(buffer.AsSpan(0, read));
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int ReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        lock (stream)
        {
            stream.Position = offset;
            return stream.Read(buffer);
        }
    }

    private static string? TryFindDisplayNameInXmlAsciiRuns(ReadOnlySpan<byte> buffer)
    {
        const int maxXmlLength = 0x100000;
        for (var index = 0; index <= buffer.Length - 5; index++)
        {
            if (!LooksLikeXmlStart(buffer[index..]))
            {
                continue;
            }

            var length = ReadAsciiRunLength(buffer[index..], maxXmlLength);
            if (length <= 0)
            {
                continue;
            }

            var xml = Encoding.ASCII.GetString(buffer.Slice(index, length));
            if (xml.IndexOf("displayname", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var value = ExtractDisplayName(xml);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            index += Math.Max(0, length - 1);
        }

        return null;
    }

    private static bool LooksLikeXmlStart(ReadOnlySpan<byte> buffer)
    {
        return StartsWith(buffer, "<?xml"u8)
               || StartsWith(buffer, "<Package"u8)
               || StartsWith(buffer, "<Identity"u8)
               || StartsWith(buffer, "<Properties"u8)
               || StartsWith(buffer, "<Applications"u8);
    }

    private static int ReadAsciiRunLength(ReadOnlySpan<byte> value, int maxLength)
    {
        var length = 0;
        var limit = Math.Min(value.Length, maxLength);
        while (length < limit && IsXmlAsciiByte(value[length]))
        {
            length++;
        }

        return length;
    }

    private static bool IsXmlAsciiByte(byte value)
    {
        return value is 0x09 or 0x0A or 0x0D || value is >= 0x20 and <= 0x7E;
    }

    private static string? ExtractDisplayName(string text)
    {
        var elementValue = ExtractBetween(text, "<DisplayName>", "</DisplayName>");
        if (!string.IsNullOrWhiteSpace(elementValue))
        {
            return SanitizeDisplayName(elementValue);
        }

        const string attribute = "DisplayName=\"";
        var attributeIndex = text.IndexOf(attribute, StringComparison.OrdinalIgnoreCase);
        if (attributeIndex < 0)
        {
            return null;
        }

        var start = attributeIndex + attribute.Length;
        var end = text.IndexOf('"', start);
        if (end <= start)
        {
            return null;
        }

        return SanitizeDisplayName(text[start..end]);
    }

    private static string? ExtractBetween(string text, string startToken, string endToken)
    {
        var startIndex = text.IndexOf(startToken, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
        {
            return null;
        }

        var start = startIndex + startToken.Length;
        var end = text.IndexOf(endToken, start, StringComparison.OrdinalIgnoreCase);
        return end > start ? text[start..end] : null;
    }

    private static string? SanitizeDisplayName(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.Length <= 160 ? value : value[..160];
    }

    private static long TryGetPngSize(FileStream stream, long absoluteOffset, long remainingLength)
    {
        var maxScan = (int)Math.Min(remainingLength, 0x2000000);
        var buffer = new byte[maxScan];
        stream.Position = absoluteOffset;
        var read = stream.Read(buffer, 0, buffer.Length);
        var index = 8;
        while (index + 12 <= read)
        {
            var chunkLength = ReadUInt32BigEndian(buffer.AsSpan(index));
            var typeIndex = index + 4;
            if (typeIndex + 4 > read)
            {
                return 0;
            }

            if (buffer[typeIndex] == (byte)'I' &&
                buffer[typeIndex + 1] == (byte)'E' &&
                buffer[typeIndex + 2] == (byte)'N' &&
                buffer[typeIndex + 3] == (byte)'D')
            {
                return typeIndex + 8;
            }

            var next = index + 12L + chunkLength;
            if (next <= index || next > read)
            {
                return 0;
            }

            index = (int)next;
        }

        return 0;
    }

    private static long TryGetZipSize(FileStream stream, long absoluteOffset, long remainingLength)
    {
        var maxScan = (int)Math.Min(remainingLength, 0x1000000);
        var buffer = new byte[maxScan];
        stream.Position = absoluteOffset;
        var read = stream.Read(buffer, 0, buffer.Length);
        for (var index = 4; index <= read - 4; index++)
        {
            if (buffer[index] == 0x50 &&
                buffer[index + 1] == 0x4B &&
                buffer[index + 2] == 0x05 &&
                buffer[index + 3] == 0x06)
            {
                return Math.Min(index + 22L, read);
            }
        }

        return 0;
    }

    private static long EstimateUnknownSize(long remainingLength)
    {
        return Math.Min(remainingLength, 0x100000);
    }

    private static string ReadAsciiNullTerminated(FileStream stream, long offset, int maxLength)
    {
        if (maxLength <= 0 || offset < 0 || offset >= stream.Length)
        {
            return string.Empty;
        }

        var buffer = new byte[Math.Min(maxLength, (int)Math.Min(int.MaxValue, stream.Length - offset))];
        stream.Position = offset;
        var read = stream.Read(buffer, 0, buffer.Length);
        var length = 0;
        while (length < read && buffer[length] is >= 0x20 and <= 0x7E)
        {
            length++;
        }

        return length == 0 ? string.Empty : Encoding.ASCII.GetString(buffer, 0, length);
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
    {
        return value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value)
    {
        return ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];
    }

    private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> value)
    {
        return value[0] | ((uint)value[1] << 8) | ((uint)value[2] << 16) | ((uint)value[3] << 24);
    }

    private static ushort ReadUInt16LittleEndian(ReadOnlySpan<byte> value)
    {
        return (ushort)(value[0] | (value[1] << 8));
    }

    private static ulong ReadUInt64BigEndian(ReadOnlySpan<byte> value)
    {
        return ((ulong)ReadUInt32BigEndian(value) << 32) | ReadUInt32BigEndian(value[4..]);
    }
}

public sealed record Ps3DirectoryEntry(
    string Name,
    string Kind,
    long Offset,
    uint Inode,
    ushort RecordLength,
    byte FileType,
    byte NameLength);

public sealed record PlayStationMetadataEntry(
    string Name,
    string Kind,
    long Offset,
    uint Inode,
    ushort RecordLength,
    byte FileType,
    byte NameLength,
    bool IsDeleted,
    string MetadataStatus,
    long Size = 0,
    int DataOffsetCount = 0,
    int DataRunCount = 0,
    long LargestRunBytes = 0,
    string FragmentationStatus = "",
    string DataRanges = "",
    string DataOffsets = "");

public sealed class Ps4UfsDirentScanner
{
    private const int DirectoryBlockSize = 0x1000;

    private static readonly IReadOnlyDictionary<byte, string> FileTypes = new Dictionary<byte, string>
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

    private readonly string _sourcePath;
    private readonly long _displayBaseOffset;

    public Ps4UfsDirentScanner(string sourcePath, long displayBaseOffset)
    {
        _sourcePath = sourcePath;
        _displayBaseOffset = displayBaseOffset;
    }

    public List<PlayStationMetadataEntry> Analyze(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var rows = new List<PlayStationMetadataEntry>();
        using var stream = new FileStream(_sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var steps = Math.Max(1L, stream.Length / DirectoryBlockSize);
        var progressEvery = Math.Max(1L, steps / 500);
        var block = new byte[DirectoryBlockSize];

        for (long offset = 0, step = 0; offset + 0x20 < stream.Length; offset += DirectoryBlockSize, step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            stream.Position = offset;
            var read = stream.Read(block, 0, block.Length);
            if (read < 0x20)
            {
                break;
            }

            rows.AddRange(ReadDirectoryBlock(block.AsSpan(0, read), _displayBaseOffset + offset));

            if (step % progressEvery == 0)
            {
                progress?.Report((int)Math.Min(int.MaxValue, step));
            }
        }

        progress?.Report((int)Math.Min(int.MaxValue, steps));
        return rows;
    }

    private static List<PlayStationMetadataEntry> ReadDirectoryBlock(ReadOnlySpan<byte> block, long displayBlockOffset)
    {
        var first = ParseHeader(block);
        if (first.FileType != 4 || first.NameLength != 1 || !NameEquals(block, 8, first.NameLength, ".") || !IsValidRecord(first, block.Length))
        {
            return [];
        }

        var secondOffset = first.RecordLength;
        if (secondOffset + 8 >= block.Length)
        {
            return [];
        }

        var second = ParseHeader(block[secondOffset..]);
        if (second.FileType != 4 || second.NameLength != 2 || !NameEquals(block, secondOffset + 8, second.NameLength, "..") || !IsValidRecord(second, block.Length - secondOffset))
        {
            return [];
        }

        var rows = new List<PlayStationMetadataEntry>();
        var cursor = 0;
        var seen = 0;
        while (cursor + 8 <= block.Length && seen++ < 4096)
        {
            var record = ParseHeader(block[cursor..]);
            if (!IsValidRecord(record, block.Length - cursor))
            {
                break;
            }

            var name = ReadDirentName(block, cursor + 8, record.NameLength);
            if (!string.IsNullOrWhiteSpace(name) && name != "." && name != "..")
            {
                var deleted = record.Inode == 0;
                var kind = FileTypes.TryGetValue(record.FileType, out var value) ? value : "Unknown";
                rows.Add(new PlayStationMetadataEntry(
                    name,
                    kind,
                    displayBlockOffset + cursor,
                    record.Inode,
                    record.RecordLength,
                    record.FileType,
                    record.NameLength,
                    deleted,
                    deleted ? "Deleted PS4 UFS dirent candidate" : "Active PS4 UFS dirent"));
            }

            cursor += record.RecordLength;
        }

        return rows;
    }

    private static (uint Inode, ushort RecordLength, byte FileType, byte NameLength) ParseHeader(ReadOnlySpan<byte> header)
    {
        var inode = (uint)(header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24));
        var recordLength = (ushort)(header[4] | (header[5] << 8));
        return (inode, recordLength, header[6], header[7]);
    }

    private static bool IsValidRecord((uint Inode, ushort RecordLength, byte FileType, byte NameLength) record, int remaining)
    {
        var minimumLength = 8 + record.NameLength;
        minimumLength = (minimumLength + 3) & ~3;
        return record.RecordLength >= minimumLength &&
               record.RecordLength <= remaining &&
               record.RecordLength % 4 == 0 &&
               record.NameLength <= 255 &&
               FileTypes.ContainsKey(record.FileType);
    }

    private static string ReadDirentName(ReadOnlySpan<byte> block, int offset, int length)
    {
        if (length <= 0 || offset < 0 || offset + length > block.Length)
        {
            return string.Empty;
        }

        var nameBytes = block.Slice(offset, length);
        foreach (var value in nameBytes)
        {
            if (value < 0x20 || value > 0x7E || value == (byte)'/')
            {
                return string.Empty;
            }
        }

        return Encoding.ASCII.GetString(nameBytes);
    }

    private static bool NameEquals(ReadOnlySpan<byte> block, int offset, int length, string expected)
    {
        return ReadDirentName(block, offset, length).Equals(expected, StringComparison.Ordinal);
    }
}

public sealed class Ps3DirentScanner
{
    private static readonly IReadOnlyDictionary<byte, string> FileTypes = new Dictionary<byte, string>
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

    private readonly string _sourcePath;
    private readonly long _displayBaseOffset;

    public Ps3DirentScanner(string sourcePath, long displayBaseOffset)
    {
        _sourcePath = sourcePath;
        _displayBaseOffset = displayBaseOffset;
    }

    public List<Ps3DirectoryEntry> Analyze(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var rows = new List<Ps3DirectoryEntry>();
        using var stream = new FileStream(_sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        var steps = Math.Max(1L, stream.Length / 0x800);
        var progressEvery = Math.Max(1L, steps / 500);
        Span<byte> first = stackalloc byte[8];
        Span<byte> second = stackalloc byte[8];

        for (long offset = 0, step = 0; offset + 0x10 < stream.Length; offset += 0x800, step++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            stream.Position = offset;
            if (stream.Read(first) != first.Length)
            {
                break;
            }

            var firstRecord = ParseHeader(first);
            if (firstRecord.FileType != 4 || firstRecord.NameLength != 1 || !ReadNameEquals(stream, firstRecord.NameLength, "."))
            {
                continue;
            }

            if (firstRecord.RecordLength == 0 || firstRecord.RecordLength >= 0x200)
            {
                continue;
            }

            stream.Position = offset + firstRecord.RecordLength;
            if (stream.Read(second) != second.Length)
            {
                continue;
            }

            var secondRecord = ParseHeader(second);
            if (secondRecord.FileType != 4 || secondRecord.NameLength != 2 || !ReadNameEquals(stream, secondRecord.NameLength, ".."))
            {
                continue;
            }

            rows.AddRange(ReadDirectoryStream(stream, offset));

            if (step % progressEvery == 0)
            {
                progress?.Report((int)Math.Min(int.MaxValue, step));
            }
        }

        progress?.Report((int)Math.Min(int.MaxValue, steps));
        return rows;
    }

    private List<Ps3DirectoryEntry> ReadDirectoryStream(FileStream stream, long startOffset)
    {
        var rows = new List<Ps3DirectoryEntry>();
        var seen = new HashSet<long>();
        var header = new byte[8];
        var offset = startOffset;
        while (offset >= 0 && offset + 8 < stream.Length && seen.Add(offset))
        {
            stream.Position = offset;
            if (stream.Read(header) != header.Length)
            {
                break;
            }

            var record = ParseHeader(header);
            if (record.RecordLength == 0 || record.RecordLength >= 0x200 || record.NameLength > 255 || !FileTypes.ContainsKey(record.FileType))
            {
                break;
            }

            var nameBytes = new byte[record.NameLength];
            if (stream.Read(nameBytes, 0, nameBytes.Length) != nameBytes.Length)
            {
                break;
            }

            string name;
            try
            {
                name = Encoding.ASCII.GetString(nameBytes);
            }
            catch
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(name) && name != "." && name != "..")
            {
                rows.Add(new Ps3DirectoryEntry(
                    name,
                    FileTypes[record.FileType],
                    _displayBaseOffset + offset,
                    record.Inode,
                    record.RecordLength,
                    record.FileType,
                    record.NameLength));
            }

            offset += record.RecordLength;
        }

        return rows;
    }

    private static (uint Inode, ushort RecordLength, byte FileType, byte NameLength) ParseHeader(ReadOnlySpan<byte> header)
    {
        var inode = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
        var recordLength = (ushort)((header[4] << 8) | header[5]);
        return (inode, recordLength, header[6], header[7]);
    }

    private static bool ReadNameEquals(FileStream stream, byte length, string expected)
    {
        var buffer = new byte[length];
        return stream.Read(buffer, 0, buffer.Length) == buffer.Length
               && Encoding.ASCII.GetString(buffer).Equals(expected, StringComparison.Ordinal);
    }
}
