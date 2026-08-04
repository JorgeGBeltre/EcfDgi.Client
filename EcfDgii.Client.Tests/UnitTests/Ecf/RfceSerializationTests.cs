using System.IO;
using System.Xml;
using Xunit;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Infrastructure.Serialization;

namespace EcfDgii.Client.UnitTests.Ecf
{
    public class RfceSerializationTests
    {
        [Fact]
        public void Rfce_ShouldSerializeWithCorrectTagsAndStructure()
        {
            var rfce = new Rfce
            {
                Encabezado = new RfceEncabezado
                {
                    Version = "1.0",
                    IdDoc = new RfceIdDoc
                    {
                        TipoeCF = "32",
                        ENcf = "E320000000001",
                        TipoIngresos = 1,
                        TipoPago = 1
                    },
                    Emisor = new RfceEmisor
                    {
                        RncEmisor = "101672919",
                        RazonSocialEmisor = "WILLY CHIC DOMINICANA SRL",
                        FechaEmision = "10-10-2020"
                    },
                    Comprador = new RfceComprador
                    {
                        RncComprador = "101889063",
                        RazonSocialComprador = "Cliente Test"
                    },
                    Totales = new RfceTotales
                    {
                        MontoTotal = 100.00m,
                        TotalITBIS = 18.00m
                    },
                    CodigoSeguridadeCF = "ABCD12"
                }
            };

            var serializer = new EcfXmlSerializer();
            var xml = serializer.Serialize(rfce);

            // Assert tag casing
            Assert.Contains("<Version>1.0</Version>", xml);
            Assert.Contains("<eNCF>E320000000001</eNCF>", xml);
            Assert.Contains("<RNCEmisor>101672919</RNCEmisor>", xml);
            Assert.Contains("<RNCComprador>101889063</RNCComprador>", xml);
            Assert.Contains("<RazonSocialEmisor>WILLY CHIC DOMINICANA SRL</RazonSocialEmisor>", xml);
            
            // Assert CodigoSeguridadeCF is child of Encabezado, sibling of Totales
            Assert.Contains("<CodigoSeguridadeCF>ABCD12</CodigoSeguridadeCF>", xml);
            
            // Check that CodigoSeguridadeCF is serialized after Totales but before closing Encabezado
            var indexOfTotales = xml.IndexOf("</Totales>");
            var indexOfSecCode = xml.IndexOf("<CodigoSeguridadeCF>");
            var indexOfEncabezado = xml.IndexOf("</Encabezado>");
            
            Assert.True(indexOfTotales < indexOfSecCode);
            Assert.True(indexOfSecCode < indexOfEncabezado);
        }
    }
}
