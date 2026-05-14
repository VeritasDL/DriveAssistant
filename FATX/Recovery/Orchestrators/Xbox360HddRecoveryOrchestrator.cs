using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.Json;
using FATX.Recovery.Models;
using FATX.Recovery.Builders;

namespace FATX.Recovery.Orchestrators
{
    /// <summary>
    /// Orchestrates the complete Xbox 360 HDD recovery process (Steps 0-3 plus file copying).
    /// </summary>
    public class Xbox360HddRecoveryOrchestrator
    {
        private readonly Xbox360HddRecoveryConfig _config;
        private HddJsonParser.HddJsonData? _hddData;
        private FileRecoveryMatcher? _matcher;
        private uint[]? _fileAllocationTable;
        private uint _bytesPerCluster;
        private long _fileAreaByteOffset;
        private Dictionary<string, uint>? _fileToFirstClusterMap;

        /// <summary>
        /// Event raised for status updates.
        /// </summary>
        public event Action<string>? StatusChanged;

        /// <summary>
        /// Event raised for progress updates (0-100).
        /// </summary>
        public event Action<int>? ProgressChanged;

        /// <summary>
        /// Event raised for log messages.
        /// </summary>
        public event Action<string>? LogMessage;

        public Xbox360HddRecoveryOrchestrator(Xbox360HddRecoveryConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _config.Validate();
        }

        /// <summary>
        /// Executes the complete recovery pipeline.
        /// </summary>
        public bool Execute(CancellationToken cancellationToken = default)
        {
            try
            {
                Log("=== Xbox 360 HDD Recovery Started ===");
                Log($"Output: {_config.OutputImagePath}");
                UpdateProgress(0);

                // Step 0: Load configuration (already done via constructor)
                UpdateStatus("Step 0/6: Configuration loaded");
                Log($"✓ Recovered files: {_config.RecoveredFilesPath}");
                Log($"✓ Deleted files: {_config.DeletedFilesPath}");
                UpdateProgress(10);

                // Step 1: Load and parse HDD JSON
                cancellationToken.ThrowIfCancellationRequested();
                UpdateStatus("Step 1/6: Loading HDD metadata");
                if (!LoadHddJson(cancellationToken))
                    return false;
                UpdateProgress(20);

                // Step 2: Initialize file matcher
                cancellationToken.ThrowIfCancellationRequested();
                UpdateStatus("Step 2/6: Indexing recovery files");
                if (!InitializeFileMatcher(cancellationToken))
                    return false;
                UpdateProgress(30);

                // Step 3: Create devkit superblock and image structure
                cancellationToken.ThrowIfCancellationRequested();
                UpdateStatus("Step 3/6: Creating HDD image structure");
                if (!CreateImageStructure(cancellationToken))
                    return false;
                UpdateProgress(50);

                // Step 4: Write directory entries (dirents)
                cancellationToken.ThrowIfCancellationRequested();
                UpdateStatus("Step 4/6: Writing directory entries");
                if (!WriteDirentStream(cancellationToken))
                    return false;
                UpdateProgress(70);

                // Step 5: Copy file contents
                cancellationToken.ThrowIfCancellationRequested();
                UpdateStatus("Step 5/6: Copying file contents");
                if (!CopyFileContents(cancellationToken))
                    return false;
                UpdateProgress(85);

                // Step 6: Finalize and verify
                cancellationToken.ThrowIfCancellationRequested();
                UpdateStatus("Step 6/6: Finalizing image");
                if (!FinalizeImage(cancellationToken))
                    return false;
                UpdateProgress(100);

                Log("=== Xbox 360 HDD Recovery Completed Successfully ===");
                UpdateStatus("Recovery completed");
                return true;
            }
            catch (OperationCanceledException)
            {
                Log("⚠ Recovery cancelled by user");
                UpdateStatus("Cancelled");
                return false;
            }
            catch (Exception ex)
            {
                Log($"✗ Error: {ex.Message}");
                UpdateStatus("Failed");
                return false;
            }
        }

        private bool LoadHddJson(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var json = File.ReadAllText(_config.HddJsonPath);
                _hddData = JsonSerializer.Deserialize<HddJsonParser.HddJsonData>(json);

                if (_hddData == null)
                {
                    Log("✗ Failed to parse HDD JSON");
                    return false;
                }

                Log($"✓ Loaded HDD metadata: {_hddData.Partitions?.Count ?? 0} partition(s)");
                return true;
            }
            catch (Exception ex)
            {
                Log($"✗ JSON load failed: {ex.Message}");
                return false;
            }
        }

