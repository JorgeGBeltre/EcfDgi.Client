using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Infrastructure.Serialization;
using Xunit;

namespace EcfDgii.Client.Tests
{
    public class EcfXmlSerializerTests
    {
        private readonly EcfXmlSerializer _serializer = new EcfXmlSerializer();

        [Fact]
        public void Serialize_RfceWithNulls_OmitsNullTags()
        {
            var rfce = new Rfce
            {
                Encabezado = new RfceEncabezado
                {
                    Emisor = new RfceEmisor { RncEmisor = "101672919" },
                    Comprador = null, // Should be omitted
                    Totales = new RfceTotales { MontoTotal = 100 }
                }
            };

            var xml = _serializer.Serialize(rfce);

            Assert.DoesNotContain("<Comprador", xml);
            Assert.Contains("<RncEmisor>101672919</RncEmisor>", xml);
        }

        [Fact]
        public void GetFileName_ValidData_ReturnsCorrectFormat()
        {
            var fileName = _serializer.GetFileName("101672919", "E310000000001");
            Assert.Equal("101672919E310000000001.xml", fileName);
        }

        [Fact]
        public void EscapeAlfanum_SpecialChars_EscapesCorrectly()
        {
            var input = "Test & Co < > \" '";
            var output = _serializer.EscapeAlfanum(input);
            Assert.Equal("Test &amp; Co &lt; &gt; &quot; &apos;", output);
        }
    }
}
