using DiscUtils.Ntfs;
using DiscUtils.Ntfs.Internals;
using DiscUtils.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FATXTools.Wpf;

internal sealed class XboxStorageImage : IDisposable
{
    private const int SectorSize = 512;
    private static readonly Guid EmptyPartitionType = Guid.Empty;
    private static readonly Guid BasicDataPartitionType = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    private static readonly Dictionary<Guid, string> KnownXboxPartitionTypes = new()
    {
        [new Guid("B3727DA5-A3AC-4B3D-9FD6-2EA54441011B")] = "Temp Content",
        [new Guid("C90D7A47-CCB9-4CBA-8C66-0459F6B85724")] = "System Support",
        [new Guid("9A056AD7-32ED-4141-AEB1-AFB9BD5565DC")] = "System Update",
        [new Guid("24B2197C-9D01-45F9-A8E1-DBBCFA161EB2")] = "System Update 2",
        [new Guid("A2344BDB-D6DE-4766-9EB5-4109A12228E5")] = "User Content",
        [new Guid("25E8A1B2-0B2A-4474-93FA-35B847D97EE5")] = "User Content",
        [new Guid("5B114955-4A1C-45C4-86DC-D95070008139")] = "User Content"
    };

    private readonly FileStream _stream;

    private XboxStorageImage(
        string sourcePath,
        FileStream stream,
        IReadOnlyList<XboxNtfsVolume> volumes,
        IReadOnlyList<XboxBootFileSystemVolume> bootFileSystems,
        bool hasXboxSignature)
    {
        SourcePath = sourcePath;
        _stream = stream;
        Volumes = volumes;
        BootFileSystems = bootFileSystems;
        HasXboxBootSignature = hasXboxSignature;
    }

    public string SourcePath { get; }

    public IReadOnlyList<XboxNtfsVolume> Volumes { get; }

    public IReadOnlyList<XboxBootFileSystemVolume> BootFileSystems { get; }

    public bool HasXboxBootSignature { get; }

    public static XboxStorageImage Open(string sourcePath)
    {
        var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            options: FileOptions.RandomAccess);

        try
        {
            var partitions = ReadGptPartitions(stream);
            if (partitions.Count == 0)
            {
                throw new InvalidDataException("No GPT partitions were found.");
            }

            var hasXboxBootSignature = HasXboxExternalBootSignature(stream);
            var volumes = new List<XboxNtfsVolume>();
            var bootFileSystems = new List<XboxBootFileSystemVolume>();
            foreach (var partition in partitions)
            {
                if (XboxBootFileSystemVolume.TryOpen(sourcePath, stream, partition, out var bootFileSystem))
                {
                    bootFileSystems.Add(bootFileSystem);
                    continue;
                }

                if (!LooksLikeXboxPartition(partition) && !LooksLikeNtfs(stream, partition.Offset))
                {
                    continue;
                }

                var slice = new PartitionSliceStream(stream, partition.Offset, partition.Length);
                try
                {
                    var ntfs = new NtfsFileSystem(slice);
                    var label = string.IsNullOrWhiteSpace(partition.Name)
                        ? GetKnownPartitionName(partition.TypeGuid) ?? ntfs.VolumeLabel
                        : partition.Name;
                    var family = DetermineFamily(partition, hasXboxBootSignature);
                    volumes.Add(new XboxNtfsVolume(ntfs, partition, label, family, sourcePath));
                }
                catch
                {
                    slice.Dispose();
                }
            }

            if (volumes.Count == 0 && bootFileSystems.Count == 0)
            {
                throw new InvalidDataException("GPT was detected, but no readable Xbox partitions were found.");
            }

            return new XboxStorageImage(sourcePath, stream, volumes, bootFileSystems, hasXboxBootSignature);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var volume in Volumes)
        {
            volume.Dispose();
        }

