using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FATXTools.Wpf;

internal static class NintendoNandCrypto
{
    private const int SectorSize = 512;
    private const int AesBlockSize = 16;
    private const ulong DsiFooterSizeFlag = 0x40;
    private const long DsiMinimumNandSize = 0x0F000000;
    private const ulong Nintendo3dsNandMediaUnit = 0x200;
    private const int KeyslotTwlNand = 0x03;
    private const int KeyslotCtrNandOld = 0x04;
    private const int KeyslotCtrNandNew = 0x05;
    private static readonly byte[] DsiNoCashFooterMagic = Encoding.ASCII.GetBytes("DSi eMMC CID/CPU");
    private static readonly UInt128 DsiRetailTwlKeyY = ParseUInt128("E1A00005202DDD1DBD4DC4D30AB9DC76");
    private static readonly UInt128 DsiDevTwlKeyY = ParseUInt128("E1A00005266A649766E8B87AF176BFAA");
    private static readonly UInt128 CtrNandNewKeyY = ParseUInt128("4D804F4E9990194613A204AC584460BE");
    private static readonly UInt128 TwlScramblerConstant = ParseUInt128("FFFEFB4E295902582A680F5F1A4F3E79");
    private static readonly UInt128 CtrScramblerConstant = ParseUInt128("1FF9E9AAC5FE0408024591DC5D52768A");
    private static readonly byte[] OtpMagic = [0x0F, 0xB0, 0xAD, 0xDE];

