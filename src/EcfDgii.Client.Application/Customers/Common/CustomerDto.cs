using System;

namespace EcfDgii.Client.Application.Customers.Common
{
    public class CustomerDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Rnc { get; set; } = string.Empty;
    }
}
