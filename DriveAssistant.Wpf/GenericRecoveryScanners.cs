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
    string Detail,
    IReadOnlyList<FileExtent>? Extents = null,
    string FragmentationStatus = "",
    string ExtentSummary = "")
{
    public bool HasFileData => Size > 0 && File.Exists(SourcePath);

    public IReadOnlyList<FileExtent> EffectiveExtents => Extents is { Count: > 0 }
        ? Extents
        : Size > 0 ? [new FileExtent(SourceOffset, Size)] : [];

    public int FragmentRunCount => EffectiveExtents.Count;

    public string EffectiveFragmentationStatus => !string.IsNullOrWhiteSpace(FragmentationStatus)
        ? FragmentationStatus
        : FragmentRunCount <= 1
            ? "Contiguous"
            : $"Fragmented into {FragmentRunCount:N0} runs";

    public string EffectiveExtentSummary => !string.IsNullOrWhiteSpace(ExtentSummary)
        ? ExtentSummary
        : BuildExtentSummary(EffectiveExtents);

    private static string BuildExtentSummary(IReadOnlyList<FileExtent> extents)
    {
        return extents.Count == 0
            ? string.Empty
            : string.Join(", ", extents.Take(8).Select(extent => $"0x{extent.Offset:X}+0x{extent.Length:X}"));
    }
}

public sealed record GenericCarverMatch(string Name, string Extension, long Size, string Detail);

internal sealed record XvdContainerMetadata(byte HeaderType, string Classification, string? DisplayName, string Detail);

public sealed class GenericFileCarver
{
    private const int HeaderBufferSize = 0x300;
    private const int ScanChunkSize = 0x4000000;
    private const long Ps4PkgFirstFragmentOffset = 0x28000000L;
    private const long Ps4PkgFragmentStride = 0x28000000L;
    private const long Ps4PkgNormalFragmentLength = 0x6400000L;
    private const int Ps4PkgMaxPredictedFragments = 128;
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
                    var size = Math.Min(match.Size, readableLength - relative);
                    if (size <= 0)
                    {
                        continue;
                    }

