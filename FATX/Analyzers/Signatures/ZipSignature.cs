using FATX.FileSystem;
using System;
using System.Linq;

namespace FATX.Analyzers.Signatures
{
    class ZipSignature : FileSignature
    {
        private static readonly byte[] LocalHeader = new byte[] { 0x50, 0x4B, 0x03, 0x04 };
        private static readonly byte[] EndOfCentralDir = new byte[] { 0x50, 0x4B, 0x05, 0x06 };

        public ZipSignature(Volume volume, long offset)
            : base(volume, offset)
        {
        }

        public override bool Test()
        {
            return ReadBytes(4).SequenceEqual(LocalHeader);
        }

        public override void Parse()
        {
            const int maxScan = 0x1000000;
            Seek(0);
            var buf = ReadBytes(maxScan);
            for (int i = 4; i <= buf.Length - 4; i++)
            {
                if (buf[i] == EndOfCentralDir[0] &&
                    buf[i + 1] == EndOfCentralDir[1] &&
                    buf[i + 2] == EndOfCentralDir[2] &&
                    buf[i + 3] == EndOfCentralDir[3])
                {
                    FileSize = Math.Min(i + 22, buf.Length);
                    FileName = $"{FileName}.zip";
                    return;
                }
            }

            FileSize = Math.Max(4, Math.Min(buf.Length, 0x1000));
            FileName = $"{FileName}.zip";
        }
    }
}
