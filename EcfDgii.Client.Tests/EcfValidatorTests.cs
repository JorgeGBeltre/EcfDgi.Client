using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Application.Services;
using Xunit;

namespace EcfDgii.Client.Tests
{
    public class EcfValidatorTests
    {
        private readonly EcfValidator _validator = new EcfValidator();

        [Fact]
        public void ValidateRfce_Valid_ReturnsTrue()
        {
            var rfce = new Rfce
            {
                Encabezado = new RfceEncabezado
                {
                    Emisor = new RfceEmisor { RncEmisor = "101672919", RazonSocialEmisor = "TEST", FechaEmision = "15-04-2024" },
                    IdDoc = new RfceIdDoc { ENcf = "E310000000001", TipoeCF = "32" },
                    Totales = new RfceTotales { MontoTotal = 100.00m }
                }
            };

            var result = _validator.ValidateRfce(rfce);

            Assert.True(result.IsValid);
        }

        [Fact]
        public void ValidateRfce_InvalidMonto_ReturnsFalse()
        {
            var rfce = new Rfce
            {
                Encabezado = new RfceEncabezado
                {
                    Emisor = new RfceEmisor { RncEmisor = "101672919", RazonSocialEmisor = "TEST", FechaEmision = "15-04-2024" },
                    IdDoc = new RfceIdDoc { ENcf = "E310000000001", TipoeCF = "32" },
                    Totales = new RfceTotales { MontoTotal = 250_000.00m } // Over limit
                }
            };

            var result = _validator.ValidateRfce(rfce);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("recepcionfc"));
        }
    }
}
