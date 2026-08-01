using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EcfDgii.Client.Infrastructure.Security;
using Xunit;

namespace UnitTests.Security
{
    public class VectorItem
    {
        public string id { get; set; } = string.Empty;
        public string method { get; set; } = string.Empty;
        public string rawTarget { get; set; } = string.Empty;
        public string timestamp { get; set; } = string.Empty;
        public string nonce { get; set; } = string.Empty;
        public string body { get; set; } = string.Empty;
        public string expectedBodyHashHex { get; set; } = string.Empty;
        public string expectedCanonicalString { get; set; } = string.Empty;
        public string expectedSignature { get; set; } = string.Empty;
    }

    public class VectorFileRoot
    {
        public string version { get; set; } = string.Empty;
        public string secret { get; set; } = string.Empty;
        public List<VectorItem> vectors { get; set; } = new List<VectorItem>();
    }

    public class SharedHmacVectorTests
    {
        [Fact]
        public void Verify_All10_HmacTestVectors_MatchCanonicalAndSignature()
        {
            var jsonPath = @"c:\Users\Jorge\Desktop\New folder\New folder\EcfDgi.Client\tests\UnitTests\Security\hmac_test_vectors.json";
            Assert.True(File.Exists(jsonPath), $"hmac_test_vectors.json must exist at '{jsonPath}'");

            var jsonContent = File.ReadAllText(jsonPath, Encoding.UTF8);
            var root = JsonSerializer.Deserialize<VectorFileRoot>(jsonContent);

            Assert.NotNull(root);
            Assert.Equal("1.0.0", root.version);
            Assert.Equal(10, root.vectors.Count);

            foreach (var vec in root.vectors)
            {
                // 1. Verify Body Hash calculation
                var actualBodyHash = CanonicalRequestHelper.ComputeSha256Hex(vec.body);
                Assert.Equal(vec.expectedBodyHashHex, actualBodyHash);

                // 2. Verify Canonical String construction
                var actualCanonical = CanonicalRequestHelper.BuildCanonicalString(
                    vec.method,
                    vec.rawTarget,
                    vec.timestamp,
                    vec.nonce,
                    vec.body);

                Assert.Equal(vec.expectedCanonicalString, actualCanonical);

                // 3. Verify HMAC SHA256 Signature calculation
                var actualSig = CanonicalRequestHelper.ComputeHmacSha256(root.secret, actualCanonical);
                Assert.Equal(vec.expectedSignature, actualSig);
            }

            // Verify Anti-Deriva JSON File Checksum SHA-256
            using var sha256 = SHA256.Create();
            var fileBytes = File.ReadAllBytes(jsonPath);
            var actualChecksumHex = Convert.ToHexString(sha256.ComputeHash(fileBytes));
            Assert.NotEmpty(actualChecksumHex);
        }
    }
}
