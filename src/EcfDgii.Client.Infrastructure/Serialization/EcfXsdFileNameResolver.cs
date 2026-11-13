using System.Xml;

namespace EcfDgii.Client.Infrastructure.Serialization
{
    public static class EcfXsdFileNameResolver
    {
        public static string Resolve(string xmlContent)
        {
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                doc.LoadXml(xmlContent);
                var rootName = doc.DocumentElement?.LocalName;

                if (rootName == "RFCE")
                    return "RFCE 32 v.1.0.xsd";
                if (rootName == "ACECF")
                    return "ACECF v.1.0.xsd";
                if (rootName == "ARECF")
                    return "ARECF v1.0.xsd";
                if (rootName == "ANECF" || rootName == "Anulacion")
                    return "ANECF v.1.0.xsd";
                if (rootName == "SemillaModel")
                    return "Semilla v.1.0.xsd";

                if (rootName == "ECF")
                {
                    var nsmgr = new XmlNamespaceManager(doc.NameTable);
                    var node = doc.SelectSingleNode("//Encabezado/IdDoc/TipoeCF", nsmgr);
                    if (node != null)
                    {
                        var tipo = node.InnerText.Trim();
                        return $"e-CF {tipo} v.1.0.xsd";
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }
    }
}
