using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FATXTools.Wpf;

internal sealed class RawConsoleVolume : GenericFileSystemVolume
{
    private readonly string _description;
    private readonly List<GenericFileSystemEntry> _root = [];

    public RawConsoleVolume(
        string sourcePath,
        GenericPartitionCandidate partition,
        string familyText,
        string description,
        IEnumerable<GenericFileSystemEntry>? rootEntries = null)
        : base(sourcePath, partition, familyText)
    {
        _description = description;
        if (rootEntries != null)
        {
            _root.AddRange(rootEntries);
        }
    }

    public override long ClusterSize => 0x1000;

    public override long UsedSpace => 0;

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        progress?.Report(0);
        return [];
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return Offset + cluster * ClusterSize;
    }

    public override void CopyFile(GenericFileSystemEntry entry, string destinationPath, Action<long>? progress, CancellationToken cancellationToken)
    {
        if (entry.Extents.Count == 0)
        {
            base.CopyFile(entry, destinationPath, progress, cancellationToken);
            return;
        }

        const int bufferSize = 0x100000;
        var buffer = new byte[bufferSize];
        var remaining = entry.Length;
        using var input = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.RandomAccess);
        using var output = File.Create(destinationPath);
        foreach (var extent in entry.Extents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            input.Position = extent.Offset;
            var readable = Math.Min(extent.Length, remaining);
            while (readable > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, readable));
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

    public override string ToString()
    {
        return $"{Name} ({_description})";
    }
}