        private bool InitializeFileMatcher(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _matcher = new FileRecoveryMatcher(_config.RecoveredFilesPath, _config.DeletedFilesPath, enableFuzzy: _config.EnableFuzzyMatching);
                Log($"✓ File matcher initialized");
                return true;
            }
            catch (Exception ex)
            {
                Log($"✗ Matcher initialization failed: {ex.Message}");
                return false;
            }
        }

        private bool CreateImageStructure(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_hddData?.Partitions == null || _hddData.Partitions.Count == 0)
                {
                    Log("✗ No partitions found in HDD metadata");
                    return false;
                }

                var partition = _hddData.Partitions[0];
                var partitionSize = partition.Length;

                // Calculate cluster size
                _bytesPerCluster = HddStructureBuilders.CalculateBytesPerCluster(partitionSize);
                Log($"✓ Calculated cluster size: {_bytesPerCluster} bytes");

                // Create devkit superblock
                var devkitHeader = HddStructureBuilders.CreateDevkitSuperblock(
                    partitionCount: 1,
                    partitionOffset: 0x10000,
                    partitionLength: partitionSize
                );
                Log($"✓ Created devkit superblock ({devkitHeader.Length} bytes)");

                // Create FATX partition header
                var fatxHeader = HddStructureBuilders.CreateFatxPartitionHeader(
                    serialNumber: _config.SerialNumber != 0 ? _config.SerialNumber : (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    bytesPerCluster: _bytesPerCluster,
                    rootDirFirstCluster: 0
                );
                Log($"✓ Created FATX partition header ({fatxHeader.Length} bytes)");

                // Calculate offsets
                var fatxHeaderSize = 0x1000;
                var fatTableSize = (uint)((partitionSize / _bytesPerCluster) * sizeof(uint));
                var fatTableSize_aligned = ((fatTableSize + 0xFFF) / 0x1000) * 0x1000;
                _fileAreaByteOffset = 0x10000 + fatxHeaderSize + fatTableSize_aligned;

                Log($"✓ FAT table size: {fatTableSize} bytes (aligned: {fatTableSize_aligned})");
                Log($"✓ File area offset: 0x{_fileAreaByteOffset:X}");

                // Initialize FAT table (will be filled during dirent writing)
                var maxClusters = (uint)(partitionSize / _bytesPerCluster);
                _fileAllocationTable = Enumerable.Repeat(0xFFFFFFFEu, (int)maxClusters).ToArray();
                _fileToFirstClusterMap = new Dictionary<string, uint>();

                // Write to image file
                using (var fs = File.Create(_config.OutputImagePath))
                {
                    fs.Write(devkitHeader, 0, devkitHeader.Length);
                    fs.Position = 0x10000;
                    fs.Write(fatxHeader, 0, fatxHeader.Length);
                    fs.Position = 0x10000 + fatxHeaderSize;
                    
                    // Write FAT table
                    var fatBytes = new byte[fatTableSize_aligned];
                    Buffer.BlockCopy(_fileAllocationTable, 0, fatBytes, 0, (int)fatTableSize);
                    fs.Write(fatBytes, 0, fatBytes.Length);

                    // Extend file to partition size
                    fs.SetLength(partitionSize);
                }

                Log($"✓ Image structure created: {_config.OutputImagePath}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"✗ Image structure creation failed: {ex.Message}");
                return false;
            }
        }

        private bool WriteDirentStream(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_hddData?.Partitions == null || _hddData.Partitions.Count == 0)
                {
                    Log("✗ No partition data available");
                    return false;
                }

                var partition = _hddData.Partitions[0];
                var entries = FlattenEntries(partition.OriginalFilesystem ?? new List<HddJsonParser.SnapshotFileEntry>());

                Log($"✓ Processing {entries.Count} file system entries");

                uint nextCluster = 1; // Cluster 0 is reserved
                var direntBuilder = new DirentBuilder();
                var dirents = new List<byte[]>();

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_matcher?.TryFindFile(entry.Path ?? entry.Name) == true || !entry.IsDeleted)
                    {
                        var dirent = direntBuilder.CreateDirent(
                            fileName: entry.Name,
                            fileSize: Math.Max(0, entry.Size),
                            firstCluster: nextCluster,
                            isDirectory: entry.IsDirectory,
                            isDeleted: entry.IsDeleted
                        );

                        dirents.Add(dirent);

                        if (!entry.IsDirectory && entry.Size > 0)
                        {
                            var clustersNeeded = (uint)((entry.Size + _bytesPerCluster - 1) / _bytesPerCluster);
                            _fileToFirstClusterMap![entry.Name] = nextCluster;

                            // Build FAT chain for this file
                            for (uint i = 0; i < clustersNeeded; i++)
                            {
                                if (nextCluster + i < _fileAllocationTable!.Length - 1)
                                    _fileAllocationTable[nextCluster + i] = nextCluster + i + 1;
                                else if (nextCluster + i < _fileAllocationTable.Length)
                                    _fileAllocationTable[nextCluster + i] = 0xFFFFFFF8; // End of chain
                            }

                            nextCluster += clustersNeeded;
                        }
                        else if (entry.IsDirectory)
                        {
                            _fileToFirstClusterMap![entry.Name] = nextCluster++;
                        }
                    }
                    else if (_config.IncludeDeletedFiles)
                    {
                        // Create placeholder dirent for missing file
                        var dirent = direntBuilder.CreateDirent(
                            fileName: entry.Name,
                            fileSize: Math.Max(0, entry.Size),
                            firstCluster: 0,
                            isDirectory: entry.IsDirectory,
                            isDeleted: true
                        );
                        dirents.Add(dirent);
                        Log($"⚠ Placeholder created for missing: {entry.Name}");
                    }
                }

                // Write dirents to image
                using (var fs = new FileStream(_config.OutputImagePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    var direntOffset = _fileAreaByteOffset;
                    foreach (var dirent in dirents)
                    {
                        fs.Seek(direntOffset, SeekOrigin.Begin);
                        fs.Write(dirent, 0, dirent.Length);
                        direntOffset += dirent.Length;
                    }
                }

                // Update FAT table in image
                using (var fs = new FileStream(_config.OutputImagePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.Seek(0x10000 + 0x1000, SeekOrigin.Begin);
                    var fatBuffer = new byte[_fileAllocationTable!.Length * sizeof(uint)];
                    Buffer.BlockCopy(_fileAllocationTable, 0, fatBuffer, 0, fatBuffer.Length);
                    fs.Write(fatBuffer, 0, fatBuffer.Length);
                }

                Log($"✓ Wrote {dirents.Count} directory entries");
                return true;
            }
            catch (Exception ex)
            {
                Log($"✗ Dirent write failed: {ex.Message}");
                return false;
            }
        }

        private bool CopyFileContents(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_hddData?.Partitions == null || _fileToFirstClusterMap == null)
                {
                    Log("✗ No file mapping available");
                    return false;
                }

                var copier = new FileContentCopier(_config.OutputImagePath, _bytesPerCluster, _fileAreaByteOffset, _fileAllocationTable!);
                copier.FileCopyProgress += (name, copied, total) =>
                {
                    Log($"  {name}: {copied}/{total} bytes");
                };
                copier.FileCopyFailed += (name, ex) =>
                {
                    Log($"✗ Failed to copy {name}: {ex.Message}");
                };

                var filesToCopy = CollectFilesToCopy(_hddData.Partitions[0].OriginalFilesystem ?? new List<HddJsonParser.SnapshotFileEntry>());
                var successCount = 0;

                foreach (var (filePath, fileName, size) in filesToCopy)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_fileToFirstClusterMap.TryGetValue(fileName, out var firstCluster))
                    {
                        if (copier.TryCopyFile(filePath, firstCluster, size, cancellationToken))
                        {
                            successCount++;
                        }
                    }
                }

                Log($"✓ Copied {successCount}/{filesToCopy.Count} files");
                return true;
            }
            catch (Exception ex)
            {
                Log($"✗ File content copy failed: {ex.Message}");
                return false;
            }
        }

        private bool FinalizeImage(CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(_config.OutputImagePath);
                Log($"✓ Image file size: {info.Length:N0} bytes");
                Log($"✓ Image created at: {_config.OutputImagePath}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"✗ Finalization failed: {ex.Message}");
                return false;
            }
        }

        private List<HddJsonParser.SnapshotFileEntry> FlattenEntries(List<HddJsonParser.SnapshotFileEntry> entries)
        {
            var result = new List<HddJsonParser.SnapshotFileEntry>();
            foreach (var entry in entries)
            {
                result.Add(entry);
                if (entry.Children != null)
                    result.AddRange(FlattenEntries(entry.Children));
            }
            return result;
        }

        private List<(string FilePath, string FileName, long Size)> CollectFilesToCopy(List<HddJsonParser.SnapshotFileEntry> entries)
        {
            var result = new List<(string, string, long)>();
            foreach (var entry in entries)
            {
                if (!entry.IsDirectory && entry.Size > 0)
                {
                    var sourcePath = _matcher?.TryFindFile(entry.Path ?? entry.Name);
                    if (sourcePath != null)
                        result.Add((sourcePath, entry.Name, entry.Size));
                }

                if (entry.Children != null)
                    result.AddRange(CollectFilesToCopy(entry.Children));
            }
            return result;
        }

        private void UpdateStatus(string status)
        {
            StatusChanged?.Invoke(status);
        }

        private void UpdateProgress(int percentage)
        {
            ProgressChanged?.Invoke(Math.Clamp(percentage, 0, 100));
        }

        private void Log(string message)
        {
            LogMessage?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }
}
