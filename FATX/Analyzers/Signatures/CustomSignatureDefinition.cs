using System;

namespace FATX.Analyzers.Signatures
{
    public class CustomSignatureDefinition
    {
        public string Name { get; set; }
        public string Platform { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public string HeaderHex { get; set; }
        public long HeaderOffset { get; set; }
        public string FooterHex { get; set; }
        public long MaxSearchLength { get; set; } = 0x200000;
        public string Extension { get; set; } = ".bin";

        public byte[] GetHeaderBytes() => HexToBytes(HeaderHex);
        public byte[] GetFooterBytes() => string.IsNullOrWhiteSpace(FooterHex) ? null : HexToBytes(FooterHex);

        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                throw new ArgumentException("Hex string cannot be null or empty.");
            }

            hex = hex.Replace(" ", string.Empty).Replace("-", string.Empty);
            if (hex.Length % 2 != 0)
            {
                throw new ArgumentException("Hex string must be an even-length sequence.");
            }

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }
    }
}
