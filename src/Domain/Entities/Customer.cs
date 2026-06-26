using EcfDgii.Client.Domain.Common;

namespace EcfDgii.Client.Domain.Entities
{
    public class Customer : AuditableEntity
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Rnc { get; set; } = string.Empty;
    }
}