    public static bool TryOpenDsiNand(string sourcePath, List<PartitionModel> partitions, List<string> temporaryPaths, out string status)
    {
        status = string.Empty;
        try
        {
            var length = new FileInfo(sourcePath).Length;
            if (length < DsiMinimumNandSize || ((ulong)length & DsiFooterSizeFlag) != DsiFooterSizeFlag)
            {
                status = "DSi NAND image does not have a No$GBA footer-sized length.";
                return false;
            }

            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
            var footer = new byte[0x40];
            if (!GenericFileSystemImage.ReadExactly(stream, length - footer.Length, footer)
                || !footer.AsSpan(0, DsiNoCashFooterMagic.Length).SequenceEqual(DsiNoCashFooterMagic))
            {
                status = "DSi No$GBA footer magic was not found.";
                return false;
            }

            var cid = footer.AsSpan(0x10, 0x10).ToArray();
            var consoleId = footer.AsSpan(0x20, 0x08).ToArray();
            Array.Reverse(consoleId);

            if (!TryCreateDsiKey(consoleId, DsiRetailTwlKeyY, out var key)
                || !TryReadDsiMbr(stream, key, cid, out var baseCounter, out var partitionRows))
            {
                if (!TryCreateDsiKey(consoleId, DsiDevTwlKeyY, out key)
                    || !TryReadDsiMbr(stream, key, cid, out baseCounter, out partitionRows))
                {
                    status = "DSi No$GBA footer was read, but decrypted MBR validation failed.";
                    return false;
                }
            }

            var mounted = 0;
            for (var index = 0; index < Math.Min(2, partitionRows.Count); index++)
            {
                var row = partitionRows[index];
                if (row.Offset <= 0 || row.Length <= 0 || row.Offset + row.Length > length)
                {
                    continue;
                }

                var tempPath = CreateTemporaryPath("dsi", ".img");
                DecryptToFile(sourcePath, tempPath, row.Offset, row.Length, key, baseCounter, twlMode: true);
                temporaryPaths.Add(tempPath);

                if (TryOpenFatPartition(tempPath, index == 0 ? "DSi main FAT" : "DSi photo FAT", "Nintendo DSi NAND FAT", out var partition))
                {
                    partitions.Add(partition);
                    mounted++;
                }
            }

            if (mounted == 0)
            {
                status = "DSi partitions decrypted, but no FAT16/FAT32 volume mounted.";
                return false;
            }

            status = $"Mounted {mounted:N0} decrypted DSi NAND FAT partition(s) from No$GBA footer key material.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or OverflowException or ArgumentException)
        {
            status = $"DSi NAND keyed mount failed: {ex.Message}";
            return false;
        }
    }

    public static bool TryOpen3dsNand(string sourcePath, string? keyPath, List<PartitionModel> partitions, List<string> temporaryPaths, out string status)
    {
        status = string.Empty;
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
            Span<byte> header = stackalloc byte[0x200];
            if (!GenericFileSystemImage.ReadExactly(stream, 0, header) || !header.Slice(0x100, 4).SequenceEqual("NCSD"u8))
            {
                return false;
            }

            if (!Nintendo3dsKeyMaterial.TryLoad(keyPath, dev: false, out var material, out status)
                && !Nintendo3dsKeyMaterial.TryLoad(keyPath, dev: true, out material, out status))
            {
                return false;
            }

            var table = ReadNcsdPartitions(header);
            var mounted = 0;
            foreach (var row in table)
            {
                if (row.Offset <= 0 || row.Length <= 0 || row.Offset + row.Length > stream.Length)
                {
                    continue;
                }

                if (row.FsType == 1 && row.CryptType is 2 or 3)
                {
                    var keyslot = row.CryptType == 2 ? KeyslotCtrNandOld : KeyslotCtrNandNew;
                    if (!material.NormalKeys.TryGetValue(keyslot, out var key))
                    {
                        continue;
                    }

                    var counter = ReadUInt128Big(SHA256.HashData(material.Cid).AsSpan(0, 0x10));
                    var mbr = DecryptRange(stream, row.Offset, 0x200, key, counter, twlMode: false);
                    var inner = ReadMbrPartitions(mbr.AsSpan(0x1BE, 0x42)).FirstOrDefault(part => part.Offset > 0 && part.Length > 0);
                    if (inner.Length <= 0)
                    {
                        continue;
                    }

                    var tempPath = CreateTemporaryPath("3ds-ctr", ".img");
                    DecryptToFile(sourcePath, tempPath, row.Offset + inner.Offset, inner.Length, key, counter, twlMode: false);
                    temporaryPaths.Add(tempPath);
                    if (TryOpenFatPartition(tempPath, "3DS CTRNAND FAT", "Nintendo 3DS CTRNAND FAT", out var partition))
                    {
                        partitions.Add(partition);
                        mounted++;
                    }
                }
                else if (row.FsType == 1 && row.CryptType == 1 && material.NormalKeys.TryGetValue(KeyslotTwlNand, out var twlKey))
                {
                    var counter = ReadUInt128Little(SHA1.HashData(material.Cid).AsSpan(0, 0x10));
                    var mbr = DecryptRange(stream, row.Offset, 0x200, twlKey, counter, twlMode: true);
                    var innerRows = ReadMbrPartitions(mbr.AsSpan(0x1BE, 0x42));
                    for (var innerIndex = 0; innerIndex < Math.Min(2, innerRows.Count); innerIndex++)
                    {
                        var inner = innerRows[innerIndex];
                        if (inner.Offset <= 0 || inner.Length <= 0)
                        {
                            continue;
                        }

                        var tempPath = CreateTemporaryPath("3ds-twl", ".img");
                        DecryptToFile(sourcePath, tempPath, row.Offset + inner.Offset, inner.Length, twlKey, counter, twlMode: true);
                        temporaryPaths.Add(tempPath);
                        if (TryOpenFatPartition(tempPath, innerIndex == 0 ? "3DS TWLNAND FAT" : "3DS TWLPHOTO FAT", "Nintendo 3DS TWL FAT", out var partition))
                        {
                            partitions.Add(partition);
                            mounted++;
                        }
                    }
                }
            }

            if (mounted == 0)
            {
                status = "3DS key material loaded, but no decrypted FAT partition mounted.";
                return false;
            }

            status = $"Mounted {mounted:N0} decrypted 3DS NAND FAT partition(s) with local boot9/OTP/CID key material.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or OverflowException or ArgumentException)
        {
            status = $"3DS NAND keyed mount failed: {ex.Message}";
            return false;
        }
    }

    private static bool TryCreateDsiKey(byte[] consoleId, UInt128 keyY, out byte[] normalKey)
    {
        normalKey = [];
        if (consoleId.Length < 8)
        {
            return false;
        }

        var first = BinaryPrimitives.ReadUInt32BigEndian(consoleId.AsSpan(4, 4));
        var second = BinaryPrimitives.ReadUInt32BigEndian(consoleId.AsSpan(0, 4));
        Span<byte> keyXBytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(keyXBytes[0..4], first);
        BinaryPrimitives.WriteUInt32LittleEndian(keyXBytes[4..8], first ^ 0x24EE6906);
        BinaryPrimitives.WriteUInt32LittleEndian(keyXBytes[8..12], second ^ 0xE65B601D);
        BinaryPrimitives.WriteUInt32LittleEndian(keyXBytes[12..16], second);
        var keyX = ReadUInt128Little(keyXBytes);
        normalKey = KeygenTwl(keyX, keyY);
        return true;
    }

    private static bool TryReadDsiMbr(FileStream stream, byte[] key, byte[] cid, out UInt128 counter, out IReadOnlyList<(long Offset, long Length)> partitions)
    {
        counter = ReadUInt128Little(SHA1.HashData(cid).AsSpan(0, 0x10));
        partitions = [];
        var header = DecryptRange(stream, 0, SectorSize, key, counter, twlMode: true);
        var mbr = header.AsSpan(0x1BE, 0x42);
        if (mbr[0x40] != 0x55 || mbr[0x41] != 0xAA)
        {
            return false;
        }

        partitions = ReadMbrPartitions(mbr);
        return true;
    }

    private static List<NcsdPartitionRow> ReadNcsdPartitions(ReadOnlySpan<byte> header)
    {
        var rows = new List<NcsdPartitionRow>();
        for (var index = 0; index < 8; index++)
        {
            var entryOffset = 0x120 + index * 8;
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(header[entryOffset..]) * (long)Nintendo3dsNandMediaUnit;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header[(entryOffset + 4)..]) * (long)Nintendo3dsNandMediaUnit;
            rows.Add(new NcsdPartitionRow(header[0x110 + index], header[0x118 + index], offset, length));
        }

        return rows;
    }

