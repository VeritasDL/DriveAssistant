using System.Buffers.Binary;
using System.IO;
using System.Linq;
using ZstdSharp;

namespace FATXTools.Wpf;

internal static class RvzGameCubeImage
{
    private const int Header1Size = 0x48;
    private const int Header2Size = 0xDC;
    private const int DiscHeaderOffset = 0x10;
    private const int RawDataEntrySize = 0x18;
    private const int RvzGroupEntrySize = 0x0C;
    private const int WiiSectorSize = 0x8000;
    private const int GameCubeMagicOffset = 0x1C;
    private const uint GameCubeMagic = 0xC2339F3D;
    private const uint RvzMagic = 0x52565A01;
    private const uint CompressionNone = 0;
    private const uint CompressionZstandard = 5;

    public static bool TryDecompressToTemporaryIso(string sourcePath, out string isoPath, out string status)
    {
        isoPath = string.Empty;
        status = string.Empty;

        if (!Path.GetExtension(sourcePath).Equals(".rvz", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
            if (!TryReadHeader(input, out var header))
            {
                return false;
            }

            if (header.DiscType != 1 || header.DiscHeader.Length < 0x80 || BinaryPrimitives.ReadUInt32BigEndian(header.DiscHeader.AsSpan(GameCubeMagicOffset)) != GameCubeMagic)
            {
                return false;
            }

            var rawDataEntries = ReadRawDataEntries(input, header);
            var groupEntries = ReadGroupEntries(input, header);
            if (rawDataEntries.Count == 0 || groupEntries.Count == 0)
            {
                return false;
            }

            isoPath = Path.Combine(Path.GetTempPath(), $"drive-assistant-rvz-{Guid.NewGuid():N}.iso");
            using (var output = new FileStream(isoPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            {
                output.SetLength(checked((long)header.IsoSize));
                output.Write(header.DiscHeader, 0, Math.Min(header.DiscHeader.Length, checked((int)Math.Min(header.IsoSize, (ulong)header.DiscHeader.Length))));
                DecompressRawData(input, output, header, rawDataEntries, groupEntries);
            }

            status = $"Native RVZ decompression completed for '{Path.GetFileName(sourcePath)}' into a temporary GameCube ISO.";
            return true;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(isoPath))
            {
                TryDelete(isoPath);
                isoPath = string.Empty;
            }

            status = ex.Message;
            return false;
        }
    }

    private static bool TryReadHeader(FileStream input, out RvzHeader header)
    {
        header = default;
        Span<byte> header1 = stackalloc byte[Header1Size];
        if (!GenericFileSystemImage.ReadExactly(input, 0, header1) || BinaryPrimitives.ReadUInt32BigEndian(header1) != RvzMagic)
        {
            return false;
        }

        var header2Size = BinaryPrimitives.ReadUInt32BigEndian(header1[0x0C..]);
        if (header2Size < Header2Size || header2Size > 1024 * 1024)
        {
            return false;
        }

        var header2 = new byte[header2Size];
        if (!GenericFileSystemImage.ReadExactly(input, Header1Size, header2))
        {
            return false;
        }

        var compression = BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0x04));
        if (compression is not CompressionNone and not CompressionZstandard)
        {
            return false;
        }

        var chunkSize = BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0x0C));
        if (chunkSize < WiiSectorSize || (chunkSize & (chunkSize - 1)) != 0)
        {
            return false;
        }

        header = new RvzHeader(
            IsoSize: BinaryPrimitives.ReadUInt64BigEndian(header1[0x24..]),
            RvzSize: BinaryPrimitives.ReadUInt64BigEndian(header1[0x2C..]),
            DiscType: BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0x00)),
            Compression: compression,
            ChunkSize: chunkSize,
            DiscHeader: header2.AsSpan(DiscHeaderOffset, 0x80).ToArray(),
            RawDataCount: BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0xB4)),
            RawDataOffset: BinaryPrimitives.ReadUInt64BigEndian(header2.AsSpan(0xB8)),
            RawDataSize: BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0xC0)),
            GroupCount: BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0xC4)),
            GroupOffset: BinaryPrimitives.ReadUInt64BigEndian(header2.AsSpan(0xC8)),
            GroupSize: BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0xD0)));

        return header.IsoSize > 0 && header.IsoSize <= 8UL * 1024 * 1024 * 1024 && header.RvzSize == (ulong)input.Length;
    }

    private static IReadOnlyList<RawDataEntry> ReadRawDataEntries(FileStream input, RvzHeader header)
    {
        var bytes = ReadCompressedData(input, header.RawDataOffset, header.RawDataSize, checked((int)(header.RawDataCount * RawDataEntrySize)), header.Compression);
        var entries = new List<RawDataEntry>(checked((int)header.RawDataCount));
        for (var offset = 0; offset + RawDataEntrySize <= bytes.Length; offset += RawDataEntrySize)
        {
            entries.Add(new RawDataEntry(
                DataOffset: BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset)),
                DataSize: BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 0x08)),
                GroupIndex: BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 0x10)),
                GroupCount: BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 0x14))));
        }

        return entries;
    }

    private static IReadOnlyList<GroupEntry> ReadGroupEntries(FileStream input, RvzHeader header)
    {
        var bytes = ReadCompressedData(input, header.GroupOffset, header.GroupSize, checked((int)(header.GroupCount * RvzGroupEntrySize)), header.Compression);
        var entries = new List<GroupEntry>(checked((int)header.GroupCount));
        for (var offset = 0; offset + RvzGroupEntrySize <= bytes.Length; offset += RvzGroupEntrySize)
        {
            var dataSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 0x04));
            entries.Add(new GroupEntry(
                DataOffset: (ulong)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)) << 2,
                DataSize: dataSize & 0x7FFFFFFF,
                IsCompressed: (dataSize & 0x80000000) != 0,
                RvzPackedSize: BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 0x08))));
        }

        return entries;
    }

    private static void DecompressRawData(FileStream input, FileStream output, RvzHeader header, IReadOnlyList<RawDataEntry> rawDataEntries, IReadOnlyList<GroupEntry> groupEntries)
    {
        foreach (var rawData in rawDataEntries.OrderBy(entry => entry.DataOffset))
        {
            var skipped = rawData.DataOffset % WiiSectorSize;
            var writeOffset = rawData.DataOffset - skipped;
            var totalSize = rawData.DataSize + skipped;
            for (ulong i = 0; i < rawData.GroupCount; i++)
            {
                var groupIndex = checked((int)(rawData.GroupIndex + i));
                if (groupIndex < 0 || groupIndex >= groupEntries.Count)
                {
                    throw new InvalidDataException("RVZ group index is outside the group table.");
                }

                var groupOffsetInData = i * header.ChunkSize;
                if (groupOffsetInData >= totalSize)
                {
                    break;
                }

                var unpackedSize = checked((int)Math.Min(header.ChunkSize, totalSize - groupOffsetInData));
                var group = groupEntries[groupIndex];
                var decoded = DecodeGroup(input, header, group, unpackedSize, writeOffset + groupOffsetInData);

                output.Position = checked((long)(writeOffset + groupOffsetInData));
                output.Write(decoded, 0, unpackedSize);
            }
        }
    }

    private static byte[] DecodeGroup(FileStream input, RvzHeader header, GroupEntry group, int unpackedSize, ulong groupDiscOffset)
    {
        if (group.DataSize == 0)
        {
            return new byte[unpackedSize];
        }

        var compressedBytes = new byte[group.DataSize];
        if (!GenericFileSystemImage.ReadExactly(input, checked((long)group.DataOffset), compressedBytes))
        {
            throw new EndOfStreamException("Could not read RVZ group data.");
        }

        byte[] packedBytes;
        if (group.IsCompressed)
        {
            if (header.Compression != CompressionZstandard)
            {
                throw new NotSupportedException("Only uncompressed and Zstandard RVZ groups are supported.");
            }

            using var decompressor = new Decompressor();
            packedBytes = decompressor.Unwrap(compressedBytes).ToArray();
        }
        else
        {
            packedBytes = compressedBytes;
        }

        if (group.RvzPackedSize == 0)
        {
            if (packedBytes.Length < unpackedSize)
            {
                throw new InvalidDataException("RVZ group decoded to fewer bytes than expected.");
            }

            return packedBytes;
        }

        return DecodeRvzPackedData(packedBytes, unpackedSize, groupDiscOffset);
    }

    private static byte[] ReadCompressedData(FileStream input, ulong offset, uint compressedSize, int decompressedSize, uint compression)
    {
        var bytes = new byte[compressedSize];
        if (!GenericFileSystemImage.ReadExactly(input, checked((long)offset), bytes))
        {
            throw new EndOfStreamException("Could not read RVZ table data.");
        }

        if (compression == CompressionNone)
        {
            if (bytes.Length < decompressedSize)
            {
                throw new InvalidDataException("RVZ table is smaller than expected.");
            }

            return bytes.Length == decompressedSize ? bytes : bytes.AsSpan(0, decompressedSize).ToArray();
        }

        using var decompressor = new Decompressor();
        var decompressed = decompressor.Unwrap(bytes).ToArray();
        if (decompressed.Length < decompressedSize)
        {
            throw new InvalidDataException("RVZ table decoded to fewer bytes than expected.");
        }

        return decompressed.Length == decompressedSize ? decompressed : decompressed.AsSpan(0, decompressedSize).ToArray();
    }

    private static byte[] DecodeRvzPackedData(byte[] packedBytes, int outputSize, ulong groupDiscOffset)
    {
        var output = new byte[outputSize];
        var inputOffset = 0;
        var outputOffset = 0;
        while (inputOffset + 4 <= packedBytes.Length && outputOffset < output.Length)
        {
            var sizeWord = BinaryPrimitives.ReadUInt32BigEndian(packedBytes.AsSpan(inputOffset));
            inputOffset += 4;
            var random = (sizeWord & 0x80000000) != 0;
            var size = checked((int)(sizeWord & 0x7FFFFFFF));
            if (size < 0 || outputOffset + size > output.Length)
            {
                throw new InvalidDataException("RVZ packed segment exceeds its output group.");
            }

            if (!random)
            {
                if (inputOffset + size > packedBytes.Length)
                {
                    throw new InvalidDataException("RVZ literal packed segment exceeds its input group.");
                }

                packedBytes.AsSpan(inputOffset, size).CopyTo(output.AsSpan(outputOffset));
                inputOffset += size;
            }
            else
            {
                if (inputOffset + RvzLaggedFibonacci.SeedSizeBytes > packedBytes.Length)
                {
                    throw new InvalidDataException("RVZ random packed segment is missing seed data.");
                }

                var generator = new RvzLaggedFibonacci(packedBytes.AsSpan(inputOffset, RvzLaggedFibonacci.SeedSizeBytes));
                inputOffset += RvzLaggedFibonacci.SeedSizeBytes;
                generator.Skip((int)((groupDiscOffset + (ulong)outputOffset) % WiiSectorSize));
                generator.GetBytes(output.AsSpan(outputOffset, size));
            }

            outputOffset += size;
        }

        if (outputOffset != output.Length)
        {
            throw new InvalidDataException("RVZ packed data ended before the output group was filled.");
        }

        return output;
    }

    private static void TryDelete(string path)
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

    private readonly record struct RvzHeader(
        ulong IsoSize,
        ulong RvzSize,
        uint DiscType,
        uint Compression,
        uint ChunkSize,
        byte[] DiscHeader,
        uint RawDataCount,
        ulong RawDataOffset,
        uint RawDataSize,
        uint GroupCount,
        ulong GroupOffset,
        uint GroupSize);

    private readonly record struct RawDataEntry(ulong DataOffset, ulong DataSize, uint GroupIndex, uint GroupCount);

    private readonly record struct GroupEntry(ulong DataOffset, uint DataSize, bool IsCompressed, uint RvzPackedSize);

    private sealed class RvzLaggedFibonacci
    {
        public const int SeedSizeBytes = 68;
        private const int SeedWords = 17;
        private const int BufferWords = 521;
        private const int Tap = 32;
        private readonly uint[] _buffer = new uint[BufferWords];
        private int _positionBytes;

        public RvzLaggedFibonacci(ReadOnlySpan<byte> seed)
        {
            for (var i = 0; i < SeedWords; i++)
            {
                _buffer[i] = BinaryPrimitives.ReadUInt32BigEndian(seed[(i * 4)..]);
            }

            for (var i = SeedWords; i < BufferWords; i++)
            {
                _buffer[i] = (_buffer[i - 17] << 23) ^ (_buffer[i - 16] >> 9) ^ _buffer[i - 1];
            }

            for (var i = 0; i < BufferWords; i++)
            {
                _buffer[i] = (_buffer[i] & 0xFF00FFFF) | ((_buffer[i] >> 2) & 0x00FF0000);
            }

            for (var i = 0; i < 4; i++)
            {
                Forward();
            }
        }

        public void Skip(int count)
        {
            _positionBytes += count;
            while (_positionBytes >= BufferWords * 4)
            {
                Forward();
                _positionBytes -= BufferWords * 4;
            }
        }

        public void GetBytes(Span<byte> destination)
        {
            var written = 0;
            while (written < destination.Length)
            {
                var wordIndex = _positionBytes / 4;
                var byteIndex = _positionBytes % 4;
                var value = _buffer[wordIndex];
                destination[written++] = byteIndex switch
                {
                    0 => (byte)(value >> 24),
                    1 => (byte)(value >> 16),
                    2 => (byte)(value >> 8),
                    _ => (byte)value
                };

                _positionBytes++;
                if (_positionBytes == BufferWords * 4)
                {
                    Forward();
                    _positionBytes = 0;
                }
            }
        }

        private void Forward()
        {
            for (var i = 0; i < Tap; i++)
            {
                _buffer[i] ^= _buffer[i + BufferWords - Tap];
            }

            for (var i = Tap; i < BufferWords; i++)
            {
                _buffer[i] ^= _buffer[i - Tap];
            }
        }
    }
}
