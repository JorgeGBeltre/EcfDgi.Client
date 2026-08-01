using System.Security.Cryptography;
using System.Text;

namespace EcfDgii.Client.Api.Infrastructure.Security
{
    public static class CanonicalRequestHelper
    {
        public static string BuildCanonicalString(string method, string pathAndQuery, string timestamp, string nonce, string body)
        {
            var formattedMethod = (method ?? string.Empty).ToUpperInvariant();
            var formattedPath = string.IsNullOrWhiteSpace(pathAndQuery) ? "/" : pathAndQuery;
            var bodyHash = ComputeSha256Hex(body ?? string.Empty);

            var sb = new StringBuilder();
            sb.Append(formattedMethod).Append('\n');
            sb.Append(formattedPath).Append('\n');
            sb.Append(timestamp).Append('\n');
            sb.Append(nonce).Append('\n');
            sb.Append(bodyHash);

            return sb.ToString();
        }

        public static string ComputeHmacSha256(string secretKey, string canonicalString)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
            var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonicalString));
            return Convert.ToBase64String(hashBytes);
        }

        public static string ComputeSha256Hex(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }
    }
}
