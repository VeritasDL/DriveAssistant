using FATX.FileSystem;
using System;
using System.IO;

namespace FATX.Analyzers.Signatures
{
    public class CustomPatternSignature : FileSignature
    {
        private readonly CustomSignatureDefinition _definition;

        public CustomPatternSignature(Volume volume, long offset, CustomSignatureDefinition definition)
            : base(volume, offset)
        {
            _definition = definition;
            if (!string.IsNullOrWhiteSpace(_definition?.Name))
            {
                FileName = $"{_definition.Name}{_definition.Extension}";
            }
        }

        public override bool Test()
        {
            if (!_definition.Enabled)
            {
                return false;
            }

            var header = _definition.GetHeaderBytes();
            if (_definition.HeaderOffset > 0)
            {
                Seek(_definition.HeaderOffset, SeekOrigin.Begin);
            }

            var data = ReadBytes(header.Length);
            if (data.Length != header.Length)
            {
                return false;
            }

            for (int i = 0; i < header.Length; i++)
            {
                if (data[i] != header[i])
                {
                    return false;
                }
            }

            return true;
        }

        public override void Parse()
        {
            var footer = _definition.GetFooterBytes();
            if (footer == null || footer.Length == 0)
            {
                var fallbackSize = _definition.MaxSearchLength > 0
                    ? _definition.MaxSearchLength
                    : _definition.HeaderOffset + _definition.GetHeaderBytes().Length;
                FileSize = Math.Max(fallbackSize, _definition.HeaderOffset + _definition.GetHeaderBytes().Length);
                return;
            }

            long limit = Math.Max(_definition.MaxSearchLength, footer.Length);
            Seek(0, SeekOrigin.Begin);
            var buffer = ReadBytes((int)Math.Min(limit, int.MaxValue));

            for (int i = _definition.GetHeaderBytes().Length; i <= buffer.Length - footer.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < footer.Length; j++)
                {
                    if (buffer[i + j] != footer[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    FileSize = i + footer.Length;
                    return;
                }
            }

            FileSize = Math.Max(_definition.GetHeaderBytes().Length, 1);
        }
    }
}
