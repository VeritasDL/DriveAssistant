using FATX.Analyzers.Signatures;
using FATXTools.Wpf;
using System;
using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.IO.Compression;
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
        var systemXvd = Assert.Single(entries, entry => GetProperty<string>(entry, "Name") == "system.xvd");
        Assert.Equal(0x6000, GetProperty<long>(header, "Offset"));
        Assert.Equal(0xA000, GetProperty<long>(devkit, "Offset"));
        Assert.Equal("XVD dev title/content container", GetProperty<string>(systemXvd, "Kind"));
        Assert.Contains("display name: Dashboard Shell", GetProperty<string>(systemXvd, "MetadataStatus"));

        using var output = TempFile.Empty();
        bootType.GetMethod("CopyFile", new[] { devkit.GetType(), typeof(string) })!.Invoke(boot, new[] { devkit, output.Path });
        var exported = File.ReadAllBytes(output.Path);
        Assert.Equal(0x1000, exported.Length);
        Assert.Equal(Encoding.ASCII.GetBytes("xbfs-test"), exported.Take(9).ToArray());
    }

    [Fact]
    public void PlayStationArchiveDisk_ReadsZipBackedImageWithoutRawExtraction()
    {
        var image = new byte[3 * 1024 * 1024 + 123];
        for (var index = 0; index < image.Length; index++)
        {
            image[index] = (byte)(index * 31 + index / 7);
        }

        var zipPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("nested/test.img", CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                entryStream.Write(image, 0, image.Length);
            }

            var archiveDiskType = typeof(GenericFileSystemImage).Assembly.GetType("FATXTools.Wpf.PlayStationArchiveDisk", throwOnError: true)!;
            var isSupported = archiveDiskType.GetMethod("IsSupported", BindingFlags.Public | BindingFlags.Static)!;
            var open = archiveDiskType.GetMethod("Open", BindingFlags.Public | BindingFlags.Static)!;
            var read = archiveDiskType.GetMethod("Read", BindingFlags.Public | BindingFlags.Instance)!;

            Assert.True((bool)isSupported.Invoke(null, [zipPath])!);
            using var disk = (IDisposable)open.Invoke(null, [zipPath])!;
            Assert.Equal(image.LongLength, GetProperty<long>(disk, "Length"));
            Assert.Equal("nested/test.img", GetProperty<string>(disk, "EntryName"));

            var boundaryBuffer = new byte[4096];
            var boundaryOffset = 1024 * 1024 - 100;
            var boundaryRead = (int)read.Invoke(disk, [boundaryOffset, boundaryBuffer, 0, boundaryBuffer.Length])!;
            Assert.Equal(boundaryBuffer.Length, boundaryRead);
            Assert.Equal(image.AsSpan(boundaryOffset, boundaryRead).ToArray(), boundaryBuffer);

            var backwardBuffer = new byte[128];
            var backwardRead = (int)read.Invoke(disk, [512L, backwardBuffer, 0, backwardBuffer.Length])!;
            Assert.Equal(backwardBuffer.Length, backwardRead);
            Assert.Equal(image.AsSpan(512, backwardRead).ToArray(), backwardBuffer);

            var tailBuffer = new byte[100];
            var tailOffset = image.Length - 50L;
            var tailRead = (int)read.Invoke(disk, [tailOffset, tailBuffer, 0, tailBuffer.Length])!;
            Assert.Equal(50, tailRead);
            Assert.Equal(image.AsSpan((int)tailOffset, tailRead).ToArray(), tailBuffer.Take(tailRead).ToArray());
        }
        finally
        {
            try
            {
                File.Delete(zipPath);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void PlayStationStorageImage_ManagedPs4Reader_OpensOrbisFatPartitionWithoutNativeBridge()
    {
        using var imageFile = new TempFile(CreatePs4OrbisFat16Image());
        using var keyFile = new TempFile(new byte[32]);

        using var image = PlayStationStorageImage.Open(imageFile.Path, keyFile.Path);

        var volume = Assert.Single(image.Volumes);
        Assert.Equal("eap_vsh", volume.Name);
        Assert.True(volume.IsLoaded);
        Assert.Equal("PlayStation 4 HDD (managed)", volume.FamilyText);
        Assert.Empty(volume.GetRoot());
    }

    [Fact]
    public void PlayStationVolume_ParsesDeepUfsMetadataRowsIncludingNameOnlyCandidates()
    {
        var json = """
            {
              "partition":"user",
              "files":[
                {
                  "name":"SAVE.DAT",
                  "type":"UFS dirent slack candidate",
                  "inode":0,
                  "metadataOffset":"0x1234",
                  "recordLength":16,
                  "fileType":8,
                  "nameLength":8,
                  "isDeleted":true,
                  "metadataStatus":"Slack dirent has no inode pointer; recoverability: Name only",
                  "fragmentationStatus":"Name only"
                },
                {
                  "name":"RECOVER.BIN",
                  "type":"Deleted UFS dirent with inode",
                  "inode":42,
                  "inodeOffset":"0x2000",
                  "direntOffset":"0x3000",
                  "recordLength":20,
                  "fileType":8,
                  "nameLength":11,
                  "isDeleted":true,
                  "metadataStatus":"Deleted dirent correlated to orphan inode; recoverability: High",
                  "size":5,
                  "dataOffsetCount":1,
                  "dataRunCount":1,
                  "largestRunBytes":5,
                  "fragmentationStatus":"Contiguous",
                  "dataRanges":"0x4000+0x5",
                  "dataOffsets":"0x4000"
                }
              ]
            }
            """;
        var volume = new PlayStationVolume("image.img", "keys.bin", "user", 0, 1024, new FakePlayStationOperations(json));

        var rows = volume.ScanDeletedInodes();

        var nameOnly = Assert.Single(rows, row => row.Name == "SAVE.DAT");
        Assert.Equal(0u, nameOnly.Inode);
        Assert.Equal("UFS dirent slack candidate", nameOnly.Kind);
        Assert.True(nameOnly.IsDeleted);
        Assert.Equal(0x1234, nameOnly.Offset);
        Assert.Contains("Name only", nameOnly.MetadataStatus);

        var inodeBacked = Assert.Single(rows, row => row.Name == "RECOVER.BIN");
        Assert.Equal(42u, inodeBacked.Inode);
        Assert.Equal(5, inodeBacked.Size);
        Assert.Equal(0x3000, inodeBacked.Offset);
        Assert.Equal("Contiguous", inodeBacked.FragmentationStatus);
    }

    [Fact]
    public void NtfsBitmapReader_CollapsesAllocatedBitsIntoRuns()
    {
        using var bitmap = new MemoryStream(new byte[] { 0b0001_1110, 0b1000_0001 });
        var volumeType = typeof(GenericFileSystemImage).Assembly.GetType("FATXTools.Wpf.XboxNtfsVolume", throwOnError: true)!;
        var readRuns = volumeType.GetMethod("ReadAllocationRuns", BindingFlags.NonPublic | BindingFlags.Static)!;
        var runs = ((IEnumerable)readRuns.Invoke(null, [bitmap, 16L])!).Cast<object>().ToArray();

        Assert.Equal(3, runs.Length);
        Assert.Equal(1, GetProperty<long>(runs[0], "StartCluster"));
        Assert.Equal(4, GetProperty<long>(runs[0], "ClusterCount"));
        Assert.Equal(8, GetProperty<long>(runs[1], "StartCluster"));
        Assert.Equal(1, GetProperty<long>(runs[1], "ClusterCount"));
        Assert.Equal(15, GetProperty<long>(runs[2], "StartCluster"));
        Assert.Equal(1, GetProperty<long>(runs[2], "ClusterCount"));
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

    [Fact]
    public void GenericCarver_BalancedProfileKeepsNestedXvdFileSystemsSeparate()
    {
        var image = new byte[0xA000];
        WriteXvdHeader(image, 0, 0x41);
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(image.AsSpan(0x3003));
        image[0x355] = 0x55;
        image[0x356] = 0xAA;

        using var temp = new TempFile(image);
        var carver = new GenericFileCarver(temp.Path, 0, image.Length, 0, 0x1000, "outer NTFS partition", ScanProfile.Balanced);
        var rows = carver.Analyze(CancellationToken.None, null);

        var outer = Assert.Single(rows, row => row.Kind == "XVD");
        var nested = Assert.Single(rows, row => row.Kind == "NTFS");
        Assert.Equal("outer NTFS partition", outer.Source);
        Assert.Contains("outer NTFS partition > ", nested.Source);
        Assert.Contains("nested inside XVD/XVC container", nested.Detail);
    }

    [Fact]
    public void GenericCarver_FastProfileSkipsNestedXvdFileSystemRows()
    {
        var image = new byte[0xA000];
        WriteXvdHeader(image, 0, 0x41);
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(image.AsSpan(0x3003));

        using var temp = new TempFile(image);
        var carver = new GenericFileCarver(temp.Path, 0, image.Length, 0, 0x1000, "outer NTFS partition", ScanProfile.Fast);
        var rows = carver.Analyze(CancellationToken.None, null);

        Assert.Single(rows);
        Assert.DoesNotContain(rows, row => row.Kind == "NTFS");
    }

    [Fact]
    public void GenericCarver_LargerSyntheticXboxFixtureFindsXvdAndFatxMarkers()
    {
        var image = new byte[0x900000];
        WriteXvdHeader(image, 0x400000, 0x41);
        Encoding.ASCII.GetBytes("FATX").CopyTo(image.AsSpan(0x500000));

        using var temp = new TempFile(image);
        var carver = new GenericFileCarver(temp.Path, 0, image.Length, 0, 0x1000, "large synthetic Xbox GPT/NTFS fixture", ScanProfile.Exhaustive);
        var rows = carver.Analyze(CancellationToken.None, null);

        Assert.Contains(rows, row => row.Kind == "XVD" && row.SourceOffset == 0x400000);
        Assert.Contains(rows, row => row.Kind == "FATX" && row.Source.Contains(">"));
    }

    [Fact]
    public void GenericCarver_DetectsPs4PkgAndPlayStationSelf()
    {
        var image = new byte[0x5000];
        Encoding.ASCII.GetBytes("SCE\0").CopyTo(image.AsSpan(0));
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(0x10), 0x40);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(0x18), 0x80);

        image[0x1000] = 0x7F;
        Encoding.ASCII.GetBytes("CNT").CopyTo(image.AsSpan(0x1001));
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(0x1018), 0x2000);

        using var temp = new TempFile(image);
        var carver = new GenericFileCarver(temp.Path, 0, image.Length, 0, 0x1000, "ps4 test");
        var rows = carver.Analyze(CancellationToken.None, null);

        var self = Assert.Single(rows, row => row.Kind == "SELF");
        Assert.Equal(0xC0, self.Size);
        Assert.Contains("PlayStation SELF", self.Detail);

        var pkg = Assert.Single(rows, row => row.Kind == "PKG");
        Assert.Equal(0x2000, pkg.Size);
        Assert.Contains("PS4 package", pkg.Detail);
    }

    [Fact]
    public void Ps4UfsDirentScanner_ReturnsDeletedCandidates()
    {
        var image = new byte[0x2000];
        var block = image.AsSpan(0x1000, 0x1000);
        var cursor = 0;
        cursor += WriteUfsDirent(block[cursor..], 2, 12, 4, ".");
        cursor += WriteUfsDirent(block[cursor..], 2, 12, 4, "..");
        cursor += WriteUfsDirent(block[cursor..], 0, 16, 8, "SAVE.DAT");
        _ = WriteUfsDirent(block[cursor..], 42, 16, 8, "LIVE.BIN");

        using var temp = new TempFile(image);
        var scanner = new Ps4UfsDirentScanner(temp.Path, 0x80000000);
        var rows = scanner.Analyze(CancellationToken.None, null);

        var deleted = Assert.Single(rows, row => row.Name == "SAVE.DAT");
        Assert.True(deleted.IsDeleted);
        Assert.Equal(0x80001018, deleted.Offset);
        Assert.Equal("Deleted PS4 UFS dirent candidate", deleted.MetadataStatus);

        var active = Assert.Single(rows, row => row.Name == "LIVE.BIN");
        Assert.False(active.IsDeleted);
        Assert.Equal((uint)42, active.Inode);
    }

    [Fact]
    public void GenericCarver_CustomSignatureHonorsHeaderOffset()
    {
        var image = new byte[0x2000];
        Encoding.ASCII.GetBytes("WAVE").CopyTo(image.AsSpan(0x408));

        using var temp = new TempFile(image);
        var carver = new GenericFileCarver(temp.Path, 0, image.Length, 0, 0x200, "test image");
        carver.SetCustomSignatures(new[]
        {
            new CustomSignatureDefinition
            {
                Name = "WAV RIFF audio",
                Platform = "Common",
                Description = "WAVE marker at RIFF offset 8.",
                HeaderHex = "57 41 56 45",
                HeaderOffset = 8,
                MaxSearchLength = 0x1000,
                Extension = ".wav"
            }
        });

        var row = Assert.Single(carver.Analyze(CancellationToken.None, null));
        Assert.Equal(0x400, row.SourceOffset);
        Assert.Equal("WAV", row.Kind);
        Assert.Equal(0x1000, row.Size);
        Assert.Contains("Common", row.Detail);
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
        var payload = new byte[0xC000];
        var header = payload.AsSpan(0, 0x400);
        Encoding.ASCII.GetBytes("SFBX").CopyTo(header);
        header[4] = 1;
        header[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0x000F);
        WriteXbfsEntry(header, 1, offsetPages: 0x6, sizePages: 0x4);
        WriteXbfsEntry(header, 2, offsetPages: 0xA, sizePages: 0x1);
        WriteXbfsEntry(header, 6, offsetPages: 0xE, sizePages: 0x4);
        Encoding.ASCII.GetBytes("xbfs-test").CopyTo(payload.AsSpan(0x4000));
        WriteXvdHeader(payload, 0x8000, 0x06);
        Encoding.ASCII.GetBytes("""<Package><DisplayName>Dashboard Shell</DisplayName></Package>""").CopyTo(payload.AsSpan(0x9200));
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

    private static int WriteUfsDirent(Span<byte> target, uint inode, ushort recordLength, byte type, string name)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target, inode);
        BinaryPrimitives.WriteUInt16LittleEndian(target[4..], recordLength);
        target[6] = type;
        target[7] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name).CopyTo(target[8..]);
        return recordLength;
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

    private static byte[] CreatePs4OrbisFat16Image()
    {
        const int sectorSize = 512;
        const ulong firstPartitionLba = 0x10;
        const uint partitionSectors = 0x40;
        var image = new byte[(int)((firstPartitionLba + partitionSectors + 1) * sectorSize)];

        var header = image.AsSpan(sectorSize, sectorSize);
        Encoding.ASCII.GetBytes("EFI PART").CopyTo(header);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], 0x80);

        var entry = image.AsSpan(sectorSize * 2, 0x80);
        new Guid(0x6e0c5310, 0x8445, 0x4066, 0xb5, 0x71, 0x9b, 0x65, 0xfd, 0xb7, 0x59, 0x35).TryWriteBytes(entry);
        Guid.Parse("11111111-2222-3333-4444-555555555555").TryWriteBytes(entry[16..]);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], firstPartitionLba);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], firstPartitionLba + partitionSectors);

        var boot = image.AsSpan((int)(firstPartitionLba * sectorSize), sectorSize);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[11..], sectorSize);
        boot[13] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[14..], 1);
        boot[16] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[17..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[19..], (ushort)partitionSectors);
        boot[21] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[22..], 1);
        boot[510] = 0x55;
        boot[511] = 0xAA;
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

    private sealed class FakePlayStationOperations(string deletedJson) : IPlayStationVolumeOperations
    {
        public string FamilyText => "Fake";

        public string ListFilesJson(string imagePath, string keyPath, PlayStationVolume volume)
        {
            return """{"files":[]}""";
        }

        public void CopyFile(string imagePath, string keyPath, PlayStationVolume volume, PlayStationFileEntry entry, string outputPath)
        {
            throw new NotSupportedException();
        }

        public string DecryptToFile(string imagePath, string keyPath, PlayStationVolume volume, string outputPath)
        {
            throw new NotSupportedException();
        }

        public string ListDeletedInodesJson(string imagePath, string keyPath, PlayStationVolume volume)
        {
            return deletedJson;
        }

        public string ExportDeletedInode(string imagePath, string keyPath, PlayStationVolume volume, uint inodeNumber, string outputPath)
        {
            throw new NotSupportedException();
        }
    }
}
