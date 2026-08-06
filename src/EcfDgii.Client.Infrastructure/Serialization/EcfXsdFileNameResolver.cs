using System.Xml;

namespace EcfDgii.Client.Infrastructure.Serialization
{
    /// <summary>
    /// Resolves which DGII XSD file (checked into the repo under "Documentación Técnica (XSD)",
    /// copied next to the published app — see EcfDgii.Client.Api.csproj) applies to a given XML
    /// document, by inspecting its root element and, for an e-CF, its TipoeCF. Extracted from
    /// EcfClient's private GetXsdFileName so DocumentsController can run the SAME resolution logic
    /// for a pre-signature validation gate — duplicating this by hand in two places would let them
    /// silently drift (the exact "written once, trusted twice" pattern this session keeps finding).
    /// </summary>
    public static class EcfXsdFileNameResolver
    {
        public static string Resolve(string xmlContent)
        {
            try
            {
                var doc = new XmlDocument();
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
                // Fallback en caso de que ocurra algún error al parsear el XML
            }

            return string.Empty;
        }
    }
}
