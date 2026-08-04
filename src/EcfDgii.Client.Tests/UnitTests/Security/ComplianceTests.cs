using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using Xunit;
using Moq;
using Moq.Protected;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Security;
using EcfDgii.Client.Infrastructure.Dgii;
using EcfDgii.Client.Infrastructure.Serialization;

namespace EcfDgii.Client.UnitTests.Security
{
    public class ComplianceTests
    {
        [Fact]
        public void TimbreUrl_ShouldContainCorrectParameterSpelling_codigoseguridad()
        {
            // Arrange
            var ecfReq = new TimbreEcfRequest
            {
                RncEmisor = "101672919",
                RncComprador = "101889063",
                ENcf = "E310000000001",
                FechaEmision = "10-10-2020",
                MontoTotal = 100.50m,
                FechaFirma = "10-10-2020 09:00:00",
                CodigoSeguridad = "abcd12"
            };

            var fcReq = new TimbreFcRequest
            {
                RncEmisor = "101672919",
                ENcf = "E320000000001",
                MontoTotal = 100.50m,
                CodigoSeguridad = "abcd12"
            };

            // Act
            var ecfUrl = EcfSecurityUtils.BuildTimbreUrl("https://example.com/timbre", ecfReq);
            var fcUrl = EcfSecurityUtils.BuildTimbreFcUrl("https://example.com/timbrefc", fcReq);

            // Assert
            Assert.Contains("codigoseguridad=", ecfUrl);
            Assert.DoesNotContain("codigoseuridad=", ecfUrl); // verify no typo

            Assert.Contains("codigoseguridad=", fcUrl);
            Assert.DoesNotContain("codigoseuridad=", fcUrl); // verify no typo
        }

        [Fact]
        public void EcfSchemaValidator_ShouldValidateCorrectRfceXml()
        {
            // Arrange
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
                        MontoTotal = 100.50m,
                        TotalITBIS = 18.00m
                    },
                    CodigoSeguridadeCF = "ABCD12"
                }
            };

            var serializer = new EcfXmlSerializer();
            var xml = serializer.Serialize(rfce);

            var signer = new EcfXmlSigner(string.Empty, string.Empty);
            var signedXml = signer.SignXml(xml, "101672919");

            var validator = new EcfSchemaValidator();
            
            // Resolve path robustly by walking up the directory tree
            var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
            string? xsdDir = null;
            while (currentDir != null)
            {
                var checkDir = Path.Combine(currentDir.FullName, "Documentación Técnica (XSD)");
                if (Directory.Exists(checkDir))
                {
                    xsdDir = checkDir;
                    break;
                }
                currentDir = currentDir.Parent;
            }

            Assert.NotNull(xsdDir);
            var xsdPath = Path.Combine(xsdDir, "RFCE 32 v.1.0.xsd");

            // Act
            var result = validator.Validate(signedXml, xsdPath);

            // Assert
            Assert.True(result.IsValid, string.Join("; ", result.Errors));
        }

        [Fact]
        public void TokenManager_ShouldSuccessfullyParseExpirationWithMilliseconds()
        {
            // Arrange
            var responseXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><RespuestaAutenticacion><token>test-token-value</token><expira>2026-08-04T23:55:06.893Z</expira><expedido>2026-08-04T22:55:06.893Z</expedido></RespuestaAutenticacion>";
            
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<System.Threading.Tasks.Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<System.Threading.CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new StringContent(responseXml, Encoding.UTF8, "application/xml")
                });

            var httpClient = new HttpClient(handlerMock.Object);
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(x => x.SignXml(It.IsAny<string>(), It.IsAny<string>())).Returns("dummy-signed-xml");

            var config = new EcfEnvironmentConfig
            {
                AutenticacionUrl = "https://example.com/auth"
            };

            var tokenManager = new EcfTokenManager(httpClient, signerMock.Object, config, "101672919");

            // Act & Assert
            // We use reflection to invoke RenewTokenAsync, or we can just call GetTokenAsync which triggers it.
            // Since we mocked HttpMessageHandler, GetTokenAsync will invoke the http calls and parse the response.
            var task = tokenManager.GetTokenAsync();
            task.Wait();
            var token = task.Result;

            Assert.Equal("test-token-value", token);
        }
    }
}
