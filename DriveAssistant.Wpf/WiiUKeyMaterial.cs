using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace FATXTools.Wpf;

internal sealed class WiiUKeyMaterial
{
    private WiiUKeyMaterial(string otpPath, string? seepromPath, byte[] mlcKey, byte[]? usbKey)
    {
        OtpPath = otpPath;
        SeepromPath = seepromPath;
        MlcKey = mlcKey;
        UsbKey = usbKey;
    }

    public string OtpPath { get; }

    public string? SeepromPath { get; }

    public byte[] MlcKey { get; }

    public byte[]? UsbKey { get; }

    public static bool TryLoad(string? path, out WiiUKeyMaterial? material, out string status)
    {
        material = null;
        status = "No Wii U key path supplied. Provide a folder containing otp.bin and seeprom.bin, or an otp.bin file for MLC/plain WFS images.";
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string? otpPath;
        string? seepromPath = null;
        if (Directory.Exists(path))
        {
            otpPath = FindFirst(path, "otp.bin", "*otp*.bin", "*otp*");
            seepromPath = FindFirst(path, "seeprom.bin", "*seeprom*.bin", "*seeprom*");
        }
        else if (File.Exists(path))
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Contains("seeprom", StringComparison.OrdinalIgnoreCase))
            {
                seepromPath = path;
                otpPath = FindFirst(Path.GetDirectoryName(path) ?? ".", "otp.bin", "*otp*.bin", "*otp*");
            }
            else
            {
                otpPath = path;
                seepromPath = FindFirst(Path.GetDirectoryName(path) ?? ".", "seeprom.bin", "*seeprom*.bin", "*seeprom*");
            }
        }
        else
        {
            status = "Selected Wii U key path does not exist.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(otpPath) || !File.Exists(otpPath))
        {
            status = "Wii U OTP was not found. Expected otp.bin, 1024 bytes.";
            return false;
        }

        var otp = File.ReadAllBytes(otpPath);
        if (otp.Length < 0x190)
        {
            status = $"Wii U OTP is too small: {otp.Length:N0} bytes. Expected at least 0x190 bytes, normally 1024 bytes.";
            return false;
        }

        var mlcKey = otp.AsSpan(0x180, 0x10).ToArray();
        byte[]? usbKey = null;
        if (!string.IsNullOrWhiteSpace(seepromPath) && File.Exists(seepromPath))
        {
            var seeprom = File.ReadAllBytes(seepromPath);
            if (seeprom.Length < 0xC0)
            {
                status = $"Wii U SEEPROM is too small: {seeprom.Length:N0} bytes. Expected at least 0xC0 bytes, normally 512 bytes.";
                return false;
            }

            var usbSeed = seeprom.AsSpan(0xB0, 0x10).ToArray();
            var seedKey = otp.AsSpan(0x130, 0x10).ToArray();
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var encryptor = aes.CreateEncryptor(seedKey, null);
            usbKey = new byte[0x10];
            encryptor.TransformBlock(usbSeed, 0, usbSeed.Length, usbKey, 0);
        }

        material = new WiiUKeyMaterial(otpPath, seepromPath, mlcKey, usbKey);
        status = usbKey != null
            ? "Loaded Wii U OTP and SEEPROM; USB WFS key is available."
            : "Loaded Wii U OTP; MLC WFS key is available. USB/dev HDD images also need seeprom.bin.";
        return true;
    }

    private static string? FindFirst(string directory, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return null;
    }
}
