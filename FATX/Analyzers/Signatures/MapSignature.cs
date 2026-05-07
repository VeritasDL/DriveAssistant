using FATX.FileSystem;
using System.IO;
using System.Text;

namespace FATX.Analyzers.Signatures
{
    class MapSignature : FileSignature
    {
        public MapSignature(Volume volume, long offset)
            : base(volume, offset)
        {
        }

        public override bool Test()
        {
            byte[] magic = ReadBytes(4);
            return magic.Length == 4 && Encoding.ASCII.GetString(magic) == "head";
        }

        public override void Parse()
        {
            SetByteOrder(ByteOrder.Little);
            Seek(0x8);
            uint fileSize = ReadUInt32();
            if (fileSize < 0x1000 || fileSize > 0x40000000)
            {
                return;
            }

            try
            {
                Seek(fileSize - 4);
                byte[] tail = ReadBytes(4);
                if (tail.Length == 4 && Encoding.ASCII.GetString(tail) == "foot")
                {
                    FileSize = fileSize;
                    FileName = Path.ChangeExtension($"map_{Offset:X}", ".map");
                }
            }
            catch
            {
                // Ignore invalid map candidates.
            }
        }
    }
}
