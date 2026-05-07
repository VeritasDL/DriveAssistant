using FATX.FileSystem;

namespace FATX.Analyzers.Signatures
{
    class JpegSignature : FileSignature
    {
        public JpegSignature(Volume volume, long offset)
            : base(volume, offset)
        {
        }

        public override bool Test()
        {
            var h = ReadBytes(3);
            return h.Length == 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF;
        }

        public override void Parse()
        {
            const int maxScan = 0x1000000;
            Seek(0);
            var buf = ReadBytes(maxScan);
            for (int i = 2; i < buf.Length - 1; i++)
            {
                if (buf[i] == 0xFF && buf[i + 1] == 0xD9)
                {
                    FileSize = i + 2;
                    FileName = $"{FileName}.jpg";
                    return;
                }
            }

            FileSize = 3;
            FileName = $"{FileName}.jpg";
        }
    }
}