    private static IReadOnlyList<(long Offset, long Length)> ReadMbrPartitions(ReadOnlySpan<byte> mbr)
    {
        var rows = new List<(long Offset, long Length)>();
        if (mbr.Length < 0x42 || mbr[0x40] != 0x55 || mbr[0x41] != 0xAA)
        {
            return rows;
        }

        for (var index = 0; index < 4; index++)
        {
            var entry = mbr.Slice(index * 0x10, 0x10);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]) * (long)SectorSize;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]) * (long)SectorSize;
            rows.Add((offset, length));
        }

        return rows;
    }

    private static bool TryOpenFatPartition(string tempPath, string name, string family, out PartitionModel partition)
    {
        partition = null!;
        Span<byte> boot = stackalloc byte[SectorSize];
        using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess))
        {
            if (!GenericFileSystemImage.ReadExactly(stream, 0, boot) || boot[510] != 0x55 || boot[511] != 0xAA)
            {
                return false;
            }
        }

        var candidate = new GenericPartitionCandidate(0, Guid.Empty, 0, new FileInfo(tempPath).Length, name, SectorSize);
        GenericFileSystemVolume volume;
        var fat32 = Encoding.ASCII.GetString(boot.Slice(82, 8));
        var fat16 = Encoding.ASCII.GetString(boot.Slice(54, 8));
        if (fat32 == "FAT32   ")
        {
            volume = Fat32Volume.Open(tempPath, candidate);
        }
        else if (fat16 == "FAT16   " || LooksLikeFat16Bpb(boot))
        {
            volume = Fat16Volume.Open(tempPath, candidate);
        }
        else
        {
            return false;
        }

        partition = new PartitionModel(volume, $"Mounted decrypted {family}, {volume.GetRoot().Count:N0} root entries. Metadata scan, deleted FAT entries, carving, and export are available.");
        return true;
    }

    private static bool LooksLikeFat16Bpb(ReadOnlySpan<byte> boot)
    {
        if (boot.Length < SectorSize || boot[510] != 0x55 || boot[511] != 0xAA)
        {
            return false;
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
        var sectorsPerCluster = boot[13];
        var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..]);
        var fatCount = boot[16];
        var rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..]);
        var sectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(boot[22..]);
        return bytesPerSector is 512 or 1024 or 2048 or 4096
               && sectorsPerCluster > 0
               && (sectorsPerCluster & (sectorsPerCluster - 1)) == 0
               && reservedSectors > 0
               && fatCount is 1 or 2
               && rootEntryCount > 0
               && sectorsPerFat > 0;
    }

    private static byte[] DecryptRange(FileStream stream, long offset, int length, byte[] key, UInt128 baseCounter, bool twlMode)
    {
        var data = new byte[length];
        if (!GenericFileSystemImage.ReadExactly(stream, offset, data))
        {
            throw new EndOfStreamException("Could not read encrypted NAND range.");
        }

        DecryptInPlace(data, offset, key, baseCounter, twlMode);
        return data;
    }

    private static void DecryptToFile(string sourcePath, string outputPath, long sourceOffset, long length, byte[] key, UInt128 baseCounter, bool twlMode)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = new byte[bufferSize];
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.RandomAccess);
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize, FileOptions.SequentialScan);
        for (long copied = 0; copied < length;)
        {
            var read = (int)Math.Min(buffer.Length, length - copied);
            if (!GenericFileSystemImage.ReadExactly(input, sourceOffset + copied, buffer.AsSpan(0, read)))
            {
                throw new EndOfStreamException("Could not read encrypted NAND partition.");
            }

            DecryptInPlace(buffer.AsSpan(0, read), sourceOffset + copied, key, baseCounter, twlMode);
            output.Write(buffer, 0, read);
            copied += read;
        }
    }

    private static void DecryptInPlace(Span<byte> data, long sourceOffset, byte[] key, UInt128 baseCounter, bool twlMode)
    {
        if (sourceOffset % AesBlockSize != 0 || data.Length % AesBlockSize != 0)
        {
            var before = (int)(sourceOffset % AesBlockSize);
            var alignedOffset = sourceOffset - before;
            var paddedLength = ((before + data.Length + AesBlockSize - 1) / AesBlockSize) * AesBlockSize;
            var padded = new byte[paddedLength];
            data.CopyTo(padded.AsSpan(before));
            DecryptAlignedInPlace(padded, alignedOffset, key, baseCounter, twlMode);
            padded.AsSpan(before, data.Length).CopyTo(data);
            return;
        }

        DecryptAlignedInPlace(data, sourceOffset, key, baseCounter, twlMode);
    }

    private static void DecryptAlignedInPlace(Span<byte> data, long sourceOffset, byte[] key, UInt128 baseCounter, bool twlMode)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(key, null);
        Span<byte> counterBlock = stackalloc byte[AesBlockSize];
        Span<byte> block = stackalloc byte[AesBlockSize];
        var counter = baseCounter + (UInt128)((ulong)sourceOffset / AesBlockSize);
        for (var offset = 0; offset < data.Length; offset += AesBlockSize)
        {
            WriteUInt128Big(counterBlock, counter);
            var keyStreamArray = EncryptCounter(encryptor, counterBlock);
            if (twlMode)
            {
                for (var i = 0; i < AesBlockSize; i++)
                {
                    block[i] = data[offset + AesBlockSize - 1 - i];
                }

                for (var i = 0; i < AesBlockSize; i++)
                {
                    block[i] ^= keyStreamArray[i];
                }

                for (var i = 0; i < AesBlockSize; i++)
                {
                    data[offset + i] = block[AesBlockSize - 1 - i];
                }
            }
            else
            {
                for (var i = 0; i < AesBlockSize; i++)
                {
                    data[offset + i] ^= keyStreamArray[i];
                }
            }

            counter++;
        }
    }

    private static byte[] EncryptCounter(ICryptoTransform encryptor, ReadOnlySpan<byte> counterBlock)
    {
        var input = counterBlock.ToArray();
        var output = new byte[AesBlockSize];
        encryptor.TransformBlock(input, 0, AesBlockSize, output, 0);
        return output;
    }

    private static byte[] KeygenTwl(UInt128 keyX, UInt128 keyY)
    {
        return WriteUInt128Big(Rol((keyX ^ keyY) + TwlScramblerConstant, 42));
    }

    private static byte[] KeygenCtr(UInt128 keyX, UInt128 keyY)
    {
        return WriteUInt128Big(Rol((Rol(keyX, 2) ^ keyY) + CtrScramblerConstant, 87));
    }

    private static UInt128 Rol(UInt128 value, int bits)
    {
        bits &= 127;
        return bits == 0 ? value : (value << bits) | (value >> (128 - bits));
    }

    private static UInt128 ReadUInt128Big(ReadOnlySpan<byte> data)
    {
        var high = BinaryPrimitives.ReadUInt64BigEndian(data[..8]);
        var low = BinaryPrimitives.ReadUInt64BigEndian(data[8..16]);
        return ((UInt128)high << 64) | low;
    }

    private static UInt128 ReadUInt128Little(ReadOnlySpan<byte> data)
    {
        var low = BinaryPrimitives.ReadUInt64LittleEndian(data[..8]);
        var high = BinaryPrimitives.ReadUInt64LittleEndian(data[8..16]);
        return ((UInt128)high << 64) | low;
    }

    private static byte[] WriteUInt128Big(UInt128 value)
    {
        var buffer = new byte[16];
        WriteUInt128Big(buffer, value);
        return buffer;
    }

    private static void WriteUInt128Big(Span<byte> destination, UInt128 value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination[..8], (ulong)(value >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..16], (ulong)value);
    }

    private static UInt128 ParseUInt128(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        return ReadUInt128Big(bytes);
    }

    private static string CreateTemporaryPath(string prefix, string extension)
    {
        return Path.Combine(Path.GetTempPath(), $"drive-assistant-{prefix}-{Guid.NewGuid():N}{extension}");
    }

    private sealed record NcsdPartitionRow(int FsType, int CryptType, long Offset, long Length);

    private sealed class Nintendo3dsKeyMaterial
    {
        private Nintendo3dsKeyMaterial(byte[] cid, Dictionary<int, byte[]> normalKeys)
        {
            Cid = cid;
            NormalKeys = normalKeys;
        }

        public byte[] Cid { get; }

        public Dictionary<int, byte[]> NormalKeys { get; }

        public static bool TryLoad(string keyPath, bool dev, out Nintendo3dsKeyMaterial material, out string status)
        {
            material = null!;
            status = string.Empty;
            var baseDirectory = File.Exists(keyPath) ? Path.GetDirectoryName(keyPath) ?? "." : keyPath;
            var boot9Path = FindFirst(baseDirectory, "boot9.bin", "boot9_prot.bin", "*boot9*.bin");
            var otpPath = FindFirst(baseDirectory, "otp_dec.mem", "otp.mem", "otp.bin", "*otp*.mem", "*otp*.bin");
            var cidPath = FindFirst(baseDirectory, "nand_cid.mem", "nand_cid.bin", "*cid*.mem", "*cid*.bin");
            if (boot9Path == null || otpPath == null || cidPath == null)
            {
                status = "3DS NAND browsing needs a folder containing boot9.bin, OTP, and nand_cid.mem/bin.";
                return false;
            }

            var protectedBoot9 = ReadProtectedBoot9(boot9Path);
            var otp = File.ReadAllBytes(otpPath).AsSpan(0, Math.Min(0x100, (int)new FileInfo(otpPath).Length)).ToArray();
            var cid = File.ReadAllBytes(cidPath).AsSpan(0, 0x10).ToArray();
            if (otp.Length != 0x100 || cid.Length != 0x10)
            {
                status = "3DS OTP must be 256 bytes and NAND CID must be 16 bytes.";
                return false;
            }

            var keys = BuildKeys(protectedBoot9, otp, dev);
            material = new Nintendo3dsKeyMaterial(cid, keys);
            status = $"3DS NAND key material loaded from {baseDirectory}.";
            return true;
        }

        private static byte[] ReadProtectedBoot9(string path)
        {
            var raw = File.ReadAllBytes(path);
            if (raw.Length == 0x10000)
            {
                return raw.AsSpan(0x8000, 0x8000).ToArray();
            }

            if (raw.Length == 0x8000)
            {
                return raw;
            }

            throw new InvalidDataException("boot9.bin must be 32 KiB protected or 64 KiB full dump.");
        }

        private static Dictionary<int, byte[]> BuildKeys(byte[] protectedBoot9, byte[] otpInput, bool dev)
        {
            var keyblobOffset = 0x5860 + (dev ? 0x400 : 0);
            var otpKeyOffset = 0x56E0 + (dev ? 0x20 : 0);
            var otpKey = protectedBoot9.AsSpan(otpKeyOffset, 0x10).ToArray();
            var otpIv = protectedBoot9.AsSpan(otpKeyOffset + 0x10, 0x10).ToArray();
            var otp = otpInput.AsSpan(0, 4).SequenceEqual(OtpMagic)
                ? otpInput
                : AesCbcCrypt(otpInput, otpKey, otpIv, decrypt: true);
            if (!otp.AsSpan(0, 4).SequenceEqual(OtpMagic))
            {
                throw new InvalidDataException("3DS OTP magic was not found.");
            }

            var keyblob = protectedBoot9.AsSpan(keyblobOffset, 0x400).ToArray();
            var extdataKeygen = keyblob.AsSpan(0, 0x200).ToArray();
            var extdataOtp = extdataKeygen.AsSpan(0, 0x24).ToArray();
            var keyY = LoadBoot9KeyY(keyblob);
            keyY[KeyslotCtrNandNew] = WriteUInt128Big(CtrNandNewKeyY);

            var normal = new Dictionary<int, byte[]>();
            var twlCid = dev ? otp.AsSpan(0, 8).ToArray() : otp.AsSpan(8, 8).ToArray();
            var twlCidLo = BinaryPrimitives.ReadUInt32LittleEndian(twlCid.AsSpan(0, 4));
            var twlCidHi = BinaryPrimitives.ReadUInt32LittleEndian(twlCid.AsSpan(4, 4));
            if (!dev)
            {
                twlCidLo ^= 0xB358A6AF;
                twlCidLo |= 0x80000000;
                twlCidHi ^= 0x08C267B7;
            }

            Span<byte> twlKeyXBytes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(twlKeyXBytes[0..4], twlCidLo);
            if (dev)
            {
                Convert.FromHexString("1E4B7AEE8BC042AF").CopyTo(twlKeyXBytes[4..12]);
            }
            else
            {
                Encoding.ASCII.GetBytes("NINTENDO").CopyTo(twlKeyXBytes[4..12]);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(twlKeyXBytes[12..16], twlCidHi);
            var twlKeyY = dev ? DsiDevTwlKeyY : DsiRetailTwlKeyY;
            normal[KeyslotTwlNand] = KeygenTwl(ReadUInt128Little(twlKeyXBytes), twlKeyY);

            var consoleKeyHash = SHA256.HashData(otp.AsSpan(0x90, 0x1C).ToArray().Concat(extdataOtp).ToArray());
            var boot9InternalKey = KeygenCtr(ReadUInt128Big(consoleKeyHash.AsSpan(0, 0x10)), ReadUInt128Big(consoleKeyHash.AsSpan(0x10, 0x10)));
            var generated = GenerateOtpKeyX(extdataKeygen, boot9InternalKey);
            var ctrKeyX = ReadUInt128Big(generated.AsSpan(0, 0x10));
            normal[KeyslotCtrNandOld] = KeygenCtr(ctrKeyX, ReadUInt128Big(keyY[KeyslotCtrNandOld]));
            normal[KeyslotCtrNandNew] = KeygenCtr(ctrKeyX, CtrNandNewKeyY);
            return normal;
        }

        private static Dictionary<int, byte[]> LoadBoot9KeyY(byte[] keyblob)
        {
            var result = new Dictionary<int, byte[]>();
            var offset = 0x170;
            offset += 16 * 4;
            offset += 16 * 4;
            offset += 16 * 4;
            offset += 16 * 4;
            offset += 16 * 4;
            for (var keyslot = 0x04; keyslot < 0x08; keyslot++)
            {
                result[keyslot] = keyblob.AsSpan(offset, 16).ToArray();
                offset += 16;
            }

            return result;
        }

        private static byte[] GenerateOtpKeyX(byte[] extdataKeygen, byte[] boot9InternalKey)
        {
            var extdataOffset = 0;
            extdataOffset += 36;
            var iv = extdataKeygen.AsSpan(extdataOffset, 16).ToArray();
            extdataOffset += 16;
            var data = extdataKeygen.AsSpan(extdataOffset, 64).ToArray();
            return AesCbcCrypt(data, boot9InternalKey, iv, decrypt: false);
        }

        private static byte[] AesCbcCrypt(byte[] data, byte[] key, byte[] iv, bool decrypt)
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var transform = decrypt ? aes.CreateDecryptor(key, iv) : aes.CreateEncryptor(key, iv);
            return transform.TransformFinalBlock(data, 0, data.Length);
        }

        private static string? FindFirst(string directory, params string[] patterns)
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            foreach (var pattern in patterns)
            {
                var path = Path.Combine(directory, pattern);
                if (!pattern.Contains('*') && File.Exists(path))
                {
                    return path;
                }

                var match = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
