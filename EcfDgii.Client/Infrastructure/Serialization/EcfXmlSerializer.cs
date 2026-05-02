using System;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace EcfDgii.Client.Infrastructure.Serialization
{
    public class EcfXmlSerializer
    {
        private static readonly XmlWriterSettings XmlSettings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false,
            NamespaceHandling = NamespaceHandling.OmitDuplicates
        };

        public string Serialize<T>(T model) where T : class
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            var serializer = new XmlSerializer(typeof(T));
            using var sw = new StringWriterWithEncoding(Encoding.UTF8);
            using (var writer = XmlWriter.Create(sw, XmlSettings))
            {
                var ns = new XmlSerializerNamespaces();
                ns.Add("", "");

                serializer.Serialize(writer, model, ns);
            }

            return sw.ToString();
        }

        public T Deserialize<T>(string xml) where T : class
        {
            if (string.IsNullOrEmpty(xml)) throw new ArgumentNullException(nameof(xml));

            var serializer = new XmlSerializer(typeof(T));
            using var reader = new StringReader(xml);
            return (T)serializer.Deserialize(reader)!;
        }

        public string GetFileName(string rncEmisor, string eNcf)
        {
            return $"{rncEmisor}{eNcf}.xml";
        }

        public string EscapeAlfanum(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Replace("&", "&amp;")
                        .Replace("<", "&lt;")
                        .Replace(">", "&gt;")
                        .Replace("\"", "&quot;")
                        .Replace("'", "&apos;");
        }

        private class StringWriterWithEncoding : StringWriter
        {
            public StringWriterWithEncoding(Encoding encoding) { Encoding = encoding; }
            public override Encoding Encoding { get; }
        }
    }
}
