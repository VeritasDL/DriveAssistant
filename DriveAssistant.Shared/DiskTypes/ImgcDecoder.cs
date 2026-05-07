using FATXTools.Utilities;
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FATXTools.DiskTypes
{
    internal static class ImgcDecoder
    {
        private const int HeaderSize = 0x1000;
        private const int BlockHeaderSize = 8;

        public static string DecodeToTempRawImage(string imgcPath)
        {
            if (!File.Exists(imgcPath))
            {
                throw new FileNotFoundException("Compressed image file was not found.", imgcPath);
            }

            using (var stream = File.OpenRead(imgcPath))
            using (var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true))
            {
                var header = reader.ReadBytes(HeaderSize);
                if (header.Length < HeaderSize)
                {
                    throw new InvalidDataException("Invalid IMGC image: header is too small.");
                }

                // Validate by peeking first block type.
                var firstBlockHeader = reader.ReadBytes(BlockHeaderSize);
                if (firstBlockHeader.Length < BlockHeaderSize)
                {
                    throw new InvalidDataException("Invalid IMGC image: no blocks found.");
                }

                var firstTag = Encoding.ASCII.GetString(firstBlockHeader, 0, 4);
                if (firstTag != "lol!" && firstTag != "omg!")
                {
                    throw new InvalidDataException("Invalid IMGC image: unsupported block type.");
                }

                stream.Position = 0;

                var cacheFile = Path.Combine(
                    Path.GetTempPath(),
                    $"fatxtools_{Path.GetFileNameWithoutExtension(imgcPath)}_{File.GetLastWriteTimeUtc(imgcPath).Ticks}.img");

                if (File.Exists(cacheFile))
                {
                    return cacheFile;
                }

                var tempFile = cacheFile + ".tmp";
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }

                DecodeToFile(stream, tempFile);
                if (File.Exists(cacheFile))
                {
                    File.Delete(cacheFile);
                }
                File.Move(tempFile, cacheFile);
                AppLogger.WriteLine($"IMGC decompression completed: {imgcPath} -> {cacheFile}");
                return cacheFile;
            }
        }

        private static void DecodeToFile(Stream input, string outputPath)
        {
            using (var reader = new BinaryReader(input, Encoding.ASCII, leaveOpen: true))
            using (var output = File.Create(outputPath))
            {
                TryMarkSparse(output);

                var header = reader.ReadBytes(HeaderSize);
                if (header.Length < HeaderSize)
                {
                    throw new InvalidDataException("Invalid IMGC image header.");
                }

                while (input.Position < input.Length)
                {
                    var blockStart = input.Position;
                    var blockHeader = reader.ReadBytes(BlockHeaderSize);
                    if (blockHeader.Length == 0)
                    {
                        break;
                    }
                    if (blockHeader.Length < BlockHeaderSize)
                    {
                        throw new InvalidDataException("Invalid IMGC block header.");
                    }

                    var tag = Encoding.ASCII.GetString(blockHeader, 0, 4);
                    var size = BitConverter.ToUInt32(blockHeader, 4);
                    var payloadSize = GetPayloadSize(input, blockStart, size);

                    if (tag == "omg!")
                    {
                        if (payloadSize < 8)
                        {
                            throw new InvalidDataException("Invalid IMGC zero block.");
                        }

                        var zeroCountRaw = reader.ReadBytes((int)payloadSize);
                        if (zeroCountRaw.Length < 8)
                        {
                            throw new InvalidDataException("Invalid IMGC zero block data.");
                        }

                        var zeroCount = BitConverter.ToUInt64(zeroCountRaw, 0);
                        if (zeroCount > long.MaxValue)
                        {
                            throw new InvalidDataException("Invalid IMGC zero block length.");
                        }

                        output.Seek((long)zeroCount, SeekOrigin.Current);
                    }
                    else if (tag == "lol!")
                    {
                        var compressed = reader.ReadBytes((int)payloadSize);
                        if (compressed.Length != payloadSize)
                        {
                            throw new InvalidDataException("Invalid IMGC compressed block data.");
                        }

                        var decompressed = ImgcLzo.DecompressBlock(compressed);
                        output.Write(decompressed, 0, decompressed.Length);
                    }
                    else
                    {
                        throw new InvalidDataException($"Unsupported IMGC block type: {tag}");
                    }
                }

                output.SetLength(output.Position);
            }
        }

        private static uint GetPayloadSize(Stream input, long blockStart, uint declaredSize)
        {
            if (declaredSize < BlockHeaderSize)
            {
                return declaredSize;
            }

            var payloadSizedNext = blockStart + BlockHeaderSize + declaredSize;
            if (IsBlockBoundary(input, payloadSizedNext))
            {
                return declaredSize;
            }

            var totalSizedNext = blockStart + declaredSize;
            if (IsBlockBoundary(input, totalSizedNext))
            {
                return declaredSize - BlockHeaderSize;
            }

            return declaredSize;
        }

        private static bool IsBlockBoundary(Stream input, long position)
        {
            if (position == input.Length)
            {
                return true;
            }

            if (position < HeaderSize || position + 4 > input.Length)
            {
                return false;
            }

            var current = input.Position;
            try
            {
                var tag = new byte[4];
                input.Position = position;
                var read = input.Read(tag, 0, tag.Length);
                if (read != tag.Length)
                {
                    return false;
                }

                var text = Encoding.ASCII.GetString(tag);
                return text == "lol!" || text == "omg!";
            }
            finally
            {
                input.Position = current;
            }
        }

        private static void TryMarkSparse(FileStream output)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            try
            {
                DeviceIoControl(
                    output.SafeFileHandle,
                    FsctlSetSparse,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero);
            }
            catch
            {
                // Sparse marking is an optimization. If it is unavailable, normal decoding still works for small images.
            }
        }

        private const uint FsctlSetSparse = 0x000900C4;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        // LZO decoder adapted for IMGC custom stream format.
        private static class ImgcLzo
        {
            private static uint Bits(uint value, int position, int count)
            {
                return (value >> position) & ((1u << count) - 1u);
            }

            private static uint Bit(uint value, int position) => Bits(value, position, 1);

            private static int ParseLength(byte[] input, ref int position, int bits)
            {
                int mask = (1 << bits) - 1;
                int len = input[position++] & mask;
                if (len == 0)
                {
                    while (position < input.Length && input[position] == 0)
                    {
                        len += 0xFF;
                        position++;
                    }

                    if (position >= input.Length)
                    {
                        return len + mask;
                    }

                    len += input[position++] + mask;
                }

                return len;
            }

            public static byte[] DecompressBlock(byte[] block)
            {
                if (block.Length < 2)
                {
                    return Array.Empty<byte>();
                }

                int pos = 0;
                uint size = BitConverter.ToUInt16(block, pos);
                pos += 2;

                if ((size & 0x8000) != 0)
                {
                    if (block.Length < 4)
                    {
                        throw new InvalidDataException("Invalid IMGC compressed block length.");
                    }

                    size &= 0x7FFF;
                    size |= (uint)BitConverter.ToUInt16(block, pos) << 15;
                    pos += 2;
                }

                var output = new byte[size];
                Decompress(block, pos, output);
                return output;
            }

            private static void Decompress(byte[] input, int inputOffset, byte[] output)
            {
                int p = inputOffset;
                int op = 0;
                byte state = 0;

                while (p < input.Length && op < output.Length)
                {
                    byte instr = input[p];

                    if (p == inputOffset && instr > 17)
                    {
                        int copy = Math.Min(instr - 17, output.Length - op);
                        Buffer.BlockCopy(input, p + 1, output, op, copy);
                        p += 1 + copy;
                        op += copy;
                        state = 4;
                        continue;
                    }

                    uint length;
                    ushort follow;
                    int distance;

                    if (instr >= 64)
                    {
                        p++;
                        length = Bits(instr, 5, 3) + 1;
                        follow = input[p++];
                        distance = (int)((follow << 3) + Bits(instr, 2, 3) + 1);
                        state = (byte)Bits(instr, 0, 2);
                    }
                    else if (instr >= 32)
                    {
                        length = (uint)(ParseLength(input, ref p, 5) + 2);
                        follow = BitConverter.ToUInt16(input, p);
                        distance = (follow >> 2) + 1;
                        state = (byte)Bits(follow, 0, 2);
                        p += 2;
                    }
                    else if (instr >= 16)
                    {
                        length = (uint)(ParseLength(input, ref p, 3) + 2);
                        follow = BitConverter.ToUInt16(input, p);
                        distance = (int)((Bit(instr, 3) << 14) + (follow >> 2) + 0x4000);
                        state = (byte)Bits(follow, 0, 2);
                        p += 2;
                    }
                    else if (state == 0)
                    {
                        length = (uint)(ParseLength(input, ref p, 4) + 3);
                        int copy = Math.Min((int)length, Math.Min(input.Length - p, output.Length - op));
                        Buffer.BlockCopy(input, p, output, op, copy);
                        p += copy;
                        op += copy;
                        state = 4;
                        continue;
                    }
                    else
                    {
                        throw new InvalidDataException("Invalid IMGC/LZO instruction stream.");
                    }

                    int backCopyLength = Math.Min((int)length, output.Length - op);
                    int copyFrom = op - distance;
                    for (int i = 0; i < backCopyLength; i++)
                    {
                        output[op++] = output[copyFrom + i];
                    }

                    if (state > 0 && state < 4)
                    {
                        int tail = Math.Min(state, output.Length - op);
                        Buffer.BlockCopy(input, p, output, op, tail);
                        p += tail;
                        op += tail;
                    }
                }
            }
        }
    }
}
