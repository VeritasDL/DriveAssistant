using System;
using System.IO;

namespace FATX.Recovery.Builders
{
    /// <summary>
    /// Builds the basic Xbox 360 HDD structures (devkit superblock, FATX headers).
    /// </summary>
    public static class HddStructureBuilders
    {
        /// <summary>
        /// Creates the devkit superblock with partition table.
        /// </summary>
        public static byte[] CreateDevkitSuperblock(int partitionCount, long partitionOffset, long partitionLength)
        {
            var sb = new byte[0x10000]; // 64 KB superblock
            
            // Fill partition table (starting at offset 0x40)
            var partitionTableOffset = 0x40;
            for (int i = 0; i < partitionCount && i < 64; i++)
            {
                var offset = partitionTableOffset + (i * 32);
                BitConverter.GetBytes(partitionOffset).CopyTo(sb, offset);
                BitConverter.GetBytes(partitionLength).CopyTo(sb, offset + 8);
                sb[offset + 16] = 0x4D; // 'M' for FATX
            }

            return sb;
        }

        /// <summary>
        /// Creates the FATX partition header.
        /// </summary>
        public static byte[] CreateFatxPartitionHeader(uint serialNumber, uint bytesPerCluster, uint rootDirFirstCluster)
        {
            var header = new byte[0x1000]; // 4 KB header
            
            // FATX signature
            const uint FatxSignature = 0x58544146; // "XTAF" in little-endian
            BitConverter.GetBytes(FatxSignature).CopyTo(header, 0);
            
            // Serial number
            BitConverter.GetBytes(serialNumber).CopyTo(header, 4);
            
            // Bytes per cluster
            BitConverter.GetBytes(bytesPerCluster).CopyTo(header, 8);
            
            // Root directory first cluster
            BitConverter.GetBytes(rootDirFirstCluster).CopyTo(header, 12);

            return header;
        }

        /// <summary>
        /// Calculates the optimal bytes per cluster based on partition size.
        /// </summary>
        public static uint CalculateBytesPerCluster(long partitionSize)
        {
            // Xbox 360 typically uses 16 KB (0x4000) clusters
            if (partitionSize > 100 * 1024 * 1024 * 1024) // > 100 GB
                return 0x4000; // 16 KB
            else if (partitionSize > 10 * 1024 * 1024 * 1024) // > 10 GB
                return 0x2000; // 8 KB
            else
                return 0x1000; // 4 KB
        }
    }
}
