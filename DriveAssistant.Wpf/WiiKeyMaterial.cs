using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace FATXTools.Wpf;

internal sealed class WiiKeyMaterial
{
    private WiiKeyMaterial(string sourcePath, byte[]? commonKey, byte[]? sdKey, byte[]? sdIv, byte[]? md5Blanker, byte[]? nandKey, byte[]? nandAesIv)
    {
        SourcePath = sourcePath;
        CommonKey = commonKey;
        SdKey = sdKey;
        SdIv = sdIv;
        Md5Blanker = md5Blanker;
        NandKey = nandKey;
        NandAesIv = nandAesIv;
    }

    public string SourcePath { get; }

    public byte[]? CommonKey { get; }

    public byte[]? SdKey { get; }

    public byte[]? SdIv { get; }

    public byte[]? Md5Blanker { get; }

    public byte[]? NandKey { get; }

    public byte[]? NandAesIv { get; }

    public bool HasCommonKey => CommonKey?.Length == 16;

    public bool HasSdKeySet => SdKey?.Length == 16 && SdIv?.Length == 16;

    public bool HasNandKey => NandKey?.Length == 16;

    public bool HasAnyKey => HasCommonKey || HasSdKeySet || Md5Blanker?.Length == 16 || HasNandKey;

    public static bool TryLoad(string? path, out WiiKeyMaterial? material, out string status)
    {
        material = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            status = "No Wii key path supplied. Optional Wii key files are common-key, sd-key, sd-iv, md5-blanker, and BootMii keys.bin.";
            return false;
        }

        try
        {
            if (Directory.Exists(path))
            {
                material = LoadFromDirectory(path);
            }
            else if (File.Exists(path))
            {
                material = IsTarGz(path)
                    ? LoadFromTarGz(path)
                    : LoadFromSingleFile(path);
            }
            else
            {
                status = "Selected Wii key path does not exist.";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            status = $"Wii key material could not be loaded: {ex.Message}";
            return false;
        }

        if (material == null || !material.HasAnyKey)
        {
            status = "No supported Wii key material was found. Expected 16-byte common-key, sd-key, sd-iv, md5-blanker, or BootMii keys.bin material.";
            return false;
        }

        status = BuildStatus(material);
        return true;
    }

    private static WiiKeyMaterial LoadFromDirectory(string path)
    {
        var keysPath = FindFirst(path, "keys.bin", "*keys*.bin");
        return new WiiKeyMaterial(
            path,
            ReadKeyFile(FindFirst(path, "common-key", "common_key", "common-key.bin", "*common*key*")),
            ReadKeyFile(FindFirst(path, "sd-key", "sd_key", "sd-key.bin", "*sd*key*")),
            ReadKeyFile(FindFirst(path, "sd-iv", "sd_iv", "sd-iv.bin", "*sd*iv*")),
            ReadKeyFile(FindFirst(path, "md5-blanker", "md5_blanker", "*md5*blank*")),
            ReadBootMiiNandKey(keysPath),
            ReadBootMiiNandIv(keysPath));
    }

    private static WiiKeyMaterial LoadFromSingleFile(string path)
    {
        var name = Path.GetFileName(path);
        var bytes = ReadKeyFile(path);
        return new WiiKeyMaterial(
            path,
            LooksLike(name, "common") ? bytes : null,
            LooksLike(name, "sd-key", "sd_key") ? bytes : null,
            LooksLike(name, "sd-iv", "sd_iv") ? bytes : null,
            LooksLike(name, "md5") ? bytes : null,
            ReadBootMiiNandKey(path),
            ReadBootMiiNandIv(path));
    }

    private static WiiKeyMaterial LoadFromTarGz(string path)
    {
        byte[]? commonKey = null;
        byte[]? sdKey = null;
        byte[]? sdIv = null;
        byte[]? md5Blanker = null;
        byte[]? nandKey = null;
        byte[]? nandAesIv = null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream == null)
            {
                continue;
            }

            var name = Path.GetFileName(entry.Name.Replace('\\', '/'));
            if (!IsSupportedName(name) && !LooksLike(name, "keys"))
            {
                continue;
            }

            using var buffer = new MemoryStream();
            entry.DataStream.CopyTo(buffer);
            var raw = buffer.ToArray();
            if (LooksLike(name, "keys"))
            {
                nandKey ??= ReadBootMiiNandKey(raw);
                nandAesIv ??= ReadBootMiiNandIv(raw);
                continue;
            }

            var bytes = NormalizeKeyBytes(raw);
            if (bytes?.Length != 16)
            {
                continue;
            }

            if (LooksLike(name, "common"))
            {
                commonKey = bytes;
            }
            else if (LooksLike(name, "sd-key", "sd_key"))
            {
                sdKey = bytes;
            }
            else if (LooksLike(name, "sd-iv", "sd_iv"))
            {
                sdIv = bytes;
            }
            else if (LooksLike(name, "md5"))
            {
                md5Blanker = bytes;
            }
        }

        return new WiiKeyMaterial(path, commonKey, sdKey, sdIv, md5Blanker, nandKey, nandAesIv);
    }

    private static byte[]? ReadKeyFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return NormalizeKeyBytes(File.ReadAllBytes(path));
    }

    private static byte[]? ReadBootMiiNandKey(string? path)
    {
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? null
            : ReadBootMiiNandKey(File.ReadAllBytes(path));
    }

    private static byte[]? ReadBootMiiNandIv(string? path)
    {
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? null
            : ReadBootMiiNandIv(File.ReadAllBytes(path));
    }

    private static byte[]? ReadBootMiiNandKey(byte[] raw)
    {
        return raw.Length >= 0x168 ? raw.AsSpan(0x158, 0x10).ToArray() : null;
    }

    private static byte[]? ReadBootMiiNandIv(byte[] raw)
    {
        return raw.Length >= 0x178 ? raw.AsSpan(0x168, 0x10).ToArray() : null;
    }

    private static byte[]? NormalizeKeyBytes(byte[] raw)
    {
        if (raw.Length == 16)
        {
            return raw;
        }

        var text = System.Text.Encoding.ASCII.GetString(raw)
            .Where(Uri.IsHexDigit)
            .ToArray();
        if (text.Length < 32)
        {
            return null;
        }

        var hex = new string(text, 0, 32);
        var bytes = new byte[16];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    private static string? FindFirst(string path, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static bool IsSupportedName(string name)
    {
        return LooksLike(name, "common")
            || LooksLike(name, "sd-key", "sd_key")
            || LooksLike(name, "sd-iv", "sd_iv")
            || LooksLike(name, "md5")
            || LooksLike(name, "keys");
    }

    private static bool LooksLike(string name, params string[] fragments)
    {
        return fragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTarGz(string path)
    {
        return path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildStatus(WiiKeyMaterial material)
    {
        var parts = new[]
        {
            material.HasCommonKey ? "common key" : null,
            material.HasSdKeySet ? "SD key/IV" : null,
            material.Md5Blanker?.Length == 16 ? "MD5 blanker" : null,
            material.HasNandKey ? "NAND key" : null
        }.Where(part => part != null);
        return $"Loaded Wii key material: {string.Join(", ", parts)}.";
    }
}
