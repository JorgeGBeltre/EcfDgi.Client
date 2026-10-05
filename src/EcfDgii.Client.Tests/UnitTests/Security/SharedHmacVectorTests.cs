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
            var jsonPath = Path.Combine(AppContext.BaseDirectory, "Security", "hmac_test_vectors.json");
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

            // Verify Anti-Deriva JSON File Checksum SHA-256 (MED-249)
            // Normalizar CRLF a LF a nivel binario para inmunidad a la configuración git de fin de línea entre Windows y Linux
            using var sha256 = SHA256.Create();
            var rawBytes = File.ReadAllBytes(jsonPath);
            var normalizedBytes = NormalizeLineEndingsToLf(rawBytes);
            var actualChecksumHex = Convert.ToHexString(sha256.ComputeHash(normalizedBytes));
            const string expectedChecksumHex = "4D7FB984EAFC25517A2FE17548EEAB716B7BA4CBEC127C4CD568EB179EA61DC0";
            Assert.Equal(expectedChecksumHex, actualChecksumHex, ignoreCase: true);
        }

        private static byte[] NormalizeLineEndingsToLf(byte[] bytes)
        {
            using var ms = new MemoryStream();
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
                {
                    continue; // omit CR
                }
                ms.WriteByte(bytes[i]);
            }
            return ms.ToArray();
        }
    }
}