        _stream.Dispose();
    }

    private static List<GptPartitionRecord> ReadGptPartitions(Stream stream)
    {
        foreach (var logicalSectorSize in new[] { SectorSize, 4096 })
        {
            var partitions = ReadGptPartitions(stream, logicalSectorSize);
            if (partitions.Count > 0)
            {
                return partitions;
            }
        }

        return [];
    }

    private static List<GptPartitionRecord> ReadGptPartitions(Stream stream, int logicalSectorSize)
    {
        if (stream.Length < logicalSectorSize * 2L)
        {
            return [];
        }

        var headerBuffer = new byte[logicalSectorSize];
        var header = headerBuffer.AsSpan();
        ReadExactlyAt(stream, logicalSectorSize, header);
        if (!header[..8].SequenceEqual("EFI PART"u8))
        {
            return [];
        }

        var partitionEntryLba = ReadUInt64LittleEndian(header, 72);
        var partitionEntryCount = ReadUInt32LittleEndian(header, 80);
        var partitionEntrySize = ReadUInt32LittleEndian(header, 84);
        if (partitionEntryLba == 0 || partitionEntryCount == 0 || partitionEntrySize < 128)
        {
            return [];
        }

        var records = new List<GptPartitionRecord>();
        var entryBuffer = new byte[partitionEntrySize];
        var entriesOffset = checked((long)partitionEntryLba * logicalSectorSize);
        for (var index = 0u; index < partitionEntryCount; index++)
        {
            var entryOffset = entriesOffset + checked(index * (long)partitionEntrySize);
            if (entryOffset < 0 || entryOffset + partitionEntrySize > stream.Length)
            {
                break;
            }

            ReadExactlyAt(stream, entryOffset, entryBuffer);
            var typeGuid = new Guid(entryBuffer.AsSpan(0, 16));
            if (typeGuid == EmptyPartitionType)
            {
                continue;
            }

            var firstLba = ReadUInt64LittleEndian(entryBuffer, 32);
            var lastLba = ReadUInt64LittleEndian(entryBuffer, 40);
            if (firstLba == 0 || lastLba < firstLba)
            {
                continue;
            }

            var offset = checked((long)firstLba * logicalSectorSize);
            var length = checked((long)(lastLba - firstLba + 1) * logicalSectorSize);
            var name = Encoding.Unicode.GetString(entryBuffer, 56, Math.Min(72, entryBuffer.Length - 56)).TrimEnd('\0');
            records.Add(new GptPartitionRecord(index + 1, typeGuid, offset, length, name, logicalSectorSize));
        }

        return records;
    }

    private static bool HasXboxExternalBootSignature(Stream stream)
    {
        Span<byte> signature = stackalloc byte[2];
        ReadExactlyAt(stream, 0x1FE, signature);
        return signature[0] == 0x99 && signature[1] == 0xCC;
    }

    private static bool LooksLikeXboxPartition(GptPartitionRecord partition)
    {
        return KnownXboxPartitionTypes.ContainsKey(partition.TypeGuid)
               || partition.Name.Contains("Content", StringComparison.OrdinalIgnoreCase)
               || partition.Name.Contains("System", StringComparison.OrdinalIgnoreCase)
               || partition.TypeGuid == BasicDataPartitionType;
    }

    private static bool LooksLikeNtfs(Stream stream, long partitionOffset)
    {
        if (partitionOffset < 0 || partitionOffset + SectorSize > stream.Length)
        {
            return false;
        }

        Span<byte> sector = stackalloc byte[SectorSize];
        ReadExactlyAt(stream, partitionOffset, sector);
        return sector.Slice(3, 4).SequenceEqual("NTFS"u8);
    }

    private static XboxStorageFamily DetermineFamily(GptPartitionRecord partition, bool hasXboxBootSignature)
    {
        if (KnownXboxPartitionTypes.ContainsKey(partition.TypeGuid) || IsKnownXboxPartitionName(partition.Name))
        {
            return partition.LogicalSectorSize >= 4096 ? XboxStorageFamily.XboxSeries : XboxStorageFamily.XboxOne;
        }

        return hasXboxBootSignature ? XboxStorageFamily.XboxExternal : XboxStorageFamily.GptNtfs;
    }

    private static bool IsKnownXboxPartitionName(string name)
    {
        return name.Equals("Temp Content", StringComparison.OrdinalIgnoreCase)
               || name.Equals("User Content", StringComparison.OrdinalIgnoreCase)
               || name.Equals("System Support", StringComparison.OrdinalIgnoreCase)
               || name.Equals("System Update", StringComparison.OrdinalIgnoreCase)
               || name.Equals("System Update 2", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Auto Install", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Future Growth", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetKnownPartitionName(Guid typeGuid)
    {
        return KnownXboxPartitionTypes.TryGetValue(typeGuid, out var name) ? name : null;
    }

    private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> buffer, int offset)
    {
        return BitConverter.ToUInt32(buffer.Slice(offset, 4));
    }

    private static ulong ReadUInt64LittleEndian(ReadOnlySpan<byte> buffer, int offset)
    {
        return BitConverter.ToUInt64(buffer.Slice(offset, 8));
    }

    private static void ReadExactlyAt(Stream stream, long offset, Span<byte> buffer)
    {
        lock (stream)
        {
            stream.Position = offset;
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = stream.Read(buffer[totalRead..]);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of disk image.");
                }

                totalRead += read;
            }
        }
    }
}

