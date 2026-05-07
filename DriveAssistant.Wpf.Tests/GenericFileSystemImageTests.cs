using FATXTools.Wpf;
using System;
using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Xunit;

namespace FATXTools.Wpf.Tests;

public sealed class GenericFileSystemImageTests
{
    [Fact]
    public void Open_Fat32RawVolume_LoadsRootFileAndExportsBytes()
    {
        using var temp = new TempFile(CreateFat32Image());
        using var image = GenericFileSystemImage.Open(temp.Path);

        var partition = Assert.Single(image.Partitions);
        var volume = Assert.IsType<Fat32Volume>(partition.GenericVolume);
        var entry = Assert.Single(volume.GetRoot());

        Assert.Equal("HELLO.TXT", entry.Name);
        Assert.Equal("FAT32", volume.FamilyText);
        Assert.Equal(5, entry.Length);
        Assert.Equal("Contiguous", entry.FragmentationStatus);
        Assert.Single(entry.Extents);

        using var output = TempFile.Empty();
        volume.CopyFile(entry, output.Path);
        Assert.Equal(Encoding.ASCII.GetBytes("hello"), File.ReadAllBytes(output.Path));
    }

    [Fact]
    public void GenericCopyFile_ReportsProgressAndHonorsCancellation()
    {
        using var temp = new TempFile(CreateFat32Image());
        using var image = GenericFileSystemImage.Open(temp.Path);

        var volume = Assert.IsType<Fat32Volume>(Assert.Single(image.Partitions).GenericVolume);
        var entry = Assert.Single(volume.GetRoot());
        long reportedBytes = 0;

        using var output = TempFile.Empty();
        volume.CopyFile(entry, output.Path, bytes => reportedBytes += bytes, CancellationToken.None);

        Assert.Equal(entry.Length, reportedBytes);
        Assert.Equal(Encoding.ASCII.GetBytes("hello"), File.ReadAllBytes(output.Path));

        using var canceled = TempFile.Empty();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            volume.CopyFile(entry, canceled.Path, _ => { }, cancellation.Token));
    }

    [Fact]
    public void Open_ExFatNoFatChainDirectory_ReadsEntriesPastFirstCluster()
    {
        using var temp = new TempFile(CreateExFatImageWithMultiClusterDirectory());
        using var image = GenericFileSystemImage.Open(temp.Path);

        var partition = Assert.Single(image.Partitions);
        var volume = Assert.IsType<ExFatVolume>(partition.GenericVolume);
        var directory = Assert.Single(volume.GetRoot());
        var entry = Assert.Single(directory.Children);

        Assert.Equal("DIR", directory.Name);
        Assert.True(directory.IsDirectory);
        Assert.Equal("INNER.BIN", entry.Name);
        Assert.Equal("exFAT", volume.FamilyText);
        Assert.Equal(5, entry.Length);
        Assert.Equal("Contiguous", entry.FragmentationStatus);

        using var output = TempFile.Empty();
        volume.CopyFile(entry, output.Path);
        Assert.Equal(Encoding.ASCII.GetBytes("world"), File.ReadAllBytes(output.Path));
    }

    [Fact]
    public void Open_ExFatInside4096ByteGptRawImage_LoadsPartition()
    {
        using var temp = new TempFile(Create4096ByteGptImage(CreateExFatImageWithMultiClusterDirectory(), "Portable exFAT"));
        using var image = GenericFileSystemImage.Open(temp.Path);

        var partition = Assert.Single(image.Partitions);
        var volume = Assert.IsType<ExFatVolume>(partition.GenericVolume);
        var directory = Assert.Single(volume.GetRoot());

        Assert.Equal("Portable exFAT", partition.Name);
        Assert.Equal("DIR", directory.Name);
        Assert.Equal("INNER.BIN", Assert.Single(directory.Children).Name);
    }

    [Fact]
    public void XboxGptParser_Reads4096ByteSectorRawImageAsXboxSeries()
    {
        using var temp = new TempFile(Create4096ByteGptImage(Array.Empty<byte>(), "Temp Content"));
        using var stream = File.OpenRead(temp.Path);
        var storageType = typeof(GenericFileSystemImage).Assembly.GetType("FATXTools.Wpf.XboxStorageImage", throwOnError: true)!;
        var read = storageType.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == "ReadGptPartitions"
                              && method.GetParameters() is [{ ParameterType: var parameterType }]
                              && parameterType == typeof(Stream));
        var determineFamily = storageType.GetMethod("DetermineFamily", BindingFlags.NonPublic | BindingFlags.Static)!;
        var partitions = ((IEnumerable)read.Invoke(null, [stream])!).Cast<object>().ToList();

        var partition = Assert.Single(partitions);
        Assert.Equal("Temp Content", GetProperty<string>(partition, "Name"));
        Assert.Equal(4096, GetProperty<int>(partition, "LogicalSectorSize"));
        Assert.Equal(0x6000, GetProperty<long>(partition, "Offset"));

        var family = determineFamily.Invoke(null, [partition, false])!;
        Assert.Equal("XboxSeries", family.ToString());
    }

    [Fact]
    public void XboxImageOpen_ExposesXbfsAsReadableBootFilesystem()
    {
        using var temp = new TempFile(Create4096ByteGptImage(CreateXbfsPayload(), "XBFS"));
        var storageType = typeof(GenericFileSystemImage).Assembly.GetType("FATXTools.Wpf.XboxStorageImage", throwOnError: true)!;
        var open = storageType.GetMethod("Open", BindingFlags.Public | BindingFlags.Static)!;
        using var image = (IDisposable)open.Invoke(null, new object[] { temp.Path })!;
        var bootFileSystems = ((IEnumerable)storageType.GetProperty("BootFileSystems")!.GetValue(image)!).Cast<object>().ToArray();

        var boot = Assert.Single(bootFileSystems);
        var bootType = boot.GetType();
        Assert.Equal("XBFS", GetProperty<string>(boot, "Name"));
        Assert.Equal("Xbox Boot File System (XBFS)", GetProperty<string>(boot, "FamilyText"));

        var entries = ((IEnumerable)bootType.GetMethod("GetRoot")!.Invoke(boot, Array.Empty<object>())!).Cast<object>().ToArray();
        var header = Assert.Single(entries, entry => GetProperty<string>(entry, "Name") == "header.bin");
        var devkit = Assert.Single(entries, entry => GetProperty<string>(entry, "Name") == "devkit.ini");
        Assert.Equal(0x6000, GetProperty<long>(header, "Offset"));
        Assert.Equal(0xA000, GetProperty<long>(devkit, "Offset"));

        using var output = TempFile.Empty();
        bootType.GetMethod("CopyFile", new[] { devkit.GetType(), typeof(string) })!.Invoke(boot, new[] { devkit, output.Path });
        var exported = File.ReadAllBytes(output.Path);
        Assert.Equal(0x1000, exported.Length);
        Assert.Equal(Encoding.ASCII.GetBytes("xbfs-test"), exported.Take(9).ToArray());
    }

    [Fact]
    public void GenericCarver_XvdUsesNextXvdBoundAndReportsHeaderType()
    {
        using var temp = new TempFile(CreateXvdCarverImage());
        var carver = new GenericFileCarver(temp.Path, 0, 0x9000, 0, 0x1000, "test image");
        var rows = carver.Analyze(System.Threading.CancellationToken.None, null);

        Assert.Equal(2, rows.Count(row => row.Kind == "XVD"));
        var first = rows.Single(row => row.SourceOffset == 0);
        Assert.Equal(0x5000, first.Size);
        Assert.Contains("header type 0x06", first.Detail);
        Assert.Contains("dev title/content container", first.Detail);
        Assert.Contains("display name: Sample Game", first.Detail);

        var second = rows.Single(row => row.SourceOffset == 0x5000);
        Assert.Contains("header type 0x41", second.Detail);
        Assert.Contains("retail", second.Detail);
    }

    private static byte[] CreateFat32Image()
    {
        var image = new byte[4 * 512];
        var boot = image.AsSpan(0, 512);
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[11..], 512);
        boot[13] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[14..], 1);
        boot[16] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(boot[36..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[44..], 2);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot[82..]);
        boot[510] = 0x55;
        boot[511] = 0xAA;

        var fat = image.AsSpan(512, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(fat[0..], 0x0FFFFFF8);
        BinaryPrimitives.WriteUInt32LittleEndian(fat[4..], 0x0FFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(fat[8..], 0x0FFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(fat[12..], 0x0FFFFFFF);

        var entry = image.AsSpan(1024, 32);
        Encoding.ASCII.GetBytes("HELLO   TXT").CopyTo(entry);
        entry[11] = 0x20;
        BinaryPrimitives.WriteUInt16LittleEndian(entry[26..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], 5);
        Encoding.ASCII.GetBytes("hello").CopyTo(image.AsSpan(1536, 5));
        return image;
    }

    private static byte[] CreateExFatImageWithMultiClusterDirectory()
    {
        var image = new byte[6 * 512];
        var boot = image.AsSpan(0, 512);
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[80..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[84..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[88..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[96..], 2);
        boot[108] = 9;
        boot[109] = 0;
        boot[510] = 0x55;
        boot[511] = 0xAA;

        var fat = image.AsSpan(512, 512);
        BinaryPrimitives.WriteUInt32LittleEndian(fat[8..], 0xFFFFFFFF);

        WriteExFatEntrySet(image, ClusterOffset(2), "DIR", 0x10, firstCluster: 3, dataLength: 1024, noFatChain: true);
        for (var offset = ClusterOffset(3); offset < ClusterOffset(4); offset += 32)
        {
            image[offset] = 0x81;
        }

        WriteExFatEntrySet(image, ClusterOffset(4), "INNER.BIN", 0x20, firstCluster: 5, dataLength: 5, noFatChain: true);
        Encoding.ASCII.GetBytes("world").CopyTo(image.AsSpan(ClusterOffset(5), 5));
        return image;
    }

    private static byte[] CreateXbfsPayload()
    {
        var payload = new byte[0x6000];
        var header = payload.AsSpan(0, 0x400);
        Encoding.ASCII.GetBytes("SFBX").CopyTo(header);
        header[4] = 1;
        header[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0x000F);
        WriteXbfsEntry(header, 1, offsetPages: 0x6, sizePages: 0x4);
        WriteXbfsEntry(header, 2, offsetPages: 0xA, sizePages: 0x1);
        Encoding.ASCII.GetBytes("xbfs-test").CopyTo(payload.AsSpan(0x4000));
        return payload;
    }

    private static byte[] CreateXvdCarverImage()
    {
        var image = new byte[0x9000];
        WriteXvdHeader(image, 0, 0x06);
        WriteXvdHeader(image, 0x5000, 0x41);
        image[0x11FF] = 0xFE;
        Encoding.ASCII.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <Package>
              <Applications>
                <Application>
                  <uap:VisualElements displayname="Sample Game" />
                </Application>
              </Applications>
            </Package>
            """).CopyTo(image.AsSpan(0x1200));
        return image;
    }

    private static void WriteXvdHeader(byte[] image, int offset, byte type)
    {
        Encoding.ASCII.GetBytes("MSFT-XVD").CopyTo(image.AsSpan(offset + 0x200));
        image[offset + 0x208] = type;
    }

    private static void WriteXbfsEntry(Span<byte> header, int index, uint offsetPages, uint sizePages)
    {
        var entry = header.Slice(0x20 + index * 0x10, 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, offsetPages);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], sizePages);
    }

    private static byte[] Create4096ByteGptImage(byte[] partitionPayload, string partitionName)
    {
        const int logicalSectorSize = 4096;
        const ulong firstPartitionLba = 6;
        var partitionOffset = checked((int)(firstPartitionLba * logicalSectorSize));
        var partitionLength = Math.Max(logicalSectorSize, AlignUp(partitionPayload.Length, logicalSectorSize));
        var image = new byte[partitionOffset + partitionLength];
        var header = image.AsSpan(logicalSectorSize, logicalSectorSize);
        Encoding.ASCII.GetBytes("EFI PART").CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 92);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)(image.Length / logicalSectorSize - 1));
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], firstPartitionLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], (ulong)(image.Length / logicalSectorSize - 2));
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], 64);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], 128);

        var entry = image.AsSpan(logicalSectorSize * 2, 128);
        new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7").TryWriteBytes(entry);
        Guid.Parse("11111111-2222-3333-4444-555555555555").TryWriteBytes(entry[16..]);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], firstPartitionLba);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], firstPartitionLba + (ulong)(partitionLength / logicalSectorSize) - 1);
        Encoding.Unicode.GetBytes(partitionName).CopyTo(entry[56..]);

        partitionPayload.CopyTo(image.AsSpan(partitionOffset));
        return image;
    }

    private static void WriteExFatEntrySet(
        byte[] image,
        int offset,
        string name,
        ushort attributes,
        uint firstCluster,
        ulong dataLength,
        bool noFatChain)
    {
        var primary = image.AsSpan(offset, 32);
        primary[0] = 0x85;
        primary[1] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(primary[4..], attributes);

        var stream = image.AsSpan(offset + 32, 32);
        stream[0] = 0xC0;
        stream[1] = noFatChain ? (byte)0x02 : (byte)0x00;
        stream[3] = (byte)name.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(stream[8..], dataLength);
        BinaryPrimitives.WriteUInt32LittleEndian(stream[20..], firstCluster);
        BinaryPrimitives.WriteUInt64LittleEndian(stream[24..], dataLength);

        var fileName = image.AsSpan(offset + 64, 32);
        fileName[0] = 0xC1;
        Encoding.Unicode.GetBytes(name).CopyTo(fileName[2..]);
    }

    private static int ClusterOffset(uint cluster)
    {
        return 1024 + checked((int)(cluster - 2)) * 512;
    }

    private static int AlignUp(int value, int alignment)
    {
        return value == 0 ? alignment : ((value + alignment - 1) / alignment) * alignment;
    }

    private static T GetProperty<T>(object instance, string name)
    {
        return (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
    }

    private sealed class TempFile : IDisposable
    {
        private TempFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public TempFile(byte[] bytes)
            : this(System.IO.Path.GetTempFileName())
        {
            File.WriteAllBytes(Path, bytes);
        }

        public static TempFile Empty()
        {
            return new TempFile(System.IO.Path.GetTempFileName());
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
            }
        }
    }
}
