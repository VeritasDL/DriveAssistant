using FATX.FileSystem;
using System.IO;
using System.Text;

namespace FATX.Analyzers.Signatures
{
    class XEXSignature : FileSignature
    {
        public XEXSignature(Volume volume, long offset)
            : base(volume, offset)
        {

        }

        public override bool Test()
        {
            byte[] magic = this.ReadBytes(4);
            if (magic.Length < 4 || magic[0] != (byte)'X' || magic[1] != (byte)'E' || magic[2] != (byte)'X')
            {
                return false;
            }

            return magic[3] == (byte)'0'
                || magic[3] == (byte)'?'
                || magic[3] == (byte)'-'
                || magic[3] == (byte)'%'
                || magic[3] == (byte)'1'
                || magic[3] == (byte)'2';
        }

        public override void Parse()
        {
            Seek(0x10);
            var securityOffset = ReadUInt32();
            var headerCount = ReadUInt32();
            uint fileNameOffset = 0;
            for (int i = 0; i < headerCount; i++)
            {
                var xid = ReadUInt32();
                if (xid == 0x000183ff)
                {
                    fileNameOffset = ReadUInt32();
                }
                else
                {
                    ReadUInt32();
                }
            }
            Seek(securityOffset + 4);
            this.FileSize = ReadUInt32();
            if (fileNameOffset != 0)
            {
                Seek(fileNameOffset + 4);
                this.FileName = Path.ChangeExtension(ReadCString(), ".xex");
            }
        }
    }
}