public enum XboxStorageFamily
{
    XboxOne,
    XboxSeries,
    XboxExternal,
    GptNtfs
}

public sealed class XboxNtfsVolume : IDisposable
{
    private readonly NtfsFileSystem _fileSystem;
    private readonly GptPartitionRecord _partition;
    private readonly string _sourcePath;

    internal XboxNtfsVolume(NtfsFileSystem fileSystem, GptPartitionRecord partition, string label, XboxStorageFamily family, string sourcePath)
    {
        _fileSystem = fileSystem;
        _partition = partition;
        _sourcePath = sourcePath;
        Label = string.IsNullOrWhiteSpace(label) ? $"Partition {partition.Index}" : label;
        Family = family;
    }

    public string Name => Label;

    public string Label { get; }

    public XboxStorageFamily Family { get; }

    public string FamilyText => Family switch
    {
        XboxStorageFamily.XboxOne => "Xbox One GPT/NTFS",
        XboxStorageFamily.XboxSeries => "Xbox Series GPT/NTFS",
        XboxStorageFamily.XboxExternal => "Xbox GPT/NTFS external",
        _ => "GPT/NTFS"
    };

    public long Offset => _partition.Offset;

    public long Length => _partition.Length;

    public Guid PartitionTypeGuid => _partition.TypeGuid;

    public string PartitionLabel => _partition.Name;

    public long ClusterSize => _fileSystem.ClusterSize;

    public long TotalSpace => _fileSystem.Size;

    public long UsedSpace => _fileSystem.UsedSpace;

    public long FreeSpace => _fileSystem.AvailableSpace;

    public string SourcePath => _sourcePath;

    public IReadOnlyList<NtfsAllocationRun> ReadAllocationRuns()
    {
        if (_fileSystem.TotalClusters <= 0)
        {
            return [];
        }

        try
        {
            using var bitmap = _fileSystem.OpenFile(@"$Bitmap::$DATA", FileMode.Open, FileAccess.Read);
            return ReadAllocationRuns(bitmap, _fileSystem.TotalClusters);
        }
        catch
        {
            try
            {
                using var bitmap = _fileSystem.OpenFile(@"\$Bitmap::$DATA", FileMode.Open, FileAccess.Read);
                return ReadAllocationRuns(bitmap, _fileSystem.TotalClusters);
            }
            catch
            {
                return [];
            }
        }
    }

    public IReadOnlyList<XboxFileEntry> GetRoot()
    {
        return GetChildren(null);
    }

