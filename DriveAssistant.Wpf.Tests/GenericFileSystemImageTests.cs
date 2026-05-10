using FATX.Analyzers.Signatures;
using FATXTools.Wpf;
using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
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
    public void Open_Fat16RawVolume_LoadsRootFileAndDeletedEntry()
    {
        using var temp = new TempFile(CreateFat16Image(includeDeletedEntry: true));
        using var image = GenericFileSystemImage.Open(temp.Path);

        var partition = Assert.Single(image.Partitions);
        var volume = Assert.IsType<Fat16Volume>(partition.GenericVolume);
        var entry = Assert.Single(volume.GetRoot());

        Assert.Equal("HELLO.TXT", entry.Name);
        Assert.Equal("FAT16", volume.FamilyText);
        Assert.Equal(5, entry.Length);

        using var output = TempFile.Empty();
        volume.CopyFile(entry, output.Path);
        Assert.Equal(Encoding.ASCII.GetBytes("hello"), File.ReadAllBytes(output.Path));

        var deleted = Assert.Single(volume.ScanDeleted(CancellationToken.None, null));
        Assert.Equal("_LD.BIN", deleted.Name);
        Assert.True(deleted.IsDeleted);
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
    public void PlayStationStorageImage_ManagedPs4Reader_OpensOrbisFatPartitionWithGptEntryIvOffset()
    {
        using var imageFile = new TempFile(CreatePs4OrbisFat16Image(gptEntryIndex: 6, encryptWithIvOffset: true));
        using var keyFile = new TempFile(new byte[32]);

        using var image = PlayStationStorageImage.Open(imageFile.Path, keyFile.Path);

        var volume = Assert.Single(image.Volumes);
        Assert.Equal("eap_vsh", volume.Name);
        Assert.True(volume.IsLoaded);
        Assert.Equal("PlayStation 4 HDD (managed)", volume.FamilyText);
        Assert.Empty(volume.GetRoot());
    }

    [Fact]
    public void PlayStationStorageImage_ManagedPs4Reader_ScansDeletedFatEntries()
    {
        using var imageFile = new TempFile(CreatePs4OrbisFat16Image(includeDeletedEntry: true));
        using var keyFile = TempFile.Empty();

        using var image = PlayStationStorageImage.Open(imageFile.Path, keyFile.Path);

        var volume = Assert.Single(image.Volumes);
        var row = Assert.Single(volume.ScanDeletedInodes());
        Assert.Equal("_ELETED.BIN", row.Name);
        Assert.Equal("Deleted FAT file entry", row.MetadataStatus);
        Assert.True(row.IsDeleted);
        Assert.Equal(1234, row.Size);
        Assert.Equal(0x400, row.Offset);
    }

    [Fact]
    public void PlayStationStorageImage_ManagedPs4Reader_RealImageSmoke_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_PS4_E2E_IMAGE");
        var keyPath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_PS4_E2E_KEY");
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(keyPath))
        {
            return;
        }

        using var image = PlayStationStorageImage.Open(imagePath, keyPath);

        Assert.NotEmpty(image.Volumes);
        var loaded = image.Volumes.Where(volume => volume.IsLoaded).ToList();
        Assert.NotEmpty(loaded);
        Assert.All(loaded, volume => Assert.Equal("PlayStation 4 HDD (managed)", volume.FamilyText));

        var firstFile = loaded
            .SelectMany(volume => WalkPlayStationEntries(volume.GetRoot()))
            .FirstOrDefault(entry => !entry.IsDirectory && entry.Length > 0 && entry.Length <= 8 * 1024 * 1024);
        Assert.NotNull(firstFile);

        using var output = TempFile.Empty();
        firstFile.Volume.CopyFile(firstFile, output.Path);
        Assert.Equal(firstFile.Length, new FileInfo(output.Path).Length);
    }

    [Fact]
    public void PlayStationStorageImage_ManagedPs3Reader_RealImageSmoke_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_PS3_E2E_IMAGE");
        var keyPath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_PS3_E2E_KEY");
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(keyPath))
        {
            return;
        }

        Assert.True(ManagedPs3StorageImage.TryOpen(imagePath, keyPath, out var image, out var error), error);
        using (image)
        {
            Assert.NotEmpty(image.Volumes);
            var loaded = image.Volumes.Where(volume => volume.IsLoaded).ToList();
            Assert.NotEmpty(loaded);
            Assert.All(loaded, volume => Assert.Equal("PlayStation 3 HDD (managed)", volume.FamilyText));
            var devFlash = loaded.FirstOrDefault(volume => volume.Name.StartsWith("dev_flash", StringComparison.OrdinalIgnoreCase));
            if (devFlash != null)
            {
                Assert.NotNull(devFlash.ScanDeletedInodes());
            }
        }
    }

    [Fact]
    public void PlayStationStorageImage_ManagedPs3Reader_KeyRequiredRealImageSmoke_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_PS3_E2E_IMAGE");
        var keyPath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_PS3_E2E_KEY");
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(keyPath))
        {
            return;
        }

        Assert.False(ManagedPs3StorageImage.TryOpen(imagePath, string.Empty, out _, out var noKeyError));
        Assert.Contains("EID root key", noKeyError);

        Assert.True(ManagedPs3StorageImage.TryOpen(imagePath, keyPath, out var image, out var keyedError), keyedError);
        using (image)
        {
            Assert.NotEmpty(image.Volumes);
            Assert.Contains(image.Volumes, volume => volume.IsLoaded);
        }
    }

    [Fact]
    public void PlayStationStorageImage_ManagedPs3Reader_AlreadyDecryptedRealImageSmoke_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_PS3_E2E_IMAGE");
        var keyPath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_PS3_E2E_KEY");
        var decryptedPath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_PS3_E2E_DECRYPTED_IMAGE");
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(keyPath) || string.IsNullOrWhiteSpace(decryptedPath))
        {
            return;
        }

        var sourceLength = new FileInfo(imagePath).Length;
        if (!File.Exists(decryptedPath) || new FileInfo(decryptedPath).Length != sourceLength)
        {
            ManagedPs3StorageImage.CreateDecryptedImage(imagePath, keyPath, decryptedPath, CancellationToken.None);
        }

        Assert.True(ManagedPs3StorageImage.TryOpen(decryptedPath, string.Empty, out var image, out var error), error);
        using (image)
        {
            Assert.NotEmpty(image.Volumes);
            var loaded = image.Volumes.Where(volume => volume.IsLoaded).ToList();
            Assert.NotEmpty(loaded);
            Assert.All(loaded, volume => Assert.Equal("PlayStation 3 HDD (managed)", volume.FamilyText));
        }
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
        Assert.Contains(rows, row => row.Kind == "XVDXML" && row.Detail.Contains("display name: Sample Game"));

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
    public void GenericCarver_DetectsAllKnownXbox360XexVariants()
    {
        var variants = new[] { "XEX0", "XEX?", "XEX-", "XEX%", "XEX1", "XEX2" };
        var image = new byte[0x9000];

        for (var index = 0; index < variants.Length; index++)
        {
            WriteXexHeader(image, index * 0x1000, variants[index], 0x240);
        }

        using var temp = new TempFile(image);
        var carver = new GenericFileCarver(temp.Path, 0, image.Length, 0, 0x1000, "xbox 360 image", ScanProfile.Balanced);
        var rows = carver.Analyze(CancellationToken.None, null);

        foreach (var variant in variants)
        {
            var row = Assert.Single(rows, row => row.Kind == "XEX" && row.Detail.Contains(variant));
            Assert.Equal(0x240, row.Size);
        }
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

        image[0x3000] = 0x7F;
        Encoding.ASCII.GetBytes("CNT").CopyTo(image.AsSpan(0x3001));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x3004), 0x00000001);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(0x3018), 0x1000);

        using var temp = new TempFile(image);
        var carver = new GenericFileCarver(temp.Path, 0, image.Length, 0, 0x1000, "ps4 test");
        var rows = carver.Analyze(CancellationToken.None, null);

        var self = Assert.Single(rows, row => row.Kind == "SELF");
        Assert.Equal(0xC0, self.Size);
        Assert.Contains("PlayStation SELF", self.Detail);

        var pkg = Assert.Single(rows, row => row.Kind == "PKG");
        Assert.Equal(0x2000, pkg.Size);
        Assert.Contains("PS4 package", pkg.Detail);

        var dpkg = Assert.Single(rows, row => row.Kind == "DPKG");
        Assert.Equal(0x1000, dpkg.Size);
        Assert.Contains("PS4 debug package", dpkg.Detail);
    }

    [Fact]
    public void GenericCarver_DetectsNintendoSwitchFormats()
    {
        var image = new byte[0x9000];
        Encoding.ASCII.GetBytes("PFS0").CopyTo(image.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x10), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x18), 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20), 0);
        Encoding.ASCII.GetBytes("a.nca\0").CopyTo(image.AsSpan(0x28));

        Encoding.ASCII.GetBytes("NCA3").CopyTo(image.AsSpan(0x2200));
        Encoding.ASCII.GetBytes("HEAD").CopyTo(image.AsSpan(0x5100));
        Encoding.ASCII.GetBytes("NRO0").CopyTo(image.AsSpan(0x7000));
        Encoding.ASCII.GetBytes("NSO0").CopyTo(image.AsSpan(0x8000));

        using var temp = new TempFile(image);
        var carver = new GenericFileCarver(temp.Path, 0, image.Length, 0, 0x1000, "switch image", ScanProfile.Balanced);
        var rows = carver.Analyze(CancellationToken.None, null);

        Assert.Contains(rows, row => row.Kind == "NSP" && row.Size == 0x50);
        Assert.Contains(rows, row => row.Kind == "NCA");
        Assert.Contains(rows, row => row.Kind == "XCI");
        Assert.Contains(rows, row => row.Kind == "NRO");
        Assert.Contains(rows, row => row.Kind == "NSO");
    }

    [Fact]
    public void GenericCarver_ExpandsSwitchPfs0EntriesAndNcaSections()
    {
        var image = new byte[0x5000];
        Encoding.ASCII.GetBytes("PFS0").CopyTo(image.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 0x20);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x10), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x18), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x28), 0x400);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x30), 0x2000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x38), 0x0A);
        Encoding.ASCII.GetBytes("meta.cnmt\0program.nca\0").CopyTo(image.AsSpan(0x40));
        var headerSize = 0x10 + 2 * 0x18 + 0x20;

        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(headerSize), 0x0102030405060708);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(headerSize + 8), 7);
        image[headerSize + 0x0C] = 0x80;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headerSize + 0x0E), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headerSize + 0x10), 1);

        var ncaOffset = headerSize + 0x400;
        Encoding.ASCII.GetBytes("NCA3").CopyTo(image.AsSpan(ncaOffset + 0x200));
        image[ncaOffset + 0x204] = 0;
        image[ncaOffset + 0x205] = 0;
        image[ncaOffset + 0x206] = 2;
        image[ncaOffset + 0x207] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(ncaOffset + 0x210), 0x0100FF0011223344);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ncaOffset + 0x240), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ncaOffset + 0x244), 4);

        using var temp = new TempFile(image);
        var carver = new GenericFileCarver(temp.Path, 0, image.Length, 0, 0x1000, "switch package", ScanProfile.Balanced);
        var rows = carver.Analyze(CancellationToken.None, null);

        Assert.Contains(rows, row => row.Kind == "NSP" && row.Detail.Contains("2 file entries"));
        Assert.Contains(rows, row => row.Kind == "CNMT" && row.Detail.Contains("title id 0x0102030405060708"));
        Assert.Contains(rows, row => row.Kind == "NCA" && row.Detail.Contains("program/content id 0x0100FF0011223344"));
        Assert.Contains(rows, row => row.Kind == "NCASECTION" && row.Size == 0x400);
    }

    [Fact]
    public void SwitchBisKeySet_LoadsBiskeydumpAndProdKeysForms()
    {
        using var temp = TempFile.Empty();
        File.WriteAllText(temp.Path, """
BIS KEY 3 (crypt) 00112233445566778899AABBCCDDEEFF
BIS KEY 3 (tweak): FFEEDDCCBBAA99887766554433221100
bis_key_02_crypt = 11112222333344445555666677778888
bis_key_02_tweak = 88887777666655554444333322221111
""");

        var keys = SwitchBisKeySet.Load(temp.Path);

        Assert.True(keys.TryGet(3, out _));
        Assert.True(keys.TryGet(2, out _));
    }

    [Fact]
    public void SwitchDeletedContentExtensions_AreTreatedAsFiles()
    {
        var type = typeof(SwitchStorageImage).Assembly.GetType("FATXTools.Wpf.SwitchFat32Volume", throwOnError: true)!;
        var method = type.GetMethod("LooksLikeSwitchContentFile", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.True((bool)method.Invoke(null, new object[] { "3ecb1d9e787e8f6df2728ed8cb94f891.nca" })!);
        Assert.True((bool)method.Invoke(null, new object[] { "base.nsp" })!);
        Assert.False((bool)method.Invoke(null, new object[] { "Nintendo" })!);
    }

    [Fact]
    public void NintendoStorageImage_OpensWiiDiscImageForRawRecovery()
    {
        var image = new byte[0x8000];
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x18), 0x5D1C9EA3);

        using var temp = new TempFile(image);
        using var storage = NintendoStorageImage.Open(temp.Path);

        var partition = Assert.Single(storage.Partitions);
        var volume = Assert.IsType<RawConsoleVolume>(partition.GenericVolume);
        Assert.Equal("Nintendo Wii optical image", volume.FamilyText);
        Assert.Contains("raw Wii disc", partition.Status);
    }

    [Fact]
    public void NintendoStorageImage_ExplicitWiiUAllowsEncryptedRawCandidate()
    {
        var image = new byte[0x8000];
        image[0x2000] = 0xA5;

        using var temp = new TempFile(image);
        using var storage = NintendoStorageImage.Open(temp.Path, allowRawWiiUCandidate: true);

        var partition = Assert.Single(storage.Partitions);
        var volume = Assert.IsType<RawConsoleVolume>(partition.GenericVolume);
        Assert.Equal("Nintendo Wii U WFS", volume.FamilyText);
        Assert.Contains("OTP", partition.Status);
    }

    [Fact]
    public void WiiUKeyMaterial_DerivesUsbKeyFromOtpAndSeeprom()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            var otp = new byte[1024];
            var seeprom = new byte[512];
            for (var i = 0; i < 0x10; i++)
            {
                otp[0x130 + i] = (byte)i;
                otp[0x180 + i] = (byte)(0x80 + i);
                seeprom[0xB0 + i] = (byte)(0x20 + i);
            }

            File.WriteAllBytes(Path.Combine(dir.FullName, "otp.bin"), otp);
            File.WriteAllBytes(Path.Combine(dir.FullName, "seeprom.bin"), seeprom);

            Assert.True(WiiUKeyMaterial.TryLoad(dir.FullName, out var material, out var status), status);
            Assert.NotNull(material);
            Assert.NotNull(material!.UsbKey);
            Assert.Equal(otp.AsSpan(0x180, 0x10).ToArray(), material.MlcKey);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void WiiKeyMaterial_LoadsLocalDirectory()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "common-key"), Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
            File.WriteAllBytes(Path.Combine(dir.FullName, "sd-key"), Enumerable.Range(16, 16).Select(i => (byte)i).ToArray());
            File.WriteAllBytes(Path.Combine(dir.FullName, "sd-iv"), Enumerable.Range(32, 16).Select(i => (byte)i).ToArray());
            File.WriteAllBytes(Path.Combine(dir.FullName, "md5-blanker"), Enumerable.Range(48, 16).Select(i => (byte)i).ToArray());

            Assert.True(WiiKeyMaterial.TryLoad(dir.FullName, out var material, out var status), status);
            Assert.NotNull(material);
            Assert.True(material!.HasCommonKey);
            Assert.True(material.HasSdKeySet);
            Assert.Contains("common key", status);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void WiiKeyMaterial_RealArchiveSmoke_WhenConfigured()
    {
        var keyPath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_WII_KEYS");
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            return;
        }

        Assert.True(WiiKeyMaterial.TryLoad(keyPath, out var material, out var status), status);
        Assert.NotNull(material);
        Assert.True(material!.HasCommonKey);
        Assert.True(material.HasSdKeySet);
    }

    [Fact]
    public void WiiUWfsInspector_RecognizesPlainHeader()
    {
        var image = new byte[0x4000];
        WriteWfsHeader(image.AsSpan(0, 0x1000), deviceType: 0x16A2);

        using var temp = new TempFile(image);
        var info = WiiUWfsInspector.Inspect(temp.Path, null);

        Assert.True(info.IsValid, info.Detail);
        Assert.False(info.IsEncrypted);
        Assert.Equal((ushort)0x16A2, info.DeviceType);
    }

    [Fact]
    public void NintendoStorageImage_OpensPlainWfsImageWithoutKeys()
    {
        var image = new byte[0x4000];
        WriteWfsHeader(image.AsSpan(0, 0x1000), deviceType: 0x16A2);

        using var temp = new TempFile(image);
        using var storage = NintendoStorageImage.Open(temp.Path);

        var partition = Assert.Single(storage.Partitions);
        var volume = Assert.IsType<RawConsoleVolume>(partition.GenericVolume);
        Assert.Equal("Nintendo Wii U WFS", volume.FamilyText);
        Assert.Contains("Plain WFS header detected", partition.Status);
    }

    [Fact]
    public void NintendoStorageImage_OpensNdsRomAndBrowsesNitroFs()
    {
        using var temp = new TempFile(CreateNdsNitroFsImage());
        using var storage = NintendoStorageImage.Open(temp.Path);

        var partition = Assert.Single(storage.Partitions);
        var volume = Assert.IsType<NdsRomVolume>(partition.GenericVolume);
        var entry = Assert.Single(volume.GetRoot());

        Assert.Equal("HELLO.TXT", entry.Name);
        Assert.Equal("Nintendo DS NitroFS", volume.FamilyText);
        Assert.Equal(5, entry.Length);

        using var output = TempFile.Empty();
        volume.CopyFile(entry, output.Path);
        Assert.Equal(Encoding.ASCII.GetBytes("hello"), File.ReadAllBytes(output.Path));
    }

    [Fact]
    public void NintendoStorageImage_Opens3dsNcsdPartitions()
    {
        using var temp = new TempFile(Create3dsNcsdImage());
        using var storage = NintendoStorageImage.Open(temp.Path);

        var partition = Assert.Single(storage.Partitions);
        var volume = Assert.IsType<RawConsoleVolume>(partition.GenericVolume);

        Assert.Equal("NCSD partition 0", partition.Name);
        Assert.Equal("Nintendo 3DS NCSD", volume.FamilyText);
        Assert.Contains("3DS NCSD partition detected", partition.Status);
    }

    [Fact]
    public void NintendoStorageImage_3dsFat16RealImageSmoke_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_3DS_FAT_IMAGE");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        using var storage = NintendoStorageImage.Open(imagePath);
        var partition = Assert.Single(storage.Partitions);
        var volume = Assert.IsType<Fat16Volume>(partition.GenericVolume);

        Assert.NotEmpty(volume.GetRoot());
        Assert.NotNull(volume.ScanDeleted(CancellationToken.None, null));
        var file = Walk(volume.GetRoot()).FirstOrDefault(entry => !entry.IsDirectory && entry.Length > 0 && entry.Length <= 1024 * 1024);
        Assert.NotNull(file);

        using var output = TempFile.Empty();
        volume.CopyFile(file!, output.Path);
        Assert.Equal(file!.Length, new FileInfo(output.Path).Length);
    }

    [Fact]
    public void NintendoStorageImage_3dsNandRealImageSmoke_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_3DS_NAND_IMAGE");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        var keyDirectory = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_3DS_NAND_KEY_DIR");
        using var storage = NintendoStorageImage.Open(imagePath, keyPath: keyDirectory);

        Assert.NotEmpty(storage.Partitions);
        Assert.Contains(storage.Partitions, candidate => candidate.GenericVolume is Fat16Volume or Fat32Volume || candidate.GenericVolume?.FamilyText == "Nintendo 3DS NCSD");
        var partition = storage.Partitions.First(candidate => candidate.GenericVolume is Fat16Volume or Fat32Volume || candidate.GenericVolume?.FamilyText == "Nintendo 3DS NCSD");
        var volume = partition.GenericVolume!;
        Assert.NotNull(volume.ScanDeleted(CancellationToken.None, null));

        if (string.IsNullOrWhiteSpace(keyDirectory))
        {
            return;
        }

        Assert.Equal(16, new FileInfo(Path.Combine(keyDirectory, "nand_cid.mem")).Length);
        Assert.Equal(256, new FileInfo(Path.Combine(keyDirectory, "otp.mem")).Length);
        Assert.Equal(256, new FileInfo(Path.Combine(keyDirectory, "otp_dec.mem")).Length);
        if (string.Equals(Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_3DS_NAND_EXPECT_FAT"), "1", StringComparison.Ordinal))
        {
            Assert.Contains(storage.Partitions, candidate => candidate.GenericVolume is Fat16Volume or Fat32Volume);
        }
    }

    [Fact]
    public void NintendoStorageImage_DsiNandRealImageSmoke_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_DSI_NAND_IMAGE");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        using var storage = NintendoStorageImage.Open(imagePath);

        Assert.NotEmpty(storage.Partitions);
        Assert.Contains(storage.Partitions, partition => partition.GenericVolume is Fat16Volume or Fat32Volume || partition.GenericVolume?.FamilyText == "Nintendo DSi NAND");
        var volume = storage.Partitions.First(partition => partition.GenericVolume is Fat16Volume or Fat32Volume || partition.GenericVolume?.FamilyText == "Nintendo DSi NAND").GenericVolume!;
        Assert.NotNull(volume.ScanDeleted(CancellationToken.None, null));

        var carver = new GenericFileCarver(imagePath, volume.Offset, Math.Min(volume.Length, 64L * 1024 * 1024), volume.Offset, 0x1000, volume.Name, ScanProfile.Fast);
        Assert.NotNull(carver.Analyze(CancellationToken.None, null));
    }

    [Fact]
    public void NintendoStorageImage_DsiNandFooterMountsFat_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_DSI_NAND_IMAGE");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        var partitions = new List<PartitionModel>();
        var temporaryPaths = new List<string>();
        try
        {
            Assert.True(NintendoNandCrypto.TryOpenDsiNand(imagePath, partitions, temporaryPaths, out var status), status);
            Assert.Contains(partitions, partition => partition.GenericVolume is Fat16Volume or Fat32Volume);
        }
        finally
        {
            foreach (var path in temporaryPaths)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public void NintendoStorageImage_WiiUDevKitHddZipSmoke_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_WIIU_DEV_HDD_IMAGE");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        using var storage = NintendoStorageImage.Open(imagePath, allowRawWiiUCandidate: true);

        Assert.NotEmpty(storage.Partitions);
        Assert.Contains(storage.Partitions, partition => partition.GenericVolume?.FamilyText == "Nintendo Wii U WFS");
        Assert.Contains(storage.Partitions, partition => partition.Status.Contains("Wii U", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NintendoStorageImage_WiiUMlcRealImageSmoke_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_WIIU_MLC_IMAGE");
        var keyPath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_WIIU_KEYS");
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(keyPath))
        {
            return;
        }

        using var storage = NintendoStorageImage.Open(imagePath, keyPath: keyPath);
        var partition = Assert.Single(storage.Partitions);
        var volume = Assert.IsType<WiiUWfsVolume>(partition.GenericVolume);

        Assert.True(volume.HasMlcKey);
        Assert.Contains(volume.GetRoot(), entry => entry.Name.Equals("sys", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(volume.GetRoot(), entry => entry.Name.Equals("usr", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(volume.ScanDeleted(CancellationToken.None, null));

        var carver = new GenericFileCarver(imagePath, volume.Offset, Math.Min(volume.Length, 128L * 1024 * 1024), volume.Offset, 0x1000, volume.Name, ScanProfile.Fast);
        Assert.NotNull(carver.Analyze(CancellationToken.None, null));

        var file = Walk(volume.GetRoot())
            .FirstOrDefault(entry => !entry.IsDirectory && entry.Length > 0 && entry.Length <= 1024 * 1024);
        Assert.NotNull(file);

        using var output = TempFile.Empty();
        volume.CopyFile(file!, output.Path);
        Assert.Equal(file!.Length, new FileInfo(output.Path).Length);
    }

    [Fact]
    public void NintendoStorageImage_WiiRvtHImageSmoke_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_WII_RVTH_IMAGE");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        using var storage = NintendoStorageImage.Open(imagePath);

        Assert.NotEmpty(storage.Partitions);
        Assert.Contains(storage.Partitions, partition => partition.GenericVolume?.FamilyText == "Nintendo Wii RVT-H");
    }

    [Fact]
    public void NintendoStorageImage_WiiNandRealImageSmoke_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_WII_NAND_IMAGE");
        var keyPath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_WII_NAND_KEYS");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        using var storage = NintendoStorageImage.Open(imagePath, keyPath: keyPath);
        var partition = Assert.Single(storage.Partitions);
        var volume = Assert.IsType<WiiNandVolume>(partition.GenericVolume);

        Assert.True(volume.HasNandKey);
        Assert.NotEmpty(volume.GetRoot());
        Assert.Contains(volume.GetRoot(), entry => entry.Name.Equals("title", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(volume.ScanDeleted(CancellationToken.None, null));
        var carver = new GenericFileCarver(imagePath, volume.Offset, Math.Min(volume.Length, 128L * 1024 * 1024), volume.Offset, 0x1000, volume.Name, ScanProfile.Fast);
        Assert.NotNull(carver.Analyze(CancellationToken.None, null));

        var sysconf = Walk(volume.GetRoot()).FirstOrDefault(entry => entry.Path.Equals("/shared2/sys/SYSCONF", StringComparison.OrdinalIgnoreCase));
        if (sysconf != null)
        {
            using var output = TempFile.Empty();
            volume.CopyFile(sysconf, output.Path);
            var bytes = File.ReadAllBytes(output.Path);
            Assert.Equal(sysconf.Length, bytes.Length);
            Assert.Contains("IPL.", Encoding.ASCII.GetString(bytes));
        }
        else
        {
            var file = Walk(volume.GetRoot()).First(entry => !entry.IsDirectory && entry.Length > 0 && entry.Length <= 1024 * 1024);
            using var output = TempFile.Empty();
            volume.CopyFile(file, output.Path);
            Assert.Equal(file.Length, new FileInfo(output.Path).Length);
        }
    }

    [Fact]
    public void Ps2StorageImage_OpensApaPartitionTable()
    {
        using var temp = new TempFile(CreatePs2ApaImage());
        using var storage = Ps2StorageImage.Open(temp.Path);

        Assert.NotEmpty(storage.Partitions);
        Assert.Contains(storage.Partitions, partition => partition.Name == "__mbr");
        Assert.Contains(storage.Partitions, partition => partition.Name == "PP.TEST");
    }

    [Fact]
    public void Ps2StorageImage_RealImageMountsPfsAndRunsRecoveryScans_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_PS2_E2E_IMAGE");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        using var storage = Ps2StorageImage.Open(imagePath);
        var pfsPartitions = storage.Partitions
            .Where(partition => partition.GenericVolume?.FamilyText == "PlayStation 2 APA/PFS")
            .ToList();

        Assert.NotEmpty(pfsPartitions);
        Assert.Contains(pfsPartitions, partition => partition.GenericVolume!.GetRoot().Count > 0);

        var mounted = pfsPartitions.First(partition => partition.GenericVolume!.GetRoot().Count > 0).GenericVolume!;
        var deletedRows = mounted.ScanDeleted(CancellationToken.None, null);
        Assert.NotNull(deletedRows);

        var carver = new GenericFileCarver(
            imagePath,
            mounted.Offset,
            Math.Min(mounted.Length, 256L * 1024 * 1024),
            mounted.Offset,
            0x100000,
            mounted.Name,
            ScanProfile.Fast);
        var carvedRows = carver.Analyze(CancellationToken.None, null);
        Assert.NotNull(carvedRows);
    }

    [Fact]
    public void GenericCarver_DetectsWiiWiiUHandheldAndPs2StorageMarkers()
    {
        var image = new byte[0xD000];
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x18), 0x5D1C9EA3);
        Encoding.ASCII.GetBytes("WBFS").CopyTo(image.AsSpan(0x2000));
        Encoding.ASCII.GetBytes("WFS").CopyTo(image.AsSpan(0x4000));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x6004), 0x00415041);
        CreateNdsNitroFsImage().CopyTo(image.AsSpan(0x7000));
        Encoding.ASCII.GetBytes("NCSD").CopyTo(image.AsSpan(0xA100));
        Encoding.ASCII.GetBytes("NCCH").CopyTo(image.AsSpan(0xC100));

        using var temp = new TempFile(image);
        var carver = new GenericFileCarver(temp.Path, 0, image.Length, 0, 0x1000, "console image", ScanProfile.Balanced);
        var rows = carver.Analyze(CancellationToken.None, null);

        Assert.Contains(rows, row => row.Kind == "ISO" && row.Detail.Contains("Nintendo Wii"));
        Assert.Contains(rows, row => row.Kind == "WBFS");
        Assert.Contains(rows, row => row.Kind == "WFS");
        Assert.Contains(rows, row => row.Kind == "NDS");
        Assert.Contains(rows, row => row.Kind == "3DS");
        Assert.Contains(rows, row => row.Kind == "CXI");
        Assert.Contains(rows, row => row.Kind == "PS2HDD");
    }

    [Fact]
    public void LegacyConsoleStorageImage_OpenRecognizesDevkitAndSaveMedia()
    {
        var image = new byte[128 * 1024];
        image[0] = (byte)'M';
        image[1] = (byte)'C';

        using var temp = new TempFile(image);
        var memoryCardPath = Path.ChangeExtension(temp.Path, ".mcr");
        File.Copy(temp.Path, memoryCardPath, overwrite: true);
        try
        {
            using var storage = LegacyConsoleStorageImage.Open(memoryCardPath);
            var partition = Assert.Single(storage.Partitions);
            var volume = Assert.IsType<RawConsoleVolume>(partition.GenericVolume);

            Assert.Equal("Sony PlayStation memory card", volume.FamilyText);
            Assert.Single(volume.GetRoot());
        }
        finally
        {
            File.Delete(memoryCardPath);
        }
    }

    [Fact]
    public void GenericCarver_DetectsLegacyDevkitMediaMarkers()
    {
        var image = new byte[0x25000];
        image[0] = (byte)'M';
        image[1] = (byte)'C';
        Encoding.ASCII.GetBytes("SEGA SEGAKATANA ").CopyTo(image.AsSpan(0x20000));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x21000), 0x80371240);
        CreateGameBoyRomHeader().CopyTo(image.AsSpan(0x22000));

        using var temp = new TempFile(image);
        var carver = new GenericFileCarver(temp.Path, 0, image.Length, 0, 0x1000, "legacy devkit media", ScanProfile.Balanced);
        var rows = carver.Analyze(CancellationToken.None, null);

        Assert.Contains(rows, row => row.Kind == "MCR" && row.Detail.Contains("PlayStation"));
        Assert.Contains(rows, row => row.Kind == "BIN" && row.Detail.Contains("Katana"));
        Assert.Contains(rows, row => row.Kind == "Z64");
        Assert.Contains(rows, row => row.Kind == "GB");
    }

    [Fact]
    public void SwitchStorageImage_RealNandSmoke_WhenConfigured()
    {
        var imagePath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_SWITCH_E2E_IMAGE");
        var keyPath = Environment.GetEnvironmentVariable("DRIVE_ASSISTANT_SWITCH_E2E_KEYS");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        using var image = SwitchStorageImage.Open(imagePath, keyPath);

        Assert.NotEmpty(image.Partitions);
        Assert.Contains(image.Partitions, partition => partition.Name.Equals("SAFE", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            foreach (var name in new[] { "SAFE", "SYSTEM", "USER" })
            {
                var partition = image.Partitions.Single(partition => partition.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                Assert.IsType<SwitchFat32Volume>(partition.GenericVolume);
                Assert.Contains("Mounted", partition.Status);
            }
        }
    }

    [Fact]
    public void GenericCarver_AddsPredictedPs4PkgFragmentWindows()
    {
        var addFragments = typeof(GenericFileCarver).GetMethod("AddPs4PkgFragmentRows", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rows = new System.Collections.Generic.List<GenericCarvedFile>();
        var package = new GenericCarvedFile(
            "carved_0000000000000000.pkg",
            "PKG",
            "image.img",
            0x1000,
            0x2000,
            0x56400000,
            "ps4 image",
            "PS4 package");

        addFragments.Invoke(null, [rows, package, CancellationToken.None]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("PKGFRAG", row.Kind));
        Assert.Equal(0x28001000, rows[0].SourceOffset);
        Assert.Equal(0x28002000, rows[0].DisplayOffset);
        Assert.Equal(0x6400000, rows[0].Size);
        Assert.Equal(0x50001000, rows[1].SourceOffset);
        Assert.Contains("Ubisoft", rows[0].Detail);
    }

    [Fact]
    public void LegacyFatxToolsJson_ImportsMetadataClustersAndCarverOffsets()
    {
        var timestamp = PackFatTimestamp(2024, 5, 8, 17, 40, 14);
        var json = $$"""
            {
              "Version": 1,
              "Drive": {
                "FileName": "legacy.img",
                "Partitions": [
                  {
                    "Name": "Data",
                    "Offset": 4096,
                    "Length": 1048576,
                    "Analysis": {
                      "MetadataAnalyzer": [
                        {
                          "Cluster": 2,
                          "Offset": 8192,
                          "FileNameLength": 3,
                          "FileAttributes": 16,
                          "FileName": "DIR",
                          "FileNameBytes": "RElS",
                          "FirstCluster": 2,
                          "FileSize": 0,
                          "CreationTime": {{timestamp}},
                          "LastWriteTime": {{timestamp}},
                          "LastAccessTime": {{timestamp}},
                          "Children": [
                            {
                              "Cluster": 3,
                              "Offset": 8256,
                              "FileNameLength": 8,
                              "FileAttributes": 32,
                              "FileName": "FILE.BIN",
                              "FileNameBytes": "RklMRS5CSU4=",
                              "FirstCluster": 3,
                              "FileSize": 512,
                              "CreationTime": {{timestamp}},
                              "LastWriteTime": {{timestamp}},
                              "LastAccessTime": {{timestamp}},
                              "Clusters": [3, 4, 7]
                            }
                          ],
                          "Clusters": [2]
                        }
                      ],
                      "FileCarver": [
                        {
                          "Offset": 4660,
                          "Name": "found.xex",
                          "Size": 1024
                        }
                      ]
                    }
                  }
                ]
              }
            }
            """;

        using var temp = new TempFile(Encoding.UTF8.GetBytes(json));
        var load = typeof(MainWindow).GetMethod("LoadDatabaseSnapshotFromFile", BindingFlags.NonPublic | BindingFlags.Static)!;
        var snapshot = Assert.IsType<DriveDatabaseSnapshot>(load.Invoke(null, [temp.Path]));

        var partition = Assert.Single(snapshot.Partitions);
        Assert.Equal("FATX", partition.Family);
        Assert.Equal(4096, partition.Offset);

        var root = Assert.Single(partition.Analysis.MetadataAnalyzer);
        Assert.Equal("DIR", root.Name);
        Assert.Equal("DIR", root.Path);
        Assert.Equal(2024, root.Created.Year);

        var child = Assert.Single(root.Children);
        Assert.Equal("DIR/FILE.BIN", child.Path);
        Assert.Equal("3-4, 7", child.Extents);
        Assert.Equal(512, child.Size);

        var carved = Assert.Single(partition.Analysis.FileCarver);
        Assert.Equal("found.xex", carved.Name);
        Assert.Equal("XEX", carved.Kind);
        Assert.Equal(0x1234, carved.SourceOffset);
        Assert.Contains("file-area offset 0x1234", carved.Detail);
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

    private static byte[] CreateFat16Image(bool includeDeletedEntry = false)
    {
        var image = new byte[16 * 512];
        var boot = image.AsSpan(0, 512);
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[11..], 512);
        boot[13] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[14..], 1);
        boot[16] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[17..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[19..], 16);
        boot[21] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[22..], 1);
        Encoding.ASCII.GetBytes("FAT16   ").CopyTo(boot[54..]);
        boot[510] = 0x55;
        boot[511] = 0xAA;

        var fat = image.AsSpan(512, 512);
        BinaryPrimitives.WriteUInt16LittleEndian(fat[0..], 0xFFF8);
        BinaryPrimitives.WriteUInt16LittleEndian(fat[2..], 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(fat[4..], 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(fat[6..], 0xFFFF);

        var root = image.AsSpan(1024, 512);
        var entry = root[..32];
        Encoding.ASCII.GetBytes("HELLO   TXT").CopyTo(entry);
        entry[11] = 0x20;
        BinaryPrimitives.WriteUInt16LittleEndian(entry[26..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], 5);
        Encoding.ASCII.GetBytes("hello").CopyTo(image.AsSpan(1536, 5));

        if (includeDeletedEntry)
        {
            var deleted = root.Slice(32, 32);
            Encoding.ASCII.GetBytes("OLD     BIN").CopyTo(deleted);
            deleted[0] = 0xE5;
            deleted[11] = 0x20;
            BinaryPrimitives.WriteUInt16LittleEndian(deleted[26..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(deleted[28..], 4);
            Encoding.ASCII.GetBytes("old!").CopyTo(image.AsSpan(2048, 4));
        }

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

    private static void WriteXexHeader(byte[] image, int offset, string magic, uint fileSize)
    {
        Encoding.ASCII.GetBytes(magic).CopyTo(image.AsSpan(offset));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset + 0x10), 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset + 0x14), 0);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(offset + 0x24), fileSize);
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

    private static byte[] CreatePs2ApaImage()
    {
        var image = new byte[0x40000];
        WritePs2ApaHeader(image.AsSpan(0), "__mbr", startSector: 0, lengthSectors: 0x80, nextSector: 0x80, type: 0);
        WritePs2ApaHeader(image.AsSpan(0x80 * 512), "PP.TEST", startSector: 0x80, lengthSectors: 0x100, nextSector: 0, type: 0x0100);
        return image;
    }

    private static byte[] CreateNdsNitroFsImage()
    {
        var image = new byte[0x1000];
        Encoding.ASCII.GetBytes("TEST ROM    ").CopyTo(image.AsSpan(0x00));
        Encoding.ASCII.GetBytes("ABCD").CopyTo(image.AsSpan(0x0C));
        Encoding.ASCII.GetBytes("01").CopyTo(image.AsSpan(0x10));

        const uint fntOffset = 0x200;
        const uint fntSize = 0x20;
        const uint fatOffset = 0x300;
        const uint fatSize = 0x08;
        const uint dataOffset = 0x400;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x40), fntOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x44), fntSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x48), fatOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x4C), fatSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x80), (uint)image.Length);

        var fnt = image.AsSpan((int)fntOffset, (int)fntSize);
        BinaryPrimitives.WriteUInt32LittleEndian(fnt[0..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(fnt[4..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(fnt[6..], 1);
        fnt[8] = 9;
        Encoding.ASCII.GetBytes("HELLO.TXT").CopyTo(fnt[9..]);
        fnt[18] = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)fatOffset), dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)fatOffset + 4), dataOffset + 5);
        Encoding.ASCII.GetBytes("hello").CopyTo(image.AsSpan((int)dataOffset));
        return image;
    }

    private static byte[] Create3dsNcsdImage()
    {
        var image = new byte[0x5000];
        Encoding.ASCII.GetBytes("NCSD").CopyTo(image.AsSpan(0x100));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x120), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x124), 0x10);
        Encoding.ASCII.GetBytes("NCCH").CopyTo(image.AsSpan(0x2100));
        return image;
    }

    private static void WritePs2ApaHeader(Span<byte> header, string id, uint startSector, uint lengthSectors, uint nextSector, ushort type)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 0x00415041);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], nextSector);
        Encoding.ASCII.GetBytes(id).CopyTo(header[16..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[64..], startSector);
        BinaryPrimitives.WriteUInt32LittleEndian(header[68..], lengthSectors);
        BinaryPrimitives.WriteUInt16LittleEndian(header[72..], type);
    }

    private static void WriteWfsHeader(Span<byte> block, ushort deviceType)
    {
        BinaryPrimitives.WriteUInt32BigEndian(block[0..], 0x00C00000);
        BinaryPrimitives.WriteUInt32BigEndian(block[0x18..], 0x12345678);
        BinaryPrimitives.WriteUInt32BigEndian(block[0x1C..], 0x01010800);
        BinaryPrimitives.WriteUInt16BigEndian(block[0x20..], deviceType);
        BinaryPrimitives.WriteUInt32BigEndian(block[0x28..], 0xE0000000);
        BinaryPrimitives.WriteUInt32BigEndian(block[0x3C..], 1);
    }

    private static byte[] CreatePs4OrbisFat16Image(int gptEntryIndex = 0, bool encryptWithIvOffset = false, bool includeDeletedEntry = false)
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

        var entry = image.AsSpan(sectorSize * 2 + gptEntryIndex * 0x80, 0x80);
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
        Encoding.ASCII.GetBytes("FAT16   ").CopyTo(boot[54..]);
        boot[510] = 0x55;
        boot[511] = 0xAA;
        if (includeDeletedEntry)
        {
            var root = image.AsSpan((int)((firstPartitionLba + 2) * sectorSize), sectorSize);
            Encoding.ASCII.GetBytes("DELETED BIN").CopyTo(root);
            root[0] = 0xE5;
            root[11] = 0x20;
            BinaryPrimitives.WriteUInt16LittleEndian(root[26..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(root[28..], 1234);
        }

        if (encryptWithIvOffset)
        {
            EncryptXtsPartition(image.AsSpan((int)(firstPartitionLba * sectorSize), (int)(partitionSectors * sectorSize)), (ulong)gptEntryIndex << 32, new byte[16], new byte[16]);
        }

        return image;
    }

    private static void EncryptXtsPartition(Span<byte> data, ulong sectorBase, byte[] dataKey, byte[] tweakKey)
    {
        using var dataAes = CreateAes(dataKey);
        using var tweakAes = CreateAes(tweakKey);
        using var dataEncryptor = dataAes.CreateEncryptor();
        using var tweakEncryptor = tweakAes.CreateEncryptor();
        Span<byte> block = stackalloc byte[16];
        Span<byte> tweak = stackalloc byte[16];
        for (var sectorOffset = 0; sectorOffset + 512 <= data.Length; sectorOffset += 512)
        {
            tweak.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(tweak, sectorBase + (ulong)(sectorOffset / 512));
            TransformBlock(tweakEncryptor, tweak);
            var sector = data.Slice(sectorOffset, 512);
            for (var offset = 0; offset < sector.Length; offset += 16)
            {
                for (var index = 0; index < 16; index++)
                {
                    block[index] = (byte)(sector[offset + index] ^ tweak[index]);
                }

                TransformBlock(dataEncryptor, block);
                for (var index = 0; index < 16; index++)
                {
                    sector[offset + index] = (byte)(block[index] ^ tweak[index]);
                }

                MultiplyTweak(tweak);
            }
        }
    }

    private static Aes CreateAes(byte[] key)
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        return aes;
    }

    private static void TransformBlock(ICryptoTransform transform, Span<byte> block)
    {
        var input = block.ToArray();
        var output = new byte[16];
        transform.TransformBlock(input, 0, input.Length, output, 0);
        output.CopyTo(block);
    }

    private static void MultiplyTweak(Span<byte> tweak)
    {
        var carryIn = 0;
        var carryOut = 0;
        for (var index = 0; index < tweak.Length; index++)
        {
            carryOut = (tweak[index] >> 7) & 1;
            tweak[index] = (byte)(((tweak[index] << 1) + carryIn) & 0xFF);
            carryIn = carryOut;
        }

        if (carryOut != 0)
        {
            tweak[0] ^= 0x87;
        }
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

    private static uint PackFatTimestamp(int year, int month, int day, int hour, int minute, int second)
    {
        return (uint)(((year - 1980) << 25)
                      | (month << 21)
                      | (day << 16)
                      | (hour << 11)
                      | (minute << 5)
                      | (second / 2));
    }

    private static byte[] CreateGameBoyRomHeader()
    {
        var header = new byte[0x200];
        byte[] logo =
        [
            0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83,
            0x00, 0x0C, 0x00, 0x0D, 0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E,
            0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99, 0xBB, 0xBB, 0x67, 0x63,
            0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E
        ];
        logo.CopyTo(header.AsSpan(0x104));
        Encoding.ASCII.GetBytes("DEVKIT").CopyTo(header.AsSpan(0x134));
        return header;
    }

    private static T GetProperty<T>(object instance, string name)
    {
        return (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
    }

    private static IEnumerable<PlayStationFileEntry> WalkPlayStationEntries(IEnumerable<PlayStationFileEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in WalkPlayStationEntries(entry.Children))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<GenericFileSystemEntry> Walk(IEnumerable<GenericFileSystemEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Walk(entry.Children))
            {
                yield return child;
            }
        }
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
