using FATX.FileSystem;
using System.Linq;

namespace FATX.Analyzers.Signatures
{
    class PESignature : FileSignature
    {
        private byte[] PEMagic = new byte[] { 0x4D, 0x5A, 0x90, 0x00 };

        public PESignature(Volume volume, long offset)
            : base(volume, offset)
        {

        }

        public override bool Test()
        {
            if (!CanReadRelative(0, 4))
            {
                return false;
            }

            byte[] magic = ReadBytes(4);
            if (magic.SequenceEqual(PEMagic))
            {
                return true;
            }

            return false;
        }

        public override void Parse()
        {
            if (!CanReadRelative(0x3C, 4))
            {
                return;
            }

            SetByteOrder(ByteOrder.Little);
            Seek(0x3C);
            var lfanew = ReadUInt32();
            if (!CanReadRelative(lfanew, 0x108))
            {
                return;
            }

            Seek(lfanew);
            var sign = ReadUInt32();
            if (sign != 0x00004550)
            {
                return;
            }
            Seek(lfanew + 0x6);
            var nsec = ReadUInt16();
            if (nsec == 0 || nsec > 96)
            {
                return;
            }

            var lastSecOff = (lfanew + 0xF8) + ((nsec - 1) * 0x28);
            if (!CanReadRelative(lastSecOff + 0x14, 4))
            {
                return;
            }

            Seek(lastSecOff + 0x10);
            var secLen = ReadUInt32();
            Seek(lastSecOff + 0x14);
            var secOff = ReadUInt32();
            this.FileSize = secOff + secLen;
        }
    }
}
