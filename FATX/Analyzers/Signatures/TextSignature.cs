using FATX.FileSystem;
using System;
using System.IO;
using System.Text;

namespace FATX.Analyzers.Signatures
{
    class TextSignature : FileSignature
    {
        private const int ProbeLength = 64;
        private const int MaxTextLength = 0x40000; // 256 KiB

        public TextSignature(Volume volume, long offset)
            : base(volume, offset)
        {
        }

        public override bool Test()
        {
            var probe = ReadBytes(ProbeLength);
            if (probe.Length < ProbeLength)
            {
                return false;
            }

            if (probe[0] == 0x00)
            {
                return false;
            }

            int printable = 0;
            int whitespace = 0;
            foreach (var b in probe)
            {
                if (IsPrintableTextByte(b))
                {
                    printable++;
                    if (b == 0x20 || b == 0x0A || b == 0x0D || b == 0x09)
                    {
                        whitespace++;
                    }
                }
            }

            return printable >= 56 && whitespace >= 3;
        }

        public override void Parse()
        {
            int size = 0;
            while (size < MaxTextLength)
            {
                try
                {
                    Seek(size);
                    byte b = ReadByte();

                    if (b == 0x00)
                    {
                        break;
                    }

                    if (!IsPrintableTextByte(b))
                    {
                        break;
                    }

                    size++;
                }
                catch
                {
                    break;
                }
            }

            if (size < ProbeLength)
            {
                size = ProbeLength;
            }

            FileSize = size;
            FileName = Path.ChangeExtension($"text_{Offset:X}", ".txt");
        }

        private static bool IsPrintableTextByte(byte b)
        {
            if (b == 0x09 || b == 0x0A || b == 0x0D)
            {
                return true;
            }

            return b >= 0x20 && b <= 0x7E;
        }
    }
}
