using FATX.FileSystem;
using System.Text;

namespace FATX.Analyzers.Signatures
{
    class GifSignature : FileSignature
    {
        public GifSignature(Volume volume, long offset)
            : base(volume, offset)
        {
        }

        public override bool Test()
        {
            var magic = Encoding.ASCII.GetString(ReadBytes(6));
            return magic == "GIF87a" || magic == "GIF89a";
        }

        public override void Parse()
        {
            const int maxScan = 0x800000;
            Seek(0);
            var buf = ReadBytes(maxScan);
            for (int i = 6; i < buf.Length; i++)
            {
                if (buf[i] == 0x3B)
                {
                    FileSize = i + 1;
                    FileName = $"{FileName}.gif";
                    return;
                }
            }

            FileSize = 6;
            FileName = $"{FileName}.gif";
        }
    }
}
