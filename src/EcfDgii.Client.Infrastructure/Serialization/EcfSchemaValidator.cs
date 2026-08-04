using System;
using System.IO;
using System.Xml;
using System.Xml.Schema;
using EcfDgii.Client.Domain.Interfaces;

namespace EcfDgii.Client.Infrastructure.Serialization
{
    public class EcfSchemaValidator : IEcfSchemaValidator
    {
        public SchemaValidationResult Validate(string xmlContent, string xsdPath)
        {
            if (string.IsNullOrEmpty(xmlContent)) throw new ArgumentNullException(nameof(xmlContent));
            if (string.IsNullOrEmpty(xsdPath)) throw new ArgumentNullException(nameof(xsdPath));

            var result = new SchemaValidationResult();

            if (!File.Exists(xsdPath))
            {
                result.Errors.Add($"XSD schema file not found at: {xsdPath}");
                return result;
            }

            try
            {
                var schemas = new XmlSchemaSet();
                using (var xsdStream = File.OpenRead(xsdPath))
                using (var xsdReader = XmlReader.Create(xsdStream))
                {
                    schemas.Add(null, xsdReader);
                }

                var settings = new XmlReaderSettings
                {
                    ValidationType = ValidationType.Schema,
                    Schemas = schemas
                };

                settings.ValidationEventHandler += (sender, args) =>
                {
                    result.Errors.Add($"{args.Severity}: {args.Message} (Line {args.Exception.LineNumber}, Position {args.Exception.LinePosition})");
                };

                using (var stringReader = new StringReader(xmlContent))
                using (var xmlReader = XmlReader.Create(stringReader, settings))
                {
                    while (xmlReader.Read()) { }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Exception during schema validation: {ex.Message}");
            }

            return result;
        }
    }
}
