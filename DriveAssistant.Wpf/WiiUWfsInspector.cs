using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace FATXTools.Wpf;

internal sealed record WiiUWfsHeaderInfo(
    bool IsValid,
    bool IsEncrypted,
    string KeyType,
    uint Version,
    ushort DeviceType,
    uint DeviceIv,
    string Detail);

internal static class WiiUWfsInspector
{
    private const int SectorSize = 512;
    private const int PhysicalBlockSize = 0x1000;
    private const int LogicalBlockSize = 0x2000;
    private const uint WfsVersion = 0x01010800;

    public static WiiUWfsHeaderInfo Inspect(string sourcePath, WiiUKeyMaterial? keyMaterial)
    {
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        foreach (var blockSize in new[] { PhysicalBlockSize, LogicalBlockSize })
        {
            if (stream.Length < blockSize)
            {
                continue;
            }

            var block = new byte[blockSize];
            stream.Position = 0;
            if (stream.Read(block, 0, block.Length) != block.Length)
            {
                continue;
            }

            var plain = TryParseHeader(block, encrypted: false, keyType: "plain");
            if (plain.IsValid)
            {
                return plain with { Detail = $"{plain.Detail}; block size 0x{blockSize:X}" };
            }

            if (keyMaterial?.UsbKey != null)
            {
                var decrypted = DecryptBlock(block, keyMaterial.UsbKey, stream.Length);
                var usb = TryParseHeader(decrypted, encrypted: true, keyType: "usb");
                if (usb.IsValid)
                {
                    return usb with { Detail = $"{usb.Detail}; block size 0x{blockSize:X}" };
                }
            }

            if (keyMaterial != null)
            {
                var decrypted = DecryptBlock(block, keyMaterial.MlcKey, stream.Length);
                var mlc = TryParseHeader(decrypted, encrypted: true, keyType: "mlc");
                if (mlc.IsValid)
                {
                    return mlc with { Detail = $"{mlc.Detail}; block size 0x{blockSize:X}" };
                }
            }
        }

        var keyDetail = keyMaterial == null
            ? "No OTP/SEEPROM keys were loaded."
            : keyMaterial.UsbKey == null
                ? "OTP was loaded, but no SEEPROM USB key seed was available."
                : "OTP and SEEPROM were loaded, but the WFS header did not validate with the derived keys.";
        return new WiiUWfsHeaderInfo(false, false, "unknown", 0, 0, 0, keyDetail);
    }

    private static byte[] DecryptBlock(byte[] encrypted, byte[] key, long deviceLength)
    {
        var data = encrypted.ToArray();
        var iv = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(iv.AsSpan(0), (uint)encrypted.Length);
        BinaryPrimitives.WriteUInt32BigEndian(iv.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(iv.AsSpan(8), (uint)Math.Max(0, deviceLength / SectorSize));
        BinaryPrimitives.WriteUInt32BigEndian(iv.AsSpan(12), SectorSize);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor(key, iv);
        decryptor.TransformBlock(data, 0, data.Length, data, 0);
        return data;
    }

    private static WiiUWfsHeaderInfo TryParseHeader(byte[] block, bool encrypted, string keyType)
    {
        if (block.Length < 0x60)
        {
            return Invalid(keyType);
        }

        var deviceIv = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0x18));
        var version = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0x1C));
        var deviceType = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(0x20));
        if (version != WfsVersion || deviceType is not (0x1281 or 0x136A or 0x16A2))
        {
            return Invalid(keyType);
        }

        var typeText = deviceType == 0x16A2 ? "USB/dev storage" : deviceType == 0x1281 ? "MLC/dev kit" : "MLC";
        var detail = encrypted
            ? $"WFS header decrypted with {keyType} key; {typeText}"
            : $"Plain WFS header detected; {typeText}";
        return new WiiUWfsHeaderInfo(true, encrypted, keyType, version, deviceType, deviceIv, detail);
    }

    private static WiiUWfsHeaderInfo Invalid(string keyType)
    {
        return new WiiUWfsHeaderInfo(false, false, keyType, 0, 0, 0, "Invalid WFS header");
    }
}
