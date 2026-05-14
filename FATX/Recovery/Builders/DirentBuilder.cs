using System;
using System.Text;

namespace FATX.Recovery.Builders
{
    /// <summary>
    /// Creates 0x40-byte FATX directory entries (dirents).
    /// </summary>
    public class DirentBuilder
    {
        /// <summary>
        /// Creates a single 0x40-byte directory entry.
        /// </summary>
        public byte[] CreateDirent(string fileName, long fileSize, uint firstCluster, bool isDirectory, bool isDeleted)
        {
            var dirent = new byte[0x40]; // 64 bytes per entry

            // File name (max 32 bytes, null-terminated)
            var nameBytes = Encoding.ASCII.GetBytes(fileName);
            var nameLen = Math.Min(nameBytes.Length, 32);
            Array.Copy(nameBytes, 0, dirent, 0, nameLen);
            if (nameLen < 32)
                dirent[nameLen] = 0; // Null terminator

            // Attributes (offset 32)
            byte attributes = 0;
            if (isDirectory)
                attributes |= 0x10; // Directory flag
            if (isDeleted)
                dirent[0] = 0xE5; // FATX deleted marker

            dirent[32] = attributes;

            // Reserved (offset 33)
            dirent[33] = 0;

            // Creation time (offset 34-37, FAT format)
            var now = DateTime.Now;
            var fatDateTime = ConvertToFatDateTime(now);
            BitConverter.GetBytes(fatDateTime).CopyTo(dirent, 34);

            // Last access time (offset 38-41)
            BitConverter.GetBytes(fatDateTime).CopyTo(dirent, 38);

            // First cluster (offset 44-47)
            BitConverter.GetBytes(firstCluster).CopyTo(dirent, 44);

            // File size (offset 48-51)
            BitConverter.GetBytes((uint)Math.Min(fileSize, uint.MaxValue)).CopyTo(dirent, 48);

            return dirent;
        }

        /// <summary>
        /// Converts .NET DateTime to FAT format (16-bit date, 16-bit time).
        /// </summary>
        private uint ConvertToFatDateTime(DateTime dateTime)
        {
            ushort date = (ushort)(((dateTime.Year - 1980) << 9) | (dateTime.Month << 5) | dateTime.Day);
            ushort time = (ushort)((dateTime.Hour << 11) | (dateTime.Minute << 5) | (dateTime.Second >> 1));
            return ((uint)date << 16) | time;
        }
    }
}
