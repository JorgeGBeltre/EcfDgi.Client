using System.Collections.Generic;

namespace EcfDgii.Client.Domain.Interfaces
{
    public interface IEcfSchemaValidator
    {
        SchemaValidationResult Validate(string xmlContent, string xsdPath);
    }

    public class SchemaValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<string> Errors { get; } = new List<string>();
    }
}
