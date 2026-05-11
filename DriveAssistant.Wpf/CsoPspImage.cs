using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace FATXTools.Wpf;

internal static class CsoPspImage
{
    private const uint PlainBlockFlag = 0x80000000;

    public static bool TryDecompressToTemporaryIso(string sourcePath, out string isoPath, out string status)
    {
        isoPath = string.Empty;
        status = string.Empty;
        if (!Path.GetExtension(sourcePath).Equals(".cso", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
            Span<byte> header = stackalloc byte[24];
            if (!GenericFileSystemImage.ReadExactly(input, 0, header) || !header[..4].SequenceEqual("CISO"u8))
            {
                return false;
            }

            var uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(header[0x08..]);
            var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[0x10..]);
            var version = header[0x14];
            var align = header[0x15];
            if (uncompressedSize == 0 || uncompressedSize > 4UL * 1024 * 1024 * 1024 || blockSize == 0 || blockSize > 1024 * 1024 || version > 2 || align > 31)
            {
                return false;
            }

            var blockCount = checked((int)((uncompressedSize + blockSize - 1) / blockSize));
            var index = new uint[blockCount + 1];
            var indexBytes = new byte[index.Length * 4];
            if (!GenericFileSystemImage.ReadExactly(input, 24, indexBytes))
            {
                return false;
            }

            for (var i = 0; i < index.Length; i++)
            {
                index[i] = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.AsSpan(i * 4));
            }

            isoPath = Path.Combine(Path.GetTempPath(), $"drive-assistant-cso-{Guid.NewGuid():N}.iso");
            using (var output = new FileStream(isoPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            {
                output.SetLength(checked((long)uncompressedSize));
                var buffer = new byte[blockSize];
                for (var block = 0; block < blockCount; block++)
                {
                    var outputLength = (int)Math.Min(blockSize, uncompressedSize - (ulong)block * blockSize);
                    var current = index[block];
                    var next = index[block + 1];
                    var plain = (current & PlainBlockFlag) != 0;
                    var offset = (long)(current & ~PlainBlockFlag) << align;
                    var nextOffset = (long)(next & ~PlainBlockFlag) << align;
                    var storedLength = checked((int)(nextOffset - offset));
                    if (offset < 0 || storedLength < 0 || offset + storedLength > input.Length)
                    {
                        throw new InvalidDataException("CSO block index points outside the file.");
                    }

                    input.Position = offset;
                    output.Position = checked((long)((ulong)block * blockSize));
                    if (plain)
                    {
                        CopyExactly(input, output, outputLength, buffer);
                    }
                    else
                    {
                        var compressed = new byte[storedLength];
                        if (input.Read(compressed, 0, compressed.Length) != compressed.Length)
                        {
                            throw new EndOfStreamException("Could not read CSO compressed block.");
                        }

                        var decoded = InflateBlock(compressed, outputLength);
                        output.Write(decoded, 0, outputLength);
                    }
                }
            }

            status = $"Native CSO decompression completed for '{Path.GetFileName(sourcePath)}' into a temporary PSP UMD ISO.";
            return true;
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(isoPath) && File.Exists(isoPath))
            {
                TryDelete(isoPath);
            }

            isoPath = string.Empty;
            return false;
        }
    }

    private static byte[] InflateBlock(byte[] compressed, int expectedLength)
    {
        foreach (var useZlib in new[] { true, false })
        {
            try
            {
                using var input = new MemoryStream(compressed);
                using Stream inflater = useZlib ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(expectedLength);
                inflater.CopyTo(output);
                var decoded = output.ToArray();
                if (decoded.Length >= expectedLength)
                {
                    return decoded.Length == expectedLength ? decoded : decoded[..expectedLength];
                }
            }
            catch (InvalidDataException)
            {
                // Try the alternate deflate wrapper below.
            }
        }

        throw new InvalidDataException("CSO block could not be inflated.");
    }

    private static void CopyExactly(Stream input, Stream output, int length, byte[] buffer)
    {
        var remaining = length;
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("Could not read CSO plain block.");
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