                    var file = new GenericCarvedFile(
                        BuildFileName(relative, match),
                        match.Extension.TrimStart('.').ToUpperInvariant(),
                        _sourcePath,
                        absoluteOffset,
                        _displayBaseOffset + relative,
                        size,
                        _sourceName,
                        match.Detail,
                        Extents: [new FileExtent(absoluteOffset, size)],
                        FragmentationStatus: "Contiguous",
                        ExtentSummary: $"0x{absoluteOffset:X}+0x{size:X}");
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
        return ApplyNcaFragmentationHints(rows);
    }

    private static List<GenericCarvedFile> ApplyNcaFragmentationHints(List<GenericCarvedFile> rows)
    {
        if (rows.Count < 2)
        {
            return rows;
        }

        var ordered = rows
            .Select((row, index) => (row, index))
            .Where(tuple => tuple.row.Size > 0)
            .OrderBy(tuple => tuple.row.SourceOffset)
            .ToList();
        if (ordered.Count < 2)
        {
            return rows;
        }

        var updates = new Dictionary<int, GenericCarvedFile>();
        foreach (var candidate in ordered.Where(tuple => tuple.row.Kind.Equals("NCA", StringComparison.OrdinalIgnoreCase)))
        {
            var start = candidate.row.SourceOffset;
            var end = checked(start + candidate.row.Size);
            var overlap = ordered.FirstOrDefault(tuple =>
                tuple.index != candidate.index
                && string.Equals(tuple.row.SourcePath, candidate.row.SourcePath, StringComparison.OrdinalIgnoreCase)
                && tuple.row.SourceOffset > start
                && tuple.row.SourceOffset < end);
            if (overlap == default)
            {
                continue;
            }

            var firstRunLength = overlap.row.SourceOffset - start;
            if (firstRunLength <= 0)
            {
                continue;
            }

            var trailingOffset = overlap.row.SourceOffset + Math.Max(0, overlap.row.Size);
            var trailingLength = Math.Max(0, end - trailingOffset);
            var extents = new List<FileExtent> { new(start, firstRunLength) };
            if (trailingLength > 0)
            {
                extents.Add(new FileExtent(trailingOffset, trailingLength));
            }

            var overlapEnd = Math.Min(end, overlap.row.SourceOffset + Math.Max(0, overlap.row.Size));
            var overlapLength = Math.Max(0, overlapEnd - overlap.row.SourceOffset);
            var overlapName = string.IsNullOrWhiteSpace(overlap.row.Name) ? overlap.row.Kind : overlap.row.Name;
            var status = extents.Count == 1
                ? $"Contiguous span with overlap candidate ({overlapName})"
                : $"Likely fragmented or overwritten span ({extents.Count:N0} runs); overlap with {overlapName} at 0x{overlap.row.SourceOffset:X} (+0x{overlapLength:X})";
            var detail = $"{candidate.row.Detail}; overlap hint: another carved header ({overlapName}) appears inside this NCA's claimed size window at 0x{overlap.row.SourceOffset:X}.";

            updates[candidate.index] = candidate.row with
            {
                Detail = detail,
                Extents = extents,
                FragmentationStatus = status,
                ExtentSummary = string.Join(", ", extents.Take(8).Select(extent => $"0x{extent.Offset:X}+0x{extent.Length:X}"))
            };
        }

        if (updates.Count == 0)
        {
            return rows;
        }

        for (var index = 0; index < rows.Count; index++)
        {
            if (updates.TryGetValue(index, out var updated))
            {
                rows[index] = updated;
            }
        }

        return rows;
    }

    private void AddNestedContainerRows(List<GenericCarvedFile> rows, FileStream stream, GenericCarvedFile outerFile, GenericCarverMatch outerMatch, CancellationToken cancellationToken)
    {
        if (_scanProfile == ScanProfile.Fast || outerFile.Size <= 0)
        {
            return;
        }

        if (outerMatch.Extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase)
            || outerMatch.Extension.Equals(".dpkg", StringComparison.OrdinalIgnoreCase))
        {
            AddPs4PkgFragmentRows(rows, outerFile, cancellationToken);
        }

        if (outerMatch.Extension.Equals(".nsp", StringComparison.OrdinalIgnoreCase))
        {
            AddPfs0PackageRows(rows, stream, outerFile, cancellationToken);
            return;
        }

        if (outerMatch.Extension.Equals(".nca", StringComparison.OrdinalIgnoreCase))
        {
            AddNcaSectionRows(rows, stream, outerFile, cancellationToken);
            return;
        }

        if (!outerMatch.Extension.Equals(".xvd", StringComparison.OrdinalIgnoreCase)
            && !outerMatch.Extension.Equals(".xvc", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AddXvdManifestRows(rows, stream, outerFile, cancellationToken);
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

    private static void AddPfs0PackageRows(List<GenericCarvedFile> rows, FileStream stream, GenericCarvedFile packageFile, CancellationToken cancellationToken)
    {
        if (!TryReadPfs0Entries(stream, packageFile.SourceOffset, packageFile.Size, out var entries, out var headerSize))
        {
            return;
        }

        var packageBaseName = Path.GetFileNameWithoutExtension(packageFile.Name);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryOffset = packageFile.SourceOffset + headerSize + entry.Offset;
            var extension = Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant();
            var kind = string.IsNullOrWhiteSpace(extension) ? "PFS0ENTRY" : extension;
            var detail = $"Nintendo Switch PFS0 package entry: {entry.Name}";
            if (extension.Equals("NCA", StringComparison.OrdinalIgnoreCase))
            {
                detail = TryReadNcaDetail(stream, entryOffset, entry.Size) ?? detail;
            }
            else if (extension.Equals("CNMT", StringComparison.OrdinalIgnoreCase))
            {
                detail = TryReadCnmtDetail(stream, entryOffset, entry.Size) ?? detail;
            }

            var child = new GenericCarvedFile(
                $"{packageBaseName}_{SanitizeFileName(entry.Name)}",
                kind,
                packageFile.SourcePath,
                entryOffset,
                packageFile.DisplayOffset + headerSize + entry.Offset,
                Math.Min(entry.Size, Math.Max(0, packageFile.Size - headerSize - entry.Offset)),
                $"{packageFile.Source} > {packageFile.Name}",
                $"{detail}; nested inside NSP/PFS0 package");
            rows.Add(child);

            if (extension.Equals("NCA", StringComparison.OrdinalIgnoreCase))
            {
                AddNcaSectionRows(rows, stream, child, cancellationToken);
            }
        }
    }

    private static void AddNcaSectionRows(List<GenericCarvedFile> rows, FileStream stream, GenericCarvedFile ncaFile, CancellationToken cancellationToken)
    {
        foreach (var section in TryReadNcaSections(stream, ncaFile.SourceOffset, ncaFile.Size))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new GenericCarvedFile(
                $"{Path.GetFileNameWithoutExtension(ncaFile.Name)}_section_{section.Index}.bin",
                "NCASECTION",
                ncaFile.SourcePath,
                ncaFile.SourceOffset + section.Offset,
                ncaFile.DisplayOffset + section.Offset,
                section.Size,
                $"{ncaFile.Source} > {ncaFile.Name}",
                $"Nintendo Switch NCA section {section.Index}; encrypted/raw section span from NCA section table"));
        }
    }

    private static void AddXvdManifestRows(List<GenericCarvedFile> rows, FileStream stream, GenericCarvedFile xvdFile, CancellationToken cancellationToken)
    {
        foreach (var manifest in TryFindXvdXmlRuns(stream, xvdFile.SourceOffset, xvdFile.Size))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new GenericCarvedFile(
                $"{Path.GetFileNameWithoutExtension(xvdFile.Name)}_manifest_{manifest.Offset:X}.xml",
                "XVDXML",
                xvdFile.SourcePath,
                xvdFile.SourceOffset + manifest.Offset,
                xvdFile.DisplayOffset + manifest.Offset,
                manifest.Length,
                $"{xvdFile.Source} > {xvdFile.Name}",
                string.IsNullOrWhiteSpace(manifest.DisplayName)
                    ? "Xbox XVD/XVC embedded XML manifest"
                    : $"Xbox XVD/XVC embedded XML manifest; display name: {manifest.DisplayName}"));
        }
    }

    private static void AddPs4PkgFragmentRows(List<GenericCarvedFile> rows, GenericCarvedFile packageFile, CancellationToken cancellationToken)
    {
        if (packageFile.Size <= Ps4PkgFirstFragmentOffset)
        {
            return;
        }

        var packageBaseName = Path.GetFileNameWithoutExtension(packageFile.Name);
        var fragments = 0;
        for (var fragmentOffset = Ps4PkgFirstFragmentOffset;
             fragmentOffset < packageFile.Size && fragments < Ps4PkgMaxPredictedFragments;
             fragmentOffset += Ps4PkgFragmentStride)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = packageFile.Size - fragmentOffset;
            var fragmentSize = Math.Min(Ps4PkgNormalFragmentLength, remaining);
            if (fragmentSize <= 0)
            {
                break;
            }

            rows.Add(new GenericCarvedFile(
                $"{packageBaseName}_fragment_{fragments + 1:D3}_{fragmentOffset:X16}.pkgfrag",
                "PKGFRAG",
                packageFile.SourcePath,
                packageFile.SourceOffset + fragmentOffset,
                packageFile.DisplayOffset + fragmentOffset,
                fragmentSize,
                $"{packageFile.Source} > {packageFile.Name}",
                $"Predicted PS4 PKG fragment window #{fragments + 1}; package offset 0x{fragmentOffset:X}; normal span 0x{Ps4PkgNormalFragmentLength:X}; old Ubisoft kit exceptions reported at 0x8000 and 0xBA0000. If the next visible fragment appears at the next 0x{Ps4PkgFragmentStride:X} boundary, this window may have been overwritten."));
            fragments++;
        }
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
               ?? TryMatchNintendoLegacy(header, remainingLength)
               ?? TryMatchNintendoHandheld(header, remainingLength)
               ?? TryMatchPlayStation2(header, remainingLength)
               ?? TryMatchLegacyDevkitMedia(header, remainingLength)
               ?? TryMatchNintendoSwitch(header, stream, absoluteOffset, remainingLength)
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
            var packageType = header.Length >= 8 ? ReadUInt32BigEndian(header[4..]) : 0;
            var isDebugPackage = packageType == 1;
            var extension = isDebugPackage ? ".dpkg" : ".pkg";
            var detail = isDebugPackage
                ? "PS4 debug package (DPKG, CNT header)"
                : $"PS4 package (PKG, CNT header, type 0x{packageType:X8})";
            return new GenericCarverMatch(string.Empty, extension, size > 0 ? size : EstimateUnknownSize(remainingLength), detail);
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

    private static GenericCarverMatch? TryMatchNintendoSwitch(ReadOnlySpan<byte> header, FileStream stream, long absoluteOffset, long remainingLength)
    {
        if (header.Length >= 0x204
            && header[0x200] == (byte)'N'
            && header[0x201] == (byte)'C'
            && header[0x202] == (byte)'A'
            && header[0x203] is (byte)'2' or (byte)'3')
        {
            var info = TryReadNcaHeaderInfo(stream, absoluteOffset, remainingLength);
            var detail = info?.Detail ?? "Nintendo Switch NCA content archive; plaintext/decrypted header detected";
            var size = info?.ResolvedSize > 0 ? info.ResolvedSize : EstimateUnknownSize(remainingLength);
            return new GenericCarverMatch(string.Empty, ".nca", size, detail);
        }

        if (StartsWith(header, "PFS0"u8) && header.Length >= 0x10)
        {
            var size = TryGetPfs0Size(stream, absoluteOffset, remainingLength);
            var detail = TryReadPfs0Entries(stream, absoluteOffset, remainingLength, out var entries, out _)
                ? $"Nintendo Switch NSP/PFS0 package; {entries.Count:N0} file entries"
                : "Nintendo Switch NSP/PFS0 package";
            return new GenericCarverMatch(string.Empty, ".nsp", size > 0 ? size : EstimateUnknownSize(remainingLength), detail);
        }

        if (header.Length >= 0x104 && StartsWith(header[0x100..], "HEAD"u8))
        {
            return new GenericCarverMatch(string.Empty, ".xci", EstimateUnknownSize(remainingLength), "Nintendo Switch XCI game card image");
        }

        if (StartsWith(header, "NRO0"u8))
        {
            return new GenericCarverMatch(string.Empty, ".nro", EstimateUnknownSize(remainingLength), "Nintendo Switch NRO executable");
        }

        if (StartsWith(header, "NSO0"u8))
        {
            return new GenericCarverMatch(string.Empty, ".nso", EstimateUnknownSize(remainingLength), "Nintendo Switch NSO executable");
        }

        return null;
    }

    private static GenericCarverMatch? TryMatchNintendoLegacy(ReadOnlySpan<byte> header, long remainingLength)
    {
        if (StartsWith(header, "WBFS"u8))
        {
            return new GenericCarverMatch(string.Empty, ".wbfs", EstimateUnknownSize(remainingLength), "Nintendo Wii WBFS container");
        }

        if (header.Length >= 0x20)
        {
            var wiiMagic = ReadUInt32BigEndian(header[0x18..]);
            if (wiiMagic == 0x5D1C9EA3)
            {
                return new GenericCarverMatch(string.Empty, ".iso", EstimateUnknownSize(remainingLength), "Nintendo Wii optical disc image");
            }

            var gameCubeMagic = ReadUInt32BigEndian(header[0x1C..]);
            if (gameCubeMagic == 0xC2339F3D)
            {
                return new GenericCarverMatch(string.Empty, ".gcm", EstimateUnknownSize(remainingLength), "Nintendo GameCube/Dolphin development optical disc image");
            }
        }

        if (StartsWith(header, "WFS"u8))
        {
            return new GenericCarverMatch(string.Empty, ".wfs", EstimateUnknownSize(remainingLength), "Nintendo Wii U WFS storage marker");
        }

        if (StartsWith(header, "FST\0"u8))
        {
            return new GenericCarverMatch(string.Empty, ".fst", EstimateUnknownSize(remainingLength), "Nintendo Wii U filesystem table");
        }

        if (StartsWith(header, "WUX0"u8))
        {
            return new GenericCarverMatch(string.Empty, ".wux", EstimateUnknownSize(remainingLength), "Nintendo Wii U compressed disc image");
        }

        return null;
    }

    private static GenericCarverMatch? TryMatchNintendoHandheld(ReadOnlySpan<byte> header, long remainingLength)
    {
        if (header.Length >= 0x104 && StartsWith(header[0x100..], "NCSD"u8))
        {
            return new GenericCarverMatch(string.Empty, ".3ds", EstimateUnknownSize(remainingLength), "Nintendo 3DS NCSD/CCI/NAND image");
        }

        if (header.Length >= 0x104 && StartsWith(header[0x100..], "NCCH"u8))
        {
            return new GenericCarverMatch(string.Empty, ".cxi", EstimateUnknownSize(remainingLength), "Nintendo 3DS NCCH/CXI/CFA container");
        }

        if (LooksLikeNdsRom(header, remainingLength))
        {
            var romSize = ReadUInt32LittleEndian(header[0x80..]);
            var size = romSize > 0 ? Math.Min(romSize, remainingLength) : EstimateUnknownSize(remainingLength);
            return new GenericCarverMatch(string.Empty, ".nds", size, "Nintendo DS/DSi ROM with NitroFS metadata");
        }

        return null;
    }

    private static bool LooksLikeNdsRom(ReadOnlySpan<byte> header, long remainingLength)
    {
        if (header.Length < 0x200)
        {
            return false;
        }

        var fntOffset = ReadUInt32LittleEndian(header[0x40..]);
        var fntSize = ReadUInt32LittleEndian(header[0x44..]);
        var fatOffset = ReadUInt32LittleEndian(header[0x48..]);
        var fatSize = ReadUInt32LittleEndian(header[0x4C..]);
        var romSize = ReadUInt32LittleEndian(header[0x80..]);
        if (fntOffset < 0x200 || fntSize == 0 || fatOffset < 0x200 || fatSize == 0 || fatSize % 8 != 0)
        {
            return false;
        }

        if (fntOffset + (long)fntSize > remainingLength || fatOffset + (long)fatSize > remainingLength)
        {
            return false;
        }

        if (romSize != 0 && romSize > remainingLength + 0x100000)
        {
            return false;
        }

        for (var index = 0; index < 0x10; index++)
        {
            var value = header[index];
            if (value != 0 && (value < 0x20 || value > 0x7E))
            {
                return false;
            }
        }

        return true;
    }

    private static GenericCarverMatch? TryMatchPlayStation2(ReadOnlySpan<byte> header, long remainingLength)
    {
        if (header.Length >= 0x10 && ReadUInt32LittleEndian(header[4..]) == 0x00415041)
        {
            return new GenericCarverMatch(string.Empty, ".ps2hdd", EstimateUnknownSize(remainingLength), "PlayStation 2 APA HDD partition header");
        }

        return null;
    }

    private static GenericCarverMatch? TryMatchLegacyDevkitMedia(ReadOnlySpan<byte> header, long remainingLength)
    {
        if (header.Length >= 2 && header[0] == (byte)'M' && header[1] == (byte)'C' && remainingLength >= 128 * 1024)
        {
            return new GenericCarverMatch(string.Empty, ".mcr", Math.Min(128 * 1024, remainingLength), "Sony PlayStation DTL/PS1 memory-card image");
        }

        if (header.Length >= 27 && Encoding.ASCII.GetString(header[..27]) == "Sony PS2 Memory Card Format")
        {
            return new GenericCarverMatch(string.Empty, ".ps2", EstimateUnknownSize(remainingLength), "Sony PlayStation 2 TOOL/TEST memory-card image");
        }

        if (header.Length >= 16 && Encoding.ASCII.GetString(header[..16]) == "SEGA SEGAKATANA ")
        {
            return new GenericCarverMatch("IP", ".bin", Math.Min(0x8000, remainingLength), "Sega Dreamcast Katana IP.BIN boot sector");
        }

        if (header.Length >= 4)
        {
            var n64Magic = ReadUInt32BigEndian(header);
            if (n64Magic is 0x80371240 or 0x37804012 or 0x40123780)
            {
                return new GenericCarverMatch(string.Empty, ".z64", EstimateUnknownSize(remainingLength), "Nintendo 64 Partner-N64 ROM image");
            }
        }

        if (LooksLikeGameBoyRom(header))
        {
            return new GenericCarverMatch(string.Empty, ".gb", EstimateUnknownSize(remainingLength), "Nintendo Game Boy-family development ROM image");
        }

        return null;
    }

    private static bool LooksLikeGameBoyRom(ReadOnlySpan<byte> header)
    {
        ReadOnlySpan<byte> logo =
        [
            0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83,
            0x00, 0x0C, 0x00, 0x0D, 0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
            0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99, 0xBB, 0xBB, 0x67, 0x63,
            0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E
        ];
        return header.Length >= 0x134 && header.Slice(0x104, logo.Length).SequenceEqual(logo);
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
        return TryFindXvdXmlRuns(stream, absoluteOffset, size)
            .Select(row => row.DisplayName)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static IReadOnlyList<XvdXmlRun> TryFindXvdXmlRuns(Stream stream, long absoluteOffset, long size)
    {
        const int scanChunkSize = 0x400000;
        const int overlapSize = 0x4000;
        const long maxScanLength = 0x4000000;
        const int maxRows = 16;

        var scanLength = Math.Min(size, maxScanLength);
        if (scanLength <= 0)
        {
            return [];
        }

        var rows = new List<XvdXmlRun>();
        var seen = new HashSet<long>();
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

            foreach (var run in TryFindXmlAsciiRuns(buffer.AsSpan(0, read)))
            {
                var xmlOffset = windowStart - absoluteOffset + run.Offset;
                if (xmlOffset < 0 || !seen.Add(xmlOffset))
                {
                    continue;
                }

                rows.Add(new XvdXmlRun(xmlOffset, run.Length, ExtractDisplayName(run.Xml)));
                if (rows.Count >= maxRows)
                {
                    return rows;
                }
            }
        }

        return rows;
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
        return TryFindXmlAsciiRuns(buffer)
            .Select(row => ExtractDisplayName(row.Xml))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static IReadOnlyList<(int Offset, int Length, string Xml)> TryFindXmlAsciiRuns(ReadOnlySpan<byte> buffer)
    {
        const int maxXmlLength = 0x100000;
        var rows = new List<(int Offset, int Length, string Xml)>();
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
                rows.Add((index, length, xml));
                index += Math.Max(0, length - 1);
                continue;
            }

            rows.Add((index, length, xml));
            index += Math.Max(0, length - 1);
        }

        return rows;
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

    private static long TryGetPfs0Size(FileStream stream, long absoluteOffset, long remainingLength)
    {
        Span<byte> header = stackalloc byte[0x10];
        stream.Position = absoluteOffset;
        if (stream.Read(header) != header.Length || !StartsWith(header, "PFS0"u8))
        {
            return 0;
        }

        var fileCount = ReadUInt32LittleEndian(header[4..]);
        var stringTableSize = ReadUInt32LittleEndian(header[8..]);
        if (fileCount == 0 || fileCount > 4096 || stringTableSize > 0x100000)
        {
            return 0;
        }

        var entriesSize = checked((long)fileCount * 0x18);
        var headerSize = 0x10 + entriesSize + stringTableSize;
        if (headerSize <= 0 || headerSize > remainingLength)
        {
            return 0;
        }

        var entries = new byte[entriesSize];
        stream.Position = absoluteOffset + 0x10;
        if (stream.Read(entries, 0, entries.Length) != entries.Length)
        {
            return 0;
        }

        var maxEnd = headerSize;
        for (var index = 0; index < fileCount; index++)
        {
            var entry = entries.AsSpan(index * 0x18, 0x18);
            var fileOffset = checked((long)Math.Min((ulong)long.MaxValue, ReadUInt64LittleEndian(entry)));
            var fileSize = checked((long)Math.Min((ulong)long.MaxValue, ReadUInt64LittleEndian(entry[8..])));
            if (fileOffset < 0 || fileSize < 0)
            {
                return 0;
            }

            maxEnd = Math.Max(maxEnd, headerSize + fileOffset + fileSize);
            if (maxEnd > remainingLength)
            {
                return 0;
            }
        }

        return maxEnd;
    }

    private static bool TryReadPfs0Entries(Stream stream, long absoluteOffset, long remainingLength, out IReadOnlyList<Pfs0EntryInfo> entries, out long headerSize)
    {
        entries = [];
        headerSize = 0;
        Span<byte> header = stackalloc byte[0x10];
        if (ReadAt(stream, absoluteOffset, header) != header.Length || !StartsWith(header, "PFS0"u8))
        {
            return false;
        }

        var fileCount = ReadUInt32LittleEndian(header[4..]);
        var stringTableSize = ReadUInt32LittleEndian(header[8..]);
        if (fileCount == 0 || fileCount > 4096 || stringTableSize > 0x100000)
        {
            return false;
        }

        var entriesSize = checked((long)fileCount * 0x18);
        headerSize = 0x10 + entriesSize + stringTableSize;
        if (headerSize <= 0 || headerSize > remainingLength)
        {
            return false;
        }

        var raw = new byte[entriesSize + stringTableSize];
        if (ReadAt(stream, absoluteOffset + 0x10, raw) != raw.Length)
        {
            return false;
        }

        var stringTable = raw.AsSpan((int)entriesSize, (int)stringTableSize);
        var rows = new List<Pfs0EntryInfo>();
        for (var index = 0; index < fileCount; index++)
        {
            var entry = raw.AsSpan(index * 0x18, 0x18);
            var fileOffset = checked((long)Math.Min((ulong)long.MaxValue, ReadUInt64LittleEndian(entry)));
            var fileSize = checked((long)Math.Min((ulong)long.MaxValue, ReadUInt64LittleEndian(entry[8..])));
            var nameOffset = ReadUInt32LittleEndian(entry[0x10..]);
            if (nameOffset >= stringTable.Length || headerSize + fileOffset + fileSize > remainingLength)
            {
                return false;
            }

            var name = ReadNullTerminatedUtf8(stringTable[(int)nameOffset..]);
            rows.Add(new Pfs0EntryInfo(string.IsNullOrWhiteSpace(name) ? $"entry_{index:D4}.bin" : name, fileOffset, fileSize));
        }

        entries = rows;
        return true;
    }

    private static string? TryReadNcaDetail(Stream stream, long absoluteOffset, long remainingLength)
    {
        return TryReadNcaHeaderInfo(stream, absoluteOffset, remainingLength)?.Detail;
    }

    private static NcaHeaderInfo? TryReadNcaHeaderInfo(Stream stream, long absoluteOffset, long remainingLength)
    {
        if (remainingLength < 0x220)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[0x400];
        var read = ReadAt(stream, absoluteOffset, header);
        if (read < 0x220 || !StartsWith(header[0x200..], "NCA"u8))
        {
            return null;
        }

        var version = header[0x203];
        if (version is < (byte)'0' or > (byte)'9')
        {
            return null;
        }

        var distribution = header[0x204] switch
        {
            0 => "download",
            1 => "gamecard",
            _ => $"0x{header[0x204]:X2}"
        };
        var contentType = header[0x205] switch
        {
            0 => "program",
            1 => "meta",
            2 => "control",
            3 => "manual",
            4 => "data",
            5 => "public data",
            _ => $"0x{header[0x205]:X2}"
        };
        var cryptoType = header[0x206];
        var keyIndex = header[0x207];
        var programId = ReadUInt64LittleEndian(header[0x210..]);

        var headerContentSize = (long)Math.Min((ulong)long.MaxValue, ReadUInt64LittleEndian(header[0x208..]));
        long sectionTableSize = 0;
        for (var index = 0; index < 4; index++)
        {
            var entry = header.Slice(0x240 + index * 0x10, 0x10);
            var startMedia = ReadUInt32LittleEndian(entry);
            var endMedia = ReadUInt32LittleEndian(entry[4..]);
            if (startMedia == 0 || endMedia <= startMedia)
            {
                continue;
            }

            var sectionEnd = endMedia * 0x200L;
            if (sectionEnd > sectionTableSize)
            {
                sectionTableSize = sectionEnd;
            }
        }

        var validHeaderSize = headerContentSize > 0 && headerContentSize <= remainingLength ? headerContentSize : 0;
        var validSectionSize = sectionTableSize > 0 && sectionTableSize <= remainingLength ? sectionTableSize : 0;
        var resolvedSize = validHeaderSize > 0 ? validHeaderSize : validSectionSize;
        if (resolvedSize <= 0)
        {
            resolvedSize = EstimateUnknownSize(remainingLength);
        }

        var sizeSource = validHeaderSize > 0
            ? "header contentSize"
            : validSectionSize > 0
                ? "section table end"
                : "estimated fallback";
        var detail = $"Nintendo Switch NCA content archive; {distribution}, {contentType}, crypto type 0x{cryptoType:X2}, key index 0x{keyIndex:X2}, program/content id 0x{programId:X16}, size 0x{resolvedSize:X} ({sizeSource})";

        return new NcaHeaderInfo(resolvedSize, detail);
    }

    private static IReadOnlyList<NcaSectionInfo> TryReadNcaSections(Stream stream, long absoluteOffset, long remainingLength)
    {
        if (remainingLength < 0x400)
        {
            return [];
        }

        Span<byte> header = stackalloc byte[0x400];
        if (ReadAt(stream, absoluteOffset, header) < header.Length || !StartsWith(header[0x200..], "NCA"u8))
        {
            return [];
        }

        var rows = new List<NcaSectionInfo>();
        for (var index = 0; index < 4; index++)
        {
            var entry = header.Slice(0x240 + index * 0x10, 0x10);
            var startMedia = ReadUInt32LittleEndian(entry);
            var endMedia = ReadUInt32LittleEndian(entry[4..]);
            if (startMedia == 0 || endMedia <= startMedia)
            {
                continue;
            }

            var offset = startMedia * 0x200L;
            var size = (endMedia - startMedia) * 0x200L;
            if (offset < 0 || size <= 0 || offset >= remainingLength)
            {
                continue;
            }

            rows.Add(new NcaSectionInfo(index, offset, Math.Min(size, remainingLength - offset)));
        }

        return rows;
    }

    private static string? TryReadCnmtDetail(Stream stream, long absoluteOffset, long remainingLength)
    {
        if (remainingLength < 0x20)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[0x20];
        if (ReadAt(stream, absoluteOffset, header) < header.Length)
        {
            return null;
        }

        var titleId = ReadUInt64LittleEndian(header);
        var version = ReadUInt32LittleEndian(header[8..]);
        var type = header[0x0C] switch
        {
            0x01 => "system program",
            0x02 => "system data",
            0x03 => "system update",
            0x04 => "boot image package",
            0x05 => "boot image package safe",
            0x80 => "application",
            0x81 => "patch",
            0x82 => "add-on content",
            0x83 => "delta",
            _ => $"0x{header[0x0C]:X2}"
        };
        var contentCount = ReadUInt16LittleEndian(header[0x0E..]);
        var metaCount = ReadUInt16LittleEndian(header[0x10..]);
        return $"Nintendo Switch CNMT content metadata; title id 0x{titleId:X16}, version {version}, type {type}, content entries {contentCount:N0}, meta entries {metaCount:N0}";
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

    private static string ReadNullTerminatedUtf8(ReadOnlySpan<byte> value)
    {
        var length = value.IndexOf((byte)0);
        if (length < 0)
        {
            length = value.Length;
        }

        return Encoding.UTF8.GetString(value[..length]);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.Length == 0 ? "entry.bin" : builder.ToString();
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

    private static ulong ReadUInt64LittleEndian(ReadOnlySpan<byte> value)
    {
        return ReadUInt32LittleEndian(value) | ((ulong)ReadUInt32LittleEndian(value[4..]) << 32);
    }

    private sealed record Pfs0EntryInfo(string Name, long Offset, long Size);

    private sealed record NcaSectionInfo(int Index, long Offset, long Size);

    private sealed record NcaHeaderInfo(long ResolvedSize, string Detail);

    private sealed record XvdXmlRun(long Offset, long Length, string? DisplayName);
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
