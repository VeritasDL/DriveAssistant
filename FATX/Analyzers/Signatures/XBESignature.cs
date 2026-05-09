using FATX.FileSystem;
using System.IO;
using System.Text;

namespace FATX.Analyzers.Signatures
{
    class XBESignature : FileSignature
    {
        private const string XBEMagic = "XBEH";

        public XBESignature(Volume volume, long offset)
            : base(volume, offset)
        {

        }

        public override bool Test()
        {
            if (!CanReadRelative(0, 4))
            {
                return false;
            }

            byte[] magic = this.ReadBytes(4);
            if (Encoding.ASCII.GetString(magic) == XBEMagic)
            {
                return true;
            }

            return false;
        }

        public override void Parse()
        {
            if (!CanReadRelative(0x150, 4))
            {
                return;
            }

            Seek(0x104);
            var baseAddress = ReadUInt32();
            Seek(0x10C);
            this.FileSize = ReadUInt32();
            Seek(0x150);
            var debugFileNameOffset = ReadUInt32();
            var debugNameRelativeOffset = debugFileNameOffset - baseAddress;
            if (debugFileNameOffset >= baseAddress && CanReadRelative(debugNameRelativeOffset, 1))
            {
                Seek(debugNameRelativeOffset);
                var debugFileName = ReadCString();
                if (!string.IsNullOrWhiteSpace(debugFileName))
                {
                    this.FileName = Path.ChangeExtension(debugFileName, ".xbe");
                }
            }
        }
    }
}
