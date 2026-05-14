using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

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

        public event Action<string, long, long>? FileCopyProgress;
        public event Action<string, Exception>? FileCopyFailed;

        public FileContentCopier(string imagePath, uint bytesPerCluster, long fileAreaByteOffset, uint[] fileAllocationTable)
        {
            _imagePath = imagePath;
            _bytesPerCluster = bytesPerCluster;
            _fileAreaByteOffset = fileAreaByteOffset;
            _fileAllocationTable = fileAllocationTable;
        }

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

                using var sourceStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
                using var imageStream = new FileStream(_imagePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, BufferSize);

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

                    int bytesRead = sourceStream.Read(buffer, 0, (int)bytesInCluster);
                    if (bytesRead == 0)
                        break;

                    imageStream.Seek(clusterOffset, SeekOrigin.Begin);
                    imageStream.Write(buffer, 0, bytesRead);

                    remaining -= bytesRead;
                    FileCopyProgress?.Invoke(Path.GetFileName(sourceFilePath), bytesToCopy - remaining, bytesToCopy);
                    clusterIndex++;
                }

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

        private List<uint> GetClusterChain(uint startCluster)
        {
            var chain = new List<uint>();
            var current = startCluster;
            const uint EndOfChainMarker = 0xFFFFFFF8;
            const int MaxChainLength = 1000000;

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
