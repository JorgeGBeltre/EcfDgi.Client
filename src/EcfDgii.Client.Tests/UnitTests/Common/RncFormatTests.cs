using EcfDgii.Client.Shared.Common;
using Xunit;

namespace EcfDgii.Client.UnitTests.Common
{
    public class RncFormatTests
    {
        [Theory]
        [InlineData("101672919")]      // 9-digit RNC
        [InlineData("00101672919")]    // 11-digit cédula shape
        public void IsValid_AcceptsNineOrElevenAllDigitValues(string value)
        {
            Assert.True(RncFormat.IsValid(value));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("12345678")]       // 8 digits
        [InlineData("1234567890")]     // 10 digits
        [InlineData("10167291A")]      // non-digit
        public void IsValid_RejectsMissingOrMalformedValues(string? value)
        {
            Assert.False(RncFormat.IsValid(value));
        }
    }
}
