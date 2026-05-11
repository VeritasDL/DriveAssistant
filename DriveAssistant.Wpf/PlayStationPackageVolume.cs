using System.IO;
using System.IO.Compression;

namespace FATXTools.Wpf;

internal sealed class PlayStationPackageVolume : GenericFileSystemVolume
{
    private readonly List<GenericFileSystemEntry> _root = [];
    private readonly Dictionary<string, string> _zipEntryNames = new(StringComparer.OrdinalIgnoreCase);

    private PlayStationPackageVolume(string sourcePath, GenericPartitionCandidate partition, string familyText)
        : base(sourcePath, partition, familyText)
    {
    }

    public override long ClusterSize => 1;

    public override long UsedSpace => Walk(_root).Where(entry => !entry.IsDirectory).Sum(entry => entry.Length);

    public static bool TryOpen(string sourcePath, long length, out PlayStationPackageVolume volume, out string status)
    {
        volume = null!;
        status = string.Empty;
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".vpk" and not ".zip")
        {
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(sourcePath);
            var entries = archive.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.FullName))
                .ToList();
            var hasVitaMetadata = entries.Any(entry => NormalizeZipPath(entry.FullName).Equals("sce_sys/param.sfo", StringComparison.OrdinalIgnoreCase));
            var hasPspMetadata = entries.Any(entry => NormalizeZipPath(entry.FullName).Equals("eboot.pbp", StringComparison.OrdinalIgnoreCase))
                                 || entries.Any(entry => NormalizeZipPath(entry.FullName).StartsWith("psp_game/", StringComparison.OrdinalIgnoreCase));
            if (!hasVitaMetadata && !hasPspMetadata)
            {
                return false;
            }

            var family = hasVitaMetadata ? "Sony PS Vita VPK/package" : "Sony PSP homebrew/package";
            var candidate = new GenericPartitionCandidate(0, Guid.Empty, 0, length, family, 1);
            volume = new PlayStationPackageVolume(sourcePath, candidate, family);
            volume.Load(entries);
            status = hasVitaMetadata
                ? $"Mounted PS Vita VPK/ZIP package, {volume._zipEntryNames.Count:N0} file entries. Package browsing and export are available; encrypted installed PFS content still needs matching keys/tools."
                : $"Mounted PSP homebrew/ZIP package, {volume._zipEntryNames.Count:N0} file entries. Package browsing and export are available.";
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public override IReadOnlyList<GenericFileSystemEntry> GetRoot() => _root;

    public override IReadOnlyList<GenericFileSystemEntry> ScanDeleted(CancellationToken cancellationToken, IProgress<int>? progress)
    {
        progress?.Report(100);
        return [];
    }

    public override void CopyFile(GenericFileSystemEntry entry, string destinationPath, Action<long>? progress, CancellationToken cancellationToken)
    {
        if (entry.IsDirectory)
        {
            throw new InvalidOperationException("Select a file, not a directory.");
        }

        if (!_zipEntryNames.TryGetValue(entry.Path, out var zipEntryName))
        {
            base.CopyFile(entry, destinationPath, progress, cancellationToken);
            return;
        }

        using var archive = ZipFile.OpenRead(SourcePath);
        var zipEntry = archive.GetEntry(zipEntryName) ?? throw new FileNotFoundException("Package entry was not found.", zipEntryName);
        using var input = zipEntry.Open();
        using var output = File.Create(destinationPath);
        var buffer = new byte[0x100000];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            progress?.Invoke(read);
        }
    }

    protected override long ClusterToOffset(uint cluster)
    {
        return cluster;
    }

    private void Load(IEnumerable<ZipArchiveEntry> zipEntries)
    {
        var directories = new Dictionary<string, GenericFileSystemEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var zipEntry in zipEntries.OrderBy(entry => NormalizeZipPath(entry.FullName), StringComparer.OrdinalIgnoreCase))
        {
            var normalizedPath = NormalizeZipPath(zipEntry.FullName).Trim('/');
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                continue;
            }

            var isDirectory = normalizedPath.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(zipEntry.Name);
            var pathParts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parentChildren = _root;
            var parentPath = "/";
            for (var index = 0; index < pathParts.Length - (isDirectory ? 0 : 1); index++)
            {
                var name = pathParts[index];
                var directoryPath = CombinePath(parentPath, name);
                if (!directories.TryGetValue(directoryPath, out var directory))
                {
                    directory = CreateDirectoryEntry(directoryPath, name);
                    directories[directoryPath] = directory;
                    parentChildren.Add(directory);
                }

                parentChildren = directory.Children;
                parentPath = directoryPath;
            }

            if (isDirectory)
            {
                continue;
            }

            var fileName = pathParts[^1];
            var filePath = CombinePath(parentPath, fileName);
            var file = new GenericFileSystemEntry
            {
                Volume = this,
                Path = filePath,
                Name = fileName,
                Kind = DetectKind(fileName, normalizedPath),
                IsDirectory = false,
                Length = zipEntry.Length,
                Offset = zipEntry.CompressedLength,
                Cluster = 0,
                Attributes = "ZIP package entry",
                MetadataStatus = "Active PlayStation package entry",
                Extents = []
            };
            parentChildren.Add(file);
            _zipEntryNames[file.Path] = zipEntry.FullName;
        }
    }

    private GenericFileSystemEntry CreateDirectoryEntry(string path, string name)
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
            Attributes = "ZIP package directory",
            MetadataStatus = "Active PlayStation package directory",
            Extents = []
        };
    }

    private static string DetectKind(string name, string path)
    {
        if (path.Equals("sce_sys/param.sfo", StringComparison.OrdinalIgnoreCase))
        {
            return "PS Vita Param SFO";
        }

        if (name.Equals("EBOOT.PBP", StringComparison.OrdinalIgnoreCase))
        {
            return "PSP EBOOT";
        }

        return "File";
    }

    private static string NormalizeZipPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
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
}
