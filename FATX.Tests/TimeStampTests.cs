using FATX.FileSystem;
using Xunit;

namespace FATX.Tests
{
    public class TimeStampTests
    {
        [Fact]
        public void AsDateTime_ShouldClampInvalidValues()
        {
            var ts = new TimeStamp(0);
            var dt = ts.AsDateTime();

            Assert.True(dt.Year >= 1980);
        }

        [Fact]
        public void AsDateTime_ShouldReturnSafeRange()
        {
            var ts = new X360TimeStamp(0xFFFFFFFF);
            var dt = ts.AsDateTime();

            Assert.InRange(dt.Year, 1980, 9998);
        }
    }
}
