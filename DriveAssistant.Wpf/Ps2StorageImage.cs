using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FATXTools.Wpf;

internal sealed class Ps2StorageImage : IDisposable
{
    private const int SectorSize = 512;
    private const int HeaderSize = 1024;
    private const uint ApaMagic = 0x00415041;
    private const ushort ApaTypePfs = 0x0100;
    private const ushort ApaTypeHdl = 0x1337;

    private readonly FileStream _stream;

    private Ps2StorageImage(string sourcePath, FileStream stream, IReadOnlyList<PartitionModel> partitions)
    {
        SourcePath = sourcePath;
        _stream = stream;
        Partitions = partitions;
    }

    public string SourcePath { get; }

    public IReadOnlyList<PartitionModel> Partitions { get; }

    public static Ps2StorageImage Open(string sourcePath)
    {
        var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        try
        {
            var headers = ReadApaHeaders(stream);
            if (headers.Count == 0)
            {
                throw new InvalidDataException("No PlayStation 2 APA partition headers were found.");
            }

            var partitions = headers
                .OrderBy(header => header.StartSector)
                .Select(header =>
                {
                    var candidate = new GenericPartitionCandidate(
                        header.StartSector,
                        Guid.Empty,
                        header.StartSector * SectorSize,
                        header.LengthSectors * SectorSize,
                        header.Id,
                        SectorSize);
                    var family = header.Type switch
                    {
                        ApaTypePfs => "PlayStation 2 APA/PFS",
                        ApaTypeHdl => "PlayStation 2 HDLoader",
                        _ => "PlayStation 2 APA"
                    };
                    var description = header.Type switch
                    {
                        ApaTypePfs => "PFS partition detected; raw export and file carving are available.",
                        ApaTypeHdl => "HDLoader game partition detected; raw export and file carving are available.",
                        _ => $"APA partition type 0x{header.Type:X4}; raw export and file carving are available."
                    };
                    return new PartitionModel(new RawConsoleVolume(sourcePath, candidate, family, description), description);
                })
                .ToList();

            return new Ps2StorageImage(sourcePath, stream, partitions);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    private static List<Ps2ApaHeader> ReadApaHeaders(FileStream stream)
    {
        var headers = new List<Ps2ApaHeader>();
        var seen = new HashSet<uint>();
        uint sector = 0;
        for (var count = 0; count < 4096; count++)
        {
            if (!seen.Add(sector))
            {
                break;
            }

            if (!TryReadHeader(stream, sector, out var header))
            {
                break;
            }

            headers.Add(header);
            if (header.NextSector == 0 || header.NextSector == sector)
            {
                break;
            }

            sector = header.NextSector;
        }

        return headers;
    }

    private static bool TryReadHeader(FileStream stream, uint sector, out Ps2ApaHeader header)
    {
        header = default;
        var offset = sector * (long)SectorSize;
        if (offset < 0 || offset + HeaderSize > stream.Length)
        {
            return false;
        }

        var buffer = new byte[HeaderSize];
        stream.Position = offset;
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)) != ApaMagic)
        {
            return false;
        }

        var id = ReadNullTerminatedAscii(buffer.AsSpan(16, 32));
        if (string.IsNullOrWhiteSpace(id))
        {
            id = sector == 0 ? "__mbr" : $"apa_{sector:X8}";
        }

        var start = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(64));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(68));
        if (length == 0)
        {
            return false;
        }

        header = new Ps2ApaHeader(
            id,
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8)),
            start == 0 ? sector : start,
            length,
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(72)));
        return true;
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        if (end >= 0)
        {
            data = data[..end];
        }

        return Encoding.ASCII.GetString(data).Trim();
    }

    private readonly record struct Ps2ApaHeader(
        string Id,
        uint NextSector,
        uint StartSector,
        uint LengthSectors,
        ushort Type);
}
