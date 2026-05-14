using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Linq;

namespace FATX.Recovery.Builders
{
    /// <summary>
    /// Handles copying file contents to allocated clusters in the HDD image.
    /// </summary>
    public class FileContentCopier
    {
        private const int BufferSize = 0x100000; // 1 MB buffer
        private readonly string _imagePath;
        private readonly uint _bytesPerCluster;
        private readonly long _fileAreaByteOffset;
        private readonly uint[] _fileAllocationTable;

        /// <summary>
        /// Event raised when file copy progress is updated.
        /// </summary>
        public event Action<string, long, long>? FileCopyProgress;

        /// <summary>
        /// Event raised when a file copy fails.
        /// </summary>
        public event Action<string, Exception>? FileCopyFailed;

        public FileContentCopier(string imagePath, uint bytesPerCluster, long fileAreaByteOffset, uint[] fileAllocationTable)
        {
            _imagePath = imagePath;
            _bytesPerCluster = bytesPerCluster;
            _fileAreaByteOffset = fileAreaByteOffset;
            _fileAllocationTable = fileAllocationTable;
        }

        /// <summary>
        /// Copies a file from the source path to the allocated clusters in the image.
        /// </summary>
        public bool TryCopyFile(string sourceFilePath, uint firstCluster, long fileSize, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!File.Exists(sourceFilePath))
                {
                    FileCopyFailed?.Invoke(Path.GetFileName(sourceFilePath), new FileNotFoundException($"Source file not found: {sourceFilePath}"));
                    return false;
                }

                var fileInfo = new FileInfo(sourceFilePath);
                if (fileInfo.Length == 0 && fileSize > 0)
                {
                    FileCopyFailed?.Invoke(fileInfo.Name, new InvalidOperationException("Source file is empty but expected non-zero size"));
                    return false;
                }

                using var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                using var imageStream = new FileStream(_imagePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, BufferSize, FileOptions.RandomAccess);

                // Get cluster chain from FAT
                var clusterChain = GetClusterChain(firstCluster);
                if (clusterChain.Count == 0)
                {
                    FileCopyFailed?.Invoke(Path.GetFileName(sourceFilePath), new InvalidOperationException($"Invalid cluster chain starting at {firstCluster}"));
                    return false;
                }

                var bytesToCopy = Math.Min(sourceStream.Length, fileSize);
                var remaining = bytesToCopy;
                var clusterIndex = 0;
                var buffer = new byte[BufferSize];

                while (remaining > 0 && clusterIndex < clusterChain.Count)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var cluster = clusterChain[clusterIndex];
                    var clusterOffset = _fileAreaByteOffset + (long)cluster * _bytesPerCluster;
                    var bytesInCluster = Math.Min((long)_bytesPerCluster, remaining);

                    // Read from source
                    int bytesRead = sourceStream.Read(buffer, 0, (int)bytesInCluster);
                    if (bytesRead == 0)
                        break;

                    // Write to image
                    imageStream.Seek(clusterOffset, SeekOrigin.Begin);
                    imageStream.Write(buffer, 0, bytesRead);

                    remaining -= bytesRead;
                    FileCopyProgress?.Invoke(Path.GetFileName(sourceFilePath), bytesToCopy - remaining, bytesToCopy);
                    clusterIndex++;
                }

                // If file was smaller than allocated space, zero-fill remaining clusters
                if (remaining > 0)
                {
                    var zeroBuffer = new byte[BufferSize];
                    while (remaining > 0 && clusterIndex < clusterChain.Count)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var cluster = clusterChain[clusterIndex];
                        var clusterOffset = _fileAreaByteOffset + (long)cluster * _bytesPerCluster;
                        var bytesToZero = Math.Min((long)_bytesPerCluster, remaining);

                        imageStream.Seek(clusterOffset, SeekOrigin.Begin);
                        imageStream.Write(zeroBuffer, 0, (int)bytesToZero);

                        remaining -= bytesToZero;
                        clusterIndex++;
                    }
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                FileCopyFailed?.Invoke(Path.GetFileName(sourceFilePath), new OperationCanceledException("File copy cancelled"));
                return false;
            }
            catch (Exception ex)
            {
                FileCopyFailed?.Invoke(Path.GetFileName(sourceFilePath), ex);
                return false;
            }
        }

        /// <summary>
        /// Builds the cluster chain by following the FAT.
        /// </summary>
        private List<uint> GetClusterChain(uint startCluster)
        {
            var chain = new List<uint>();
            var current = startCluster;
            const uint EndOfChainMarker = 0xFFFFFFF8;
            const int MaxChainLength = 1000000; // Prevent infinite loops

            while (current < EndOfChainMarker && chain.Count < MaxChainLength)
            {
                chain.Add(current);
                if (current >= _fileAllocationTable.Length)
                    break;

                current = _fileAllocationTable[current];
            }

            return chain;
        }
    }
}
