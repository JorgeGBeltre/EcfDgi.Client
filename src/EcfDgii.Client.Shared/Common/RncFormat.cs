using System.Linq;

namespace EcfDgii.Client.Shared.Common
{
    public static class RncFormat
    {
        public static bool IsValid(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && (value.Length == 9 || value.Length == 11)
            && value.All(char.IsDigit);
    }
}