    public IReadOnlyList<XboxFileEntry> GetChildren(XboxFileEntry? directory)
    {
        var path = directory?.Path ?? @"\";
        return _fileSystem
            .GetFileSystemEntries(path)
            .Select(CreateEntry)
            .OrderBy(entry => entry.IsDirectory ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<XboxFileEntry> ScanMetadata(CancellationToken cancellationToken, IProgress<NtfsMetadataScanProgress>? progress = null)
    {
        var mft = _fileSystem.GetMasterFileTable();
        var allEntries = mft.GetEntries(EntryStates.All).ToList();
        var entriesByIndex = allEntries
            .GroupBy(entry => entry.Index)
            .ToDictionary(group => group.Key, group => group.First());
        var entries = allEntries
            .Where(entry => entry.Attributes.OfType<FileNameAttribute>().Any())
            .ToList();
        var snapshots = entries
            .Select(entry => CreateMftSnapshot(entry, entriesByIndex))
            .Where(snapshot => snapshot != null)
            .Cast<NtfsMftEntrySnapshot>()
            .ToDictionary(snapshot => snapshot.Index);

        var rows = new List<XboxFileEntry>();
        var current = 0;
        foreach (var snapshot in snapshots.Values.OrderBy(snapshot => snapshot.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            current++;
            progress?.Report(new NtfsMetadataScanProgress(current, snapshots.Count));

            var path = BuildBestEffortPath(snapshot, snapshots);
            if (snapshot.InUse && TryCreateEntryFromPath(path, snapshot, out var activeEntry))
            {
                rows.Add(activeEntry);
                continue;
            }

            rows.Add(CreateMetadataEntry(path, snapshot));
        }

        progress?.Report(new NtfsMetadataScanProgress(snapshots.Count, snapshots.Count));
        return rows
            .OrderBy(row => row.IsDeleted ? 1 : 0)
            .ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void CopyFile(XboxFileEntry entry, string destinationPath)
    {
        CopyFile(entry, destinationPath, null, CancellationToken.None);
    }

    public void CopyFile(XboxFileEntry entry, string destinationPath, Action<long>? progress, CancellationToken cancellationToken)
    {
        if (entry.IsDirectory)
        {
            throw new InvalidOperationException("Select a file entry to export.");
        }

        if (!entry.IsDeleted && entry.PathExists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = _fileSystem.OpenFile(entry.Path, FileMode.Open, FileAccess.Read);
            using var output = File.Create(destinationPath);
            CopyStream(input, output, progress, cancellationToken);
            return;
        }

        if (entry.ResidentData is { Length: > 0 } residentData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(destinationPath, residentData);
            progress?.Invoke(residentData.Length);
            return;
        }

        if (entry.Length == 0)
        {
            using (File.Create(destinationPath))
            {
            }

            return;
        }

        if (entry.Extents.Count == 0)
        {
            throw new InvalidOperationException("This NTFS metadata entry does not contain recoverable data extents.");
        }

        CopyRawExtents(entry.Extents, entry.Length, destinationPath, progress, cancellationToken);
    }

    public void Dispose()
    {
        _fileSystem.Dispose();
    }

    private XboxFileEntry CreateEntry(string path)
    {
        return CreateEntry(path, null);
    }

    private XboxFileEntry CreateEntry(string path, NtfsMftEntrySnapshot? metadata)
    {
        var normalized = NormalizePath(path);
        var isDirectory = _fileSystem.DirectoryExists(normalized);
        var name = normalized == @"\" ? "Root" : normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? normalized;
        var length = isDirectory ? 0L : SafeGet(() => _fileSystem.GetFileLength(normalized), 0L);
        var created = SafeGet(() => _fileSystem.GetCreationTimeUtc(normalized).ToLocalTime(), DateTime.MinValue);
        var modified = SafeGet(() => _fileSystem.GetLastWriteTimeUtc(normalized).ToLocalTime(), DateTime.MinValue);
        var accessed = SafeGet(() => _fileSystem.GetLastAccessTimeUtc(normalized).ToLocalTime(), DateTime.MinValue);
        var attributes = SafeGet(() => _fileSystem.GetAttributes(normalized), FileAttributes.Normal);
        var fileId = SafeGet(() => _fileSystem.GetFileId(normalized), 0L);
        var alternateDataStreams = isDirectory
            ? []
            : SafeGet(() => _fileSystem.GetAlternateDataStreams(normalized).ToList(), new List<string>());
        var extents = isDirectory
            ? []
            : SafeGet(() => _fileSystem.PathToExtents(normalized).ToList(), new List<StreamExtent>());
        var physicalExtents = extents
            .Select(extent => new FileExtent(Offset + extent.Start, extent.Length))
            .ToList();
        var firstOffset = physicalExtents.Count == 0 ? Offset : physicalExtents[0].Offset;
        var firstCluster = physicalExtents.Count == 0 || ClusterSize <= 0 ? 0 : (long)(physicalExtents[0].Offset - Offset) / ClusterSize;
        var childCounts = isDirectory ? CountChildren(normalized) : (Folders: 0, Files: 0);

        return new XboxFileEntry(
            this,
            normalized,
            name,
            isDirectory,
            length,
            created,
            modified,
            accessed,
            attributes,
            firstOffset,
            firstCluster,
            physicalExtents,
            childCounts.Folders,
            childCounts.Files,
            PathExists: true,
            IsDeleted: metadata?.InUse == false,
            IsNtfsMetadata: metadata?.IsMetaFile ?? name.StartsWith('$'),
            MftRecordIndex: metadata?.Index ?? GetMftIndexFromFileId(fileId),
            SequenceNumber: metadata?.SequenceNumber ?? 0,
            ParentMftRecordIndex: metadata?.ParentIndex ?? -1,
            MetadataStatus: metadata == null ? "Active filesystem entry" : metadata.InUse ? "Active MFT record" : "Deleted MFT record",
            AlternateDataStreams: alternateDataStreams);
    }

    private bool TryCreateEntryFromPath(string path, NtfsMftEntrySnapshot metadata, out XboxFileEntry entry)
    {
        entry = null!;
        try
        {
            var normalized = NormalizePath(path);
            if (!_fileSystem.DirectoryExists(normalized) && !_fileSystem.FileExists(normalized))
            {
                return false;
            }

            entry = CreateEntry(normalized, metadata);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private XboxFileEntry CreateMetadataEntry(string path, NtfsMftEntrySnapshot metadata)
    {
        var extents = metadata.InUse && !metadata.IsDirectory
            ? SafeGet(() => _fileSystem.PathToExtents(path).Select(extent => new FileExtent(Offset + extent.Start, extent.Length)).ToList(), metadata.DataExtents)
            : metadata.DataExtents;
        var firstOffset = extents.Count == 0 ? Offset : extents[0].Offset;
        var firstCluster = extents.Count == 0 || ClusterSize <= 0 ? 0 : (long)(extents[0].Offset - Offset) / ClusterSize;

        return new XboxFileEntry(
            this,
            NormalizePath(path),
            metadata.Name,
            metadata.IsDirectory,
            metadata.Length,
            metadata.Created,
            metadata.Modified,
            metadata.Accessed,
            metadata.Attributes,
            firstOffset,
            firstCluster,
            extents,
            0,
            0,
            PathExists: false,
            IsDeleted: !metadata.InUse,
            IsNtfsMetadata: metadata.IsMetaFile,
            MftRecordIndex: metadata.Index,
            SequenceNumber: metadata.SequenceNumber,
            ParentMftRecordIndex: metadata.ParentIndex,
            MetadataStatus: metadata.InUse ? "Active MFT record, path unavailable" : "Deleted MFT record",
            AlternateDataStreams: [],
            ResidentData: metadata.ResidentData);
    }

    private NtfsMftEntrySnapshot? CreateMftSnapshot(MasterFileTableEntry entry, IReadOnlyDictionary<long, MasterFileTableEntry> entriesByIndex)
    {
        var names = entry.Attributes.OfType<FileNameAttribute>()
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.FileName))
            .OrderBy(GetFileNameNamespacePriority)
            .ToList();
        var name = names.FirstOrDefault();
        if (name == null)
        {
            return null;
        }

        var standard = entry.Attributes.OfType<StandardInformationAttribute>().FirstOrDefault();
        var inUse = entry.Flags.HasFlag(MasterFileTableEntryFlags.InUse);
        var isDirectory = entry.Flags.HasFlag(MasterFileTableEntryFlags.IsDirectory);
        var isMetaFile = entry.Flags.HasFlag(MasterFileTableEntryFlags.IsMetaFile);
        var attributes = ConvertAttributes(standard?.FileAttributes ?? name.FileAttributes);
        var dataAttributes = GetDataAttributes(entry, entriesByIndex).ToList();
        var primaryDataAttribute = dataAttributes.FirstOrDefault();
        var residentData = !isDirectory && primaryDataAttribute is { IsResident: true }
            ? ReadResidentData(primaryDataAttribute)
            : null;
        var dataExtents = !isDirectory
            ? dataAttributes
                .Where(attribute => !attribute.IsResident)
                .SelectMany(ReadDataExtents)
                .OrderBy(extent => extent.Offset)
                .ToList()
            : [];
        var length = primaryDataAttribute == null
            ? Math.Max(0, name.RealSize)
            : Math.Max(0, Math.Min(Math.Max(name.RealSize, primaryDataAttribute.ContentLength), primaryDataAttribute.ContentLength));

        return new NtfsMftEntrySnapshot(
            entry.Index,
            entry.SequenceNumber,
            name.ParentDirectory.RecordIndex,
            name.FileName,
            isDirectory,
            inUse,
            isMetaFile,
            length,
            standard?.CreationTime.ToLocalTime() ?? name.CreationTime.ToLocalTime(),
            standard?.ModificationTime.ToLocalTime() ?? name.ModificationTime.ToLocalTime(),
            standard?.LastAccessTime.ToLocalTime() ?? name.LastAccessTime.ToLocalTime(),
            standard?.MasterFileTableChangedTime.ToLocalTime() ?? name.MasterFileTableChangedTime.ToLocalTime(),
            attributes,
            dataExtents,
            residentData);
    }

    private static IEnumerable<GenericAttribute> GetDataAttributes(MasterFileTableEntry entry, IReadOnlyDictionary<long, MasterFileTableEntry> entriesByIndex)
    {
        foreach (var attribute in entry.Attributes
                     .OfType<GenericAttribute>()
                     .Where(attribute => attribute.AttributeType == AttributeType.Data && string.IsNullOrEmpty(attribute.Name))
                     .OrderBy(attribute => attribute.Identifier))
        {
            yield return attribute;
        }

        var extensionReferences = entry.Attributes
            .OfType<AttributeListAttribute>()
            .SelectMany(attribute => attribute.Entries)
            .Where(attribute => attribute.AttributeType == AttributeType.Data && string.IsNullOrEmpty(attribute.AttributeName))
            .OrderBy(attribute => attribute.FirstFileCluster)
            .Select(attribute => (Index: attribute.MasterFileTableEntry.RecordIndex, Identifier: attribute.AttributeIdentifier))
            .Distinct()
            .Where(reference => reference.Index != entry.Index);

        foreach (var reference in extensionReferences)
        {
            if (!entriesByIndex.TryGetValue(reference.Index, out var extensionEntry))
            {
                continue;
            }

            foreach (var attribute in extensionEntry.Attributes
                         .OfType<GenericAttribute>()
                         .Where(attribute => attribute.AttributeType == AttributeType.Data
                                             && string.IsNullOrEmpty(attribute.Name)
                                             && (reference.Identifier <= 0 || attribute.Identifier == reference.Identifier))
                         .OrderBy(attribute => attribute.Identifier))
            {
                yield return attribute;
            }
        }
    }

    private IReadOnlyList<FileExtent> ReadDataExtents(GenericAttribute dataAttribute)
    {
        try
        {
            var contentLength = Math.Max(0, dataAttribute.ContentLength);
            return dataAttribute.Content
                .GetExtentsInRange(0, contentLength)
                .Where(extent => extent.Length > 0)
                .Select(extent => new FileExtent(Offset + extent.Start, extent.Length))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static byte[]? ReadResidentData(GenericAttribute dataAttribute)
    {
        try
        {
            var length = dataAttribute.ContentLength;
            if (length <= 0 || length > int.MaxValue)
            {
                return length == 0 ? [] : null;
            }

            var buffer = new byte[(int)length];
            var read = dataAttribute.Content.Read(0, buffer, 0, buffer.Length);
            if (read == buffer.Length)
            {
                return buffer;
            }

            Array.Resize(ref buffer, Math.Max(0, read));
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    private void CopyRawExtents(IReadOnlyList<FileExtent> extents, long length, string destinationPath, Action<long>? progress, CancellationToken cancellationToken)
    {
        const int bufferSize = 0x100000;
        var remaining = length;
        var buffer = new byte[bufferSize];

        using var input = new FileStream(_sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.RandomAccess);
        using var output = File.Create(destinationPath);
        foreach (var extent in extents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0)
            {
                break;
            }

            var offset = Math.Max(0, extent.Offset);
            var readable = Math.Min(extent.Length, remaining);
            if (offset >= input.Length || readable <= 0)
            {
                break;
            }

            input.Position = offset;
            readable = Math.Min(readable, input.Length - offset);
            while (readable > 0 && remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, Math.Min(readable, remaining)));
                if (read == 0)
                {
                    return;
                }

                output.Write(buffer, 0, read);
                readable -= read;
                remaining -= read;
                progress?.Invoke(read);
            }
        }
    }

    private static void CopyStream(Stream input, Stream output, Action<long>? progress, CancellationToken cancellationToken)
    {
        const int bufferSize = 0x100000;
        var buffer = new byte[bufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return;
            }

            output.Write(buffer, 0, read);
            progress?.Invoke(read);
        }
    }

    private static IReadOnlyList<NtfsAllocationRun> ReadAllocationRuns(Stream bitmap, long totalClusters)
    {
        const int bufferSize = 0x100000;
        var buffer = new byte[bufferSize];
        var runs = new List<NtfsAllocationRun>();
        long cluster = 0;
        long? runStart = null;

        while (cluster < totalClusters)
        {
            var read = bitmap.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            for (var byteIndex = 0; byteIndex < read && cluster < totalClusters; byteIndex++)
            {
                var value = buffer[byteIndex];
                for (var bit = 0; bit < 8 && cluster < totalClusters; bit++, cluster++)
                {
                    var allocated = (value & (1 << bit)) != 0;
                    if (allocated)
                    {
                        runStart ??= cluster;
                        continue;
                    }

                    if (runStart is { } start)
                    {
                        runs.Add(new NtfsAllocationRun(start, cluster - start));
                        runStart = null;
                    }
                }
            }
        }

        if (runStart is { } trailingStart)
        {
            runs.Add(new NtfsAllocationRun(trailingStart, totalClusters - trailingStart));
        }

        return runs;
    }

    private static int GetFileNameNamespacePriority(FileNameAttribute attribute)
    {
        return attribute.FileNameNamespace.ToString() switch
        {
            "Win32" => 0,
            "Posix" => 1,
            "Win32AndDos" => 2,
            "Dos" => 3,
            _ => 4
        };
    }

    private static string BuildBestEffortPath(NtfsMftEntrySnapshot snapshot, IReadOnlyDictionary<long, NtfsMftEntrySnapshot> snapshots)
    {
        if (snapshot.Index == 5 || snapshot.ParentIndex == snapshot.Index)
        {
            return @"\";
        }

        var names = new Stack<string>();
        var current = snapshot;
        var seen = new HashSet<long>();
        while (seen.Add(current.Index))
        {
            if (current.Index != 5 && !string.IsNullOrWhiteSpace(current.Name))
            {
                names.Push(current.Name);
            }

            if (current.ParentIndex == current.Index || !snapshots.TryGetValue(current.ParentIndex, out var parent))
            {
                break;
            }

            if (parent.Index == 5)
            {
                break;
            }

            current = parent;
        }

        return names.Count == 0 ? @"\" : @"\" + string.Join("\\", names);
    }

    private static long GetMftIndexFromFileId(long fileId)
    {
        return fileId <= 0 ? -1 : fileId & 0x0000FFFFFFFFFFFFL;
    }

    private static FileAttributes ConvertAttributes(NtfsFileAttributes attributes)
    {
        return (FileAttributes)((uint)attributes & 0xFFFF);
    }

    private (int Folders, int Files) CountChildren(string path)
    {
        try
        {
            var folders = 0;
            var files = 0;
            foreach (var child in _fileSystem.GetFileSystemEntries(path))
            {
                if (_fileSystem.DirectoryExists(NormalizePath(child)))
                {
                    folders++;
                }
                else
                {
                    files++;
                }
            }

            return (folders, files);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            return @"\";
        }

        var normalized = path.Replace('/', '\\');
        return normalized.StartsWith('\\') ? normalized : "\\" + normalized;
    }

    private static T SafeGet<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }
}

public sealed record XboxFileEntry(
    XboxNtfsVolume Volume,
    string Path,
    string Name,
    bool IsDirectory,
    long Length,
    DateTime Created,
    DateTime Modified,
    DateTime Accessed,
    FileAttributes Attributes,
    long Offset,
    long Cluster,
    IReadOnlyList<FileExtent> Extents,
    int FolderCount,
    int FileCount,
    bool PathExists = true,
    bool IsDeleted = false,
    bool IsNtfsMetadata = false,
    long MftRecordIndex = -1,
    int SequenceNumber = 0,
    long ParentMftRecordIndex = -1,
    string MetadataStatus = "",
    IReadOnlyList<string>? AlternateDataStreams = null,
    byte[]? ResidentData = null)
{
    public int AlternateDataStreamCount => AlternateDataStreams?.Count ?? 0;

    public string FragmentationStatus
    {
        get
        {
            if (IsDirectory)
            {
                return string.Empty;
            }

            if (Extents.Count == 0)
            {
                return Length == 0 ? "Empty" : "Resident or sparse";
            }

            return Extents.Count == 1 ? "Contiguous" : $"Fragmented into {Extents.Count:N0} runs";
        }
    }

    public string ExtentSummary
    {
        get
        {
            if (Extents.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(", ", Extents.Take(8).Select(extent => $"0x{extent.Offset:X}+0x{extent.Length:X}"));
        }
    }
}

public sealed record FileExtent(long Offset, long Length);

public sealed record NtfsAllocationRun(long StartCluster, long ClusterCount);

public sealed record NtfsMetadataScanProgress(int Current, int Total);

public sealed class XboxBootFileSystemVolume : GenericFileSystemVolume
{
    private const int HeaderSize = 0x400;
    private const int PageSize = 0x1000;
    private const int MaxFileCount = 58;
    private static readonly string[] KnownFileNames =
    [
        "1smcbl_a.bin",
        "header.bin",
        "devkit.ini",
        "mtedata.cfg",
        "certkeys.bin",
        "smcerr.log",
        "system.xvd",
        "$sospf.xvd",
        "download.xvd",
        "smc_s.cfg",
        "sp_s.cfg",
        "os_s.cfg",
        "smc_d.cfg",
        "sp_d.cfg",
        "os_d.cfg",
        "smcfw.bin",
        "boot.bin",
        "host.xvd",
        "settings.xvd",
        "1smcbl_b.bin",
        "bootanim.dat",
        "obsolete.001",
        "update.cfg",
        "obsolete.002",
        "hwinit.cfg",
        "qaslt.xvd",
        "sp_s.bak",
        "update2.cfg",
        "recovery.dat",
        "dump.lng",
        "os_d_dev.cfg",
        "os_glob.cfg",
        "sp_s.alt"
    ];

    private readonly List<GenericFileSystemEntry> _root;

    private XboxBootFileSystemVolume(
        string sourcePath,
        GenericPartitionCandidate partition,
        byte formatVersion,
        byte sequenceVersion,
        ushort layoutVersion,
        List<GenericFileSystemEntry> root)
        : base(sourcePath, partition, "Xbox Boot File System (XBFS)")
    {
        FormatVersion = formatVersion;
        SequenceVersion = sequenceVersion;
        LayoutVersion = layoutVersion;
        _root = root;
    }

    public byte FormatVersion { get; }

    public byte SequenceVersion { get; }

    public ushort LayoutVersion { get; }

    public override long ClusterSize => PageSize;

    public override long UsedSpace => _root.Where(entry => !entry.IsDirectory && entry.Extents.Count > 0).Sum(entry => Math.Max(0, entry.Length));

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot()
    {
        return _root;
    }

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        progress?.Report(0);
        return [];
    }

    internal static bool TryOpen(string sourcePath, Stream stream, GptPartitionRecord partition, out XboxBootFileSystemVolume volume)
    {
        volume = null!;
        if (!partition.Name.Equals("XBFS", StringComparison.OrdinalIgnoreCase) || partition.Length < HeaderSize)
        {
            return false;
        }

        var header = new byte[HeaderSize];
        try
        {
            ReadExactlyAt(stream, partition.Offset, header);
        }
        catch
        {
            return false;
        }

        if (!header.AsSpan(0, 4).SequenceEqual("SFBX"u8))
        {
            return false;
        }

        var candidate = new GenericPartitionCandidate(
            partition.Index,
            partition.TypeGuid,
            partition.Offset,
            partition.Length,
            string.IsNullOrWhiteSpace(partition.Name) ? "XBFS" : partition.Name,
            partition.LogicalSectorSize);
        var formatVersion = header[4];
        var sequenceVersion = header[5];
        var layoutVersion = BitConverter.ToUInt16(header, 6);
        var entries = new List<GenericFileSystemEntry>();
        volume = new XboxBootFileSystemVolume(sourcePath, candidate, formatVersion, sequenceVersion, layoutVersion, entries);

        for (var index = 0; index < MaxFileCount; index++)
        {
            var entryOffset = 0x20 + index * 0x10;
            var offsetPages = BitConverter.ToUInt32(header, entryOffset);
            var sizePages = BitConverter.ToUInt32(header, entryOffset + 4);
            if (sizePages == 0)
            {
                continue;
            }

            var physicalOffset = checked((long)offsetPages * PageSize);
            var length = checked((long)sizePages * PageSize);
            var isReadable = physicalOffset >= partition.Offset
                             && physicalOffset < partition.Offset + partition.Length
                             && physicalOffset + length <= partition.Offset + partition.Length;
            if (!isReadable)
            {
                continue;
            }

            var name = GetFileName(index);
            entries.Add(new GenericFileSystemEntry
            {
                Volume = volume,
                Path = "/" + name,
                Name = name,
                Kind = "File",
                Length = length,
                Offset = physicalOffset,
                Cluster = offsetPages,
                Attributes = $"Entry {index}",
                MetadataStatus = $"XBFS entry {index}, {sizePages:N0} page(s)",
                Extents = [new FileExtent(physicalOffset, length)]
            });
        }

        return true;
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return (long)cluster * PageSize;
    }

    private static string GetFileName(int index)
    {
        return index < KnownFileNames.Length ? KnownFileNames[index] : $"entry_{index:00}.bin";
    }

    private static void ReadExactlyAt(Stream stream, long offset, Span<byte> buffer)
    {
        lock (stream)
        {
            stream.Position = offset;
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = stream.Read(buffer[totalRead..]);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of disk image.");
                }

                totalRead += read;
            }
        }
    }
}

internal sealed record NtfsMftEntrySnapshot(
    long Index,
    int SequenceNumber,
    long ParentIndex,
    string Name,
    bool IsDirectory,
    bool InUse,
    bool IsMetaFile,
    long Length,
    DateTime Created,
    DateTime Modified,
    DateTime Accessed,
    DateTime MftChanged,
    FileAttributes Attributes,
    IReadOnlyList<FileExtent> DataExtents,
    byte[]? ResidentData);

internal sealed record GptPartitionRecord(uint Index, Guid TypeGuid, long Offset, long Length, string Name, int LogicalSectorSize = 512);

internal sealed class PartitionSliceStream : Stream
{
    private readonly Stream _baseStream;
    private readonly long _offset;
    private readonly long _length;
    private long _position;

    public PartitionSliceStream(Stream baseStream, long offset, long length)
    {
        _baseStream = baseStream;
        _offset = offset;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length)
        {
            return 0;
        }

        var toRead = (int)Math.Min(count, _length - _position);
        lock (_baseStream)
        {
            _baseStream.Position = _offset + _position;
            var read = _baseStream.Read(buffer, offset, toRead);
            _position += read;
            return read;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => _position
        };

        _position = Math.Clamp(newPosition, 0, _length);
        return _position;
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}
