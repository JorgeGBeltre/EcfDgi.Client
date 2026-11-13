using EcfDgii.Client.Shared.Common;
using Xunit;

namespace EcfDgii.Client.UnitTests.Common
{
    public class RncFormatTests
    {
        [Theory]
        [InlineData("101000009")]
        [InlineData("00100000019")]
        public void IsValid_AcceptsNineOrElevenAllDigitValues(string value)
        {
            Assert.True(RncFormat.IsValid(value));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("12345678")]
        [InlineData("1234567890")]
        [InlineData("10167291A")]
        public void IsValid_RejectsMissingOrMalformedValues(string? value)
        {
            Assert.False(RncFormat.IsValid(value));
        }
    }
}
