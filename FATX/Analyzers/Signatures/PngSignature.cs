using FATX.FileSystem;
using System;
using System.IO;
using System.Linq;

namespace FATX.Analyzers.Signatures
{
    class PngSignature : FileSignature
    {
        private static readonly byte[] PngMagic = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly byte[] IendChunk = new byte[] { 0x49, 0x45, 0x4E, 0x44 };

        public PngSignature(Volume volume, long offset)
            : base(volume, offset)
        {
        }

        public override bool Test()
        {
            var header = ReadBytes(PngMagic.Length);
            return header.SequenceEqual(PngMagic);
        }

        public override void Parse()
        {
            const int maxScan = 0x2000000;
            Seek(0);
            var buf = ReadBytes(maxScan);
            if (buf.Length < 8)
            {
                FileSize = 8;
                return;
            }

            int index = 8;
            while (index + 12 <= buf.Length)
            {
                uint chunkLength = (uint)((buf[index] << 24) | (buf[index + 1] << 16) | (buf[index + 2] << 8) | buf[index + 3]);
                int chunkTypeIndex = index + 4;
                if (chunkTypeIndex + 4 > buf.Length)
                {
                    break;
                }

                if (buf[chunkTypeIndex] == IendChunk[0] &&
                    buf[chunkTypeIndex + 1] == IendChunk[1] &&
                    buf[chunkTypeIndex + 2] == IendChunk[2] &&
                    buf[chunkTypeIndex + 3] == IendChunk[3])
                {
                    FileSize = chunkTypeIndex + 8;
                    FileName = $"{FileName}.png";
                    return;
                }

                long next = index + 12L + chunkLength;
                if (next <= index || next > buf.Length)
                {
                    break;
                }

                index = (int)next;
            }

            FileSize = Math.Min(buf.Length, 8);
            FileName = $"{FileName}.png";
        }
    }
}
