using System.Linq;

namespace EcfDgii.Client.Shared.Common
{
    /// <summary>
    /// Format validation for Dominican Republic taxpayer identifiers: RNC (9 digits) for
    /// businesses, cédula (11 digits) for individuals. This only checks the shape — it does not
    /// verify the number is actually registered with DGII.
    /// </summary>
    public static class RncFormat
    {
        public static bool IsValid(string? value) =>
            !string.IsNullOrWhiteSpace(value)
            && (value.Length == 9 || value.Length == 11)
            && value.All(char.IsDigit);
    }
}
