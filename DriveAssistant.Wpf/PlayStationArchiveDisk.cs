using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace FATXTools.Wpf;

internal sealed class PlayStationArchiveDisk : IDisposable
{
    private const int PageSize = 1024 * 1024;
    private const long MaxCachedBytes = 256L * 1024 * 1024;

    private readonly string _archivePath;
    private readonly Dictionary<long, CachePage> _pages = [];
    private readonly LinkedList<long> _lru = new();
    private readonly byte[] _scratch = new byte[PageSize];
    private ZipArchive? _archive;
    private Stream? _entryStream;
    private long _streamOffset;
    private long _cachedBytes;
    private bool _disposed;

    private PlayStationArchiveDisk(string archivePath, ZipArchive archive, ZipArchiveEntry entry)
    {
        _archivePath = archivePath;
        _archive = archive;
        EntryName = entry.FullName;
        Length = entry.Length;
        _entryStream = entry.Open();
    }

    public string EntryName { get; }

    public long Length { get; }

    public static bool IsSupported(string imagePath)
    {
        return Path.GetExtension(imagePath).Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    public static PlayStationArchiveDisk Open(string imagePath)
    {
        var archive = ZipFile.OpenRead(imagePath);
        var entry = FindImageEntry(archive)
            ?? throw new InvalidDataException("The ZIP archive does not contain a supported PlayStation raw image entry.");
        return new PlayStationArchiveDisk(imagePath, archive, entry);
    }

    public int Read(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || offset >= Length || count <= 0)
        {
            return 0;
        }

        var totalRead = 0;
        var remaining = (int)Math.Min(count, Length - offset);
        while (remaining > 0)
        {
            var pageIndex = offset / PageSize;
            var pageOffset = (int)(offset % PageSize);
            var page = EnsurePage(pageIndex);
            if (page.Length <= pageOffset)
            {
                break;
            }

            var copyLength = Math.Min(remaining, page.Length - pageOffset);
            Buffer.BlockCopy(page.Data, pageOffset, buffer, bufferOffset + totalRead, copyLength);
            totalRead += copyLength;
            remaining -= copyLength;
            offset += copyLength;
        }

        return totalRead;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _entryStream?.Dispose();
        _archive?.Dispose();
        _entryStream = null;
        _archive = null;
        _pages.Clear();
        _lru.Clear();
        _cachedBytes = 0;
    }

    private CachePage EnsurePage(long pageIndex)
    {
        if (_pages.TryGetValue(pageIndex, out var page))
        {
            Touch(pageIndex);
            return page;
        }

        var pageStart = checked(pageIndex * PageSize);
        if (pageStart < _streamOffset)
        {
            ResetStream();
        }

        SkipTo(pageStart);
        var data = new byte[(int)Math.Min(PageSize, Length - pageStart)];
        var read = ReadExactlyFromEntry(data);
        if (read < data.Length)
        {
            Array.Resize(ref data, read);
        }

        page = new CachePage(data);
        AddPage(pageIndex, page);
        return page;
    }

    private void SkipTo(long targetOffset)
    {
        while (_streamOffset < targetOffset)
        {
            var request = (int)Math.Min(_scratch.Length, targetOffset - _streamOffset);
            var read = _entryStream!.Read(_scratch, 0, request);
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of archived image while seeking to 0x{targetOffset:X} in {_archivePath}.");
            }

            _streamOffset += read;
        }
    }

    private int ReadExactlyFromEntry(byte[] data)
    {
        var totalRead = 0;
        while (totalRead < data.Length)
        {
            var read = _entryStream!.Read(data, totalRead, data.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
            _streamOffset += read;
        }

        return totalRead;
    }

    private void ResetStream()
    {
        _entryStream?.Dispose();
        _entryStream = null;
        _archive?.Dispose();
        _archive = ZipFile.OpenRead(_archivePath);
        var entry = _archive.GetEntry(EntryName)
            ?? throw new InvalidDataException($"The ZIP archive no longer contains {EntryName}.");
        _entryStream = entry.Open();
        _streamOffset = 0;
    }

    private void AddPage(long pageIndex, CachePage page)
    {
        _pages[pageIndex] = page;
        _lru.AddLast(pageIndex);
        _cachedBytes += page.Length;
        TrimCache();
    }

    private void Touch(long pageIndex)
    {
        var node = _lru.Find(pageIndex);
        if (node == null)
        {
            _lru.AddLast(pageIndex);
            return;
        }

        _lru.Remove(node);
        _lru.AddLast(node);
    }

    private void TrimCache()
    {
        while (_cachedBytes > MaxCachedBytes && _lru.First != null)
        {
            var pageIndex = _lru.First.Value;
            _lru.RemoveFirst();
            if (_pages.Remove(pageIndex, out var page))
            {
                _cachedBytes -= page.Length;
            }
        }
    }

    private static ZipArchiveEntry? FindImageEntry(ZipArchive archive)
    {
        foreach (var extension in new[] { ".img", ".bin", ".raw" })
        {
            foreach (var entry in archive.Entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Name) &&
                    entry.Length > 0 &&
                    Path.GetExtension(entry.Name).Equals(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }

        return archive.Entries.Count == 1 && archive.Entries[0].Length > 0
            ? archive.Entries[0]
            : null;
    }

    private sealed record CachePage(byte[] Data)
    {
        public int Length => Data.Length;
    }
}
