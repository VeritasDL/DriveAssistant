using FATX.Analyzers.Signatures;
using Xunit;

namespace FATX.Tests
{
    public class CustomSignatureDefinitionTests
    {
        [Fact]
        public void HeaderHex_ShouldParseSpaces()
        {
            var def = new CustomSignatureDefinition
            {
                HeaderHex = "50 4B 03 04"
            };

            var bytes = def.GetHeaderBytes();
            Assert.Equal(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, bytes);
        }
    }
}
