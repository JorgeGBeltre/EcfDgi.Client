using System;
using EcfDgii.Client.Infrastructure.Security;
using Xunit;

namespace UnitTests.Security
{
    public class HmacTestVector
    {
        public string Method { get; set; } = string.Empty;
        public string PathAndQuery { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Secret { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string ExpectedCanonicalString { get; set; } = string.Empty;
        public string ExpectedSignature { get; set; } = string.Empty;
    }

    public class HmacTestVectors
    {
        public static readonly HmacTestVector Vector1_GetNoBody = new HmacTestVector
        {
            Method = "GET",
            PathAndQuery = "/api/ecf/status?rncEmisor=101010101&eNcf=E310000000001",
            Timestamp = "1740000000",
            Nonce = "a1b2c3d4e5f6",
            Secret = "SharedTestSecretKey123",
            Body = "",
            ExpectedCanonicalString = "GET\n/api/ecf/status?rncEmisor=101010101&eNcf=E310000000001\n1740000000\na1b2c3d4e5f6\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ExpectedSignature = CanonicalRequestHelper.ComputeHmacSha256("SharedTestSecretKey123", "GET\n/api/ecf/status?rncEmisor=101010101&eNcf=E310000000001\n1740000000\na1b2c3d4e5f6\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
        };

        public static readonly HmacTestVector Vector2_PostWithBody = new HmacTestVector
        {
            Method = "POST",
            PathAndQuery = "/api/ecf/send-rfce",
            Timestamp = "1740000000",
            Nonce = "f6e5d4c3b2a1",
            Secret = "SharedTestSecretKey123",
            Body = "{\"rncEmisor\":\"101010101\"}",
            ExpectedCanonicalString = "POST\n/api/ecf/send-rfce\n1740000000\nf6e5d4c3b2a1\n" + CanonicalRequestHelper.ComputeSha256Hex("{\"rncEmisor\":\"101010101\"}"),
            ExpectedSignature = CanonicalRequestHelper.ComputeHmacSha256("SharedTestSecretKey123", "POST\n/api/ecf/send-rfce\n1740000000\nf6e5d4c3b2a1\n" + CanonicalRequestHelper.ComputeSha256Hex("{\"rncEmisor\":\"101010101\"}"))
        };

        [Fact]
        public void Verify_Vector1_CanonicalAndSignature()
        {
            var actualCanonical = CanonicalRequestHelper.BuildCanonicalString(
                Vector1_GetNoBody.Method,
                Vector1_GetNoBody.PathAndQuery,
                Vector1_GetNoBody.Timestamp,
                Vector1_GetNoBody.Nonce,
                Vector1_GetNoBody.Body);

            Assert.Equal(Vector1_GetNoBody.ExpectedCanonicalString, actualCanonical);

            var actualSig = CanonicalRequestHelper.ComputeHmacSha256(Vector1_GetNoBody.Secret, actualCanonical);
            Assert.Equal(Vector1_GetNoBody.ExpectedSignature, actualSig);
        }

        [Fact]
        public void Verify_Vector2_CanonicalAndSignature()
        {
            var actualCanonical = CanonicalRequestHelper.BuildCanonicalString(
                Vector2_PostWithBody.Method,
                Vector2_PostWithBody.PathAndQuery,
                Vector2_PostWithBody.Timestamp,
                Vector2_PostWithBody.Nonce,
                Vector2_PostWithBody.Body);

            Assert.Equal(Vector2_PostWithBody.ExpectedCanonicalString, actualCanonical);

            var actualSig = CanonicalRequestHelper.ComputeHmacSha256(Vector2_PostWithBody.Secret, actualCanonical);
            Assert.Equal(Vector2_PostWithBody.ExpectedSignature, actualSig);
        }
    }
}
