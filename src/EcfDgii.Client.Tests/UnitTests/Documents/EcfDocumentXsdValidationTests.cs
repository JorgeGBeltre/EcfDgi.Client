using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Api.Controllers;
using EcfDgii.Client.Application.Documents.Dto;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Configuration;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Infrastructure.Security;
using EcfDgii.Client.Infrastructure.Serialization;
using EcfDgii.Client.Shared.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace EcfDgii.Client.UnitTests.Documents
{
    public class EcfDocumentXsdValidationTests
    {
        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EcfDgii.Client.slnx")))
            {
                dir = dir.Parent;
            }
            if (dir == null)
            {
                throw new InvalidOperationException(
                    $"Could not locate repo root (EcfDgii.Client.slnx) walking up from {AppContext.BaseDirectory}.");
            }
            return dir.FullName;
        }

        private static string XsdDir() =>
            Path.Combine(FindRepoRoot(), "Documentación Técnica (XSD)");

        private static string XsdPath(string fileName) =>
            Path.Combine(XsdDir(), fileName);

        private static ApplicationDbContext NewDb() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        private static (DocumentsController Controller, ApplicationDbContext Db, Mock<IEcfXmlSignerSpy> SignerSpy, Mock<IEcfClient> EcfClientMock) MakeRealController(
            string nextEncf, bool enableXsdGate = true, bool useFallbackCertificate = false)
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(nextEncf);
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-XSD" });

            using var caRsa = RSA.Create(2048);
            var caReq = new CertificateRequest("CN=Test Root CA, O=Test CA, C=DO", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
            using var caCert = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

            using var leafRsa = RSA.Create(2048);
            var leafReq = new CertificateRequest("CN=101000001, O=EMPRESA DEMO SRL, C=DO", leafRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var serial = new byte[] { 1, 2, 3, 4 };
            using var leafCert = leafReq.Create(caCert, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
            using var leafCertWithKey = leafCert.CopyWithPrivateKey(leafRsa);
            var certBytes = leafCertWithKey.Export(X509ContentType.Pfx, "test-pass");
            var cert = X509CertificateLoader.LoadPkcs12(certBytes, "test-pass", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
            var realSigner = useFallbackCertificate
                ? new EcfXmlSigner(pfxPath: "", pfxPassword: "")
                : new EcfXmlSigner(cert);

            var signerSpyMock = new Mock<IEcfXmlSignerSpy> { CallBase = false };
            signerSpyMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => realSigner.SignXml(xml, rnc));
            signerSpyMock.Setup(s => s.ValidateCertificateSn(It.IsAny<string>())).Returns(true);
            signerSpyMock.Setup(s => s.UsesFallbackCertificate).Returns(() => realSigner.UsesFallbackCertificate);

            var clockMock = new Mock<IClock>();
            clockMock.Setup(c => c.UtcNow).Returns(() => DateTimeOffset.UtcNow.AddDays(1));
            var emisorOptions = Options.Create(new EcfEmisorOptions { Rnc = "101000001", RazonSocial = "EMPRESA DEMO SRL" });

            var ecfClientOptions = Options.Create(new EcfClientOptions
            {
                ValidateSchemasLocal = enableXsdGate,
                XsdDirectoryPath = enableXsdGate ? XsdDir() : null,
            });

            var controller = new DocumentsController(
                db, sequenceManagerMock.Object, ecfClientMock.Object, signerSpyMock.Object,
                NullLogger<DocumentsController>.Instance, clockMock.Object, emisorOptions,
                new EcfSchemaValidator(), ecfClientOptions)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            return (controller, db, signerSpyMock, ecfClientMock);
        }

        public interface IEcfXmlSignerSpy : IEcfXmlSigner;

        [Fact]
        public async Task Tipo31_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E310000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-31", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var validation = new EcfSchemaValidator().Validate(stored.SignedXmlContent!, XsdPath("e-CF 31 v.1.0.xsd"));

            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo31_WithIsoFechaEmision_AsErpConnectorActuallySendsIt_IsNormalizedAndValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E310000000802");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-31-ISO", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                    FechaEmision = "2026-08-06",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();

            Assert.Contains("<FechaEmision>06-08-2026</FechaEmision>", stored.SignedXmlContent!);

            var validation = new EcfSchemaValidator().Validate(stored.SignedXmlContent!, XsdPath("e-CF 31 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo31_WithExemptLines_DeclaresThemAsMontoExento_NotAsTaxedBase()
        {
            var (controller, db, _, _) = MakeRealController("E310000000804");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-31-EXENTO", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 150m,
                    MontoGravadoTotal = 100m,
                    MontoExento = 50m,
                    MontoItbis = 18m,
                    MontoTotal = 168m,
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();

            Assert.Contains("<MontoGravadoTotal>100.00</MontoGravadoTotal>", stored.SignedXmlContent!);
            Assert.Contains("<MontoGravadoI1>100.00</MontoGravadoI1>", stored.SignedXmlContent!);
            Assert.Contains("<MontoExento>50.00</MontoExento>", stored.SignedXmlContent!);

            var validation = new EcfSchemaValidator().Validate(stored.SignedXmlContent!, XsdPath("e-CF 31 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo31_WithMultipleTaxRates_DeclaresEachInItsOwnDgiiBucket()
        {
            var (controller, db, _, _) = MakeRealController("E310000000806");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-31-TASAS", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 400m,
                    MontoGravadoTotal = 350m,
                    MontoExento = 50m,
                    MontoItbis = 50m,
                    MontoTotal = 450m,
                    TaxBuckets =
                    [
                        new CanonicalTaxBucketDto { Rate = 18, Base = 100m, Tax = 18m },
                        new CanonicalTaxBucketDto { Rate = 16, Base = 200m, Tax = 32m },
                        new CanonicalTaxBucketDto { Rate = 0, Base = 50m, Tax = 0m },
                    ],
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var xml = (await db.EcfDocuments.SingleAsync()).SignedXmlContent!;

            Assert.Contains("<MontoGravadoTotal>350.00</MontoGravadoTotal>", xml);
            Assert.Contains("<MontoGravadoI1>100.00</MontoGravadoI1>", xml);
            Assert.Contains("<MontoGravadoI2>200.00</MontoGravadoI2>", xml);
            Assert.Contains("<MontoGravadoI3>50.00</MontoGravadoI3>", xml);
            Assert.Contains("<MontoExento>50.00</MontoExento>", xml);
            Assert.Contains("<ITBIS1>18</ITBIS1>", xml);
            Assert.Contains("<ITBIS2>16</ITBIS2>", xml);
            Assert.Contains("<ITBIS3>0</ITBIS3>", xml);
            Assert.Contains("<TotalITBIS>50.00</TotalITBIS>", xml);
            Assert.Contains("<TotalITBIS1>18.00</TotalITBIS1>", xml);
            Assert.Contains("<TotalITBIS2>32.00</TotalITBIS2>", xml);
            Assert.Contains("<TotalITBIS3>0.00</TotalITBIS3>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 31 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo31_WithARateDgiiHasNoBucketFor_IsRejectedInsteadOfFiledUnderTheNearestOne()
        {
            var (controller, db, _, _) = MakeRealController("E310000000807");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-31-TASA-RARA", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 100m, MontoGravadoTotal = 100m, MontoItbis = 10m, MontoTotal = 110m,
                    TaxBuckets = [new CanonicalTaxBucketDto { Rate = 10, Base = 100m, Tax = 10m }],
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.False(await db.EcfDocuments.AnyAsync());
        }

        [Fact]
        public async Task Tipo31_WithOnlyLegacyMontoSubtotal_KeepsPreSplitBehaviour()
        {
            var (controller, db, _, _) = MakeRealController("E310000000805");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-31-LEGACY", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 150m, MontoItbis = 18m, MontoTotal = 168m },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<MontoGravadoTotal>150.00</MontoGravadoTotal>", stored.SignedXmlContent!);
            Assert.DoesNotContain("<MontoExento>", stored.SignedXmlContent!);
        }

        [Fact]
        public async Task Tipo31_WithFechaEmisionAlreadyInDgiiFormat_PassesThroughUnchanged()
        {
            var (controller, db, _, _) = MakeRealController("E310000000803");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-31-DDMM", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                    FechaEmision = "06-08-2026",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<FechaEmision>06-08-2026</FechaEmision>", stored.SignedXmlContent!);
        }

        [Fact]
        public async Task Tipo34_WithIsoFechaNcfModificado_IsNormalizedAndValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E340000000802");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-34-ISO", EditSequence = "1" },
                TipoComprobante = "E34",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 50, MontoItbis = 9, MontoTotal = 59 },
                References = new CanonicalReferencesDto
                {
                    CorrectsENcf = "E310000000801",
                    CodigoModificacion = 3,
                    RazonModificacion = "error en precio",
                    FechaNcfModificado = "2026-08-06",
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<FechaNCFModificado>06-08-2026</FechaNCFModificado>", stored.SignedXmlContent!);

            var validation = new EcfSchemaValidator().Validate(stored.SignedXmlContent!, XsdPath("e-CF 34 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo34_ModifyingTipo46_WithoutBuyerRnc_GeneratesIdentificadorExtranjero_AndPassesXsd()
        {
            var (controller, db, _, _) = MakeRealController("E340000000803");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-34-E46-MOD", EditSequence = "1" },
                TipoComprobante = "E34",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001",
                    RazonSocialEmisor = "EMPRESA DEMO SRL",
                    RncComprador = "",
                    RazonSocialComprador = "FOREIGN CLIENT TEST",
                    FechaEmision = "22-10-2026",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 1500, MontoItbis = 0, MontoTotal = 1500 },
                References = new CanonicalReferencesDto
                {
                    CorrectsENcf = "E460000000045",
                    CodigoModificacion = 1,
                    RazonModificacion = "Anulación Total",
                    FechaNcfModificado = "2026-10-22",
                },
                Lines = [new CanonicalLineDto { LineNumber = 1, ItemName = "Item Export", Quantity = 1m, UnitPrice = 1500m, Amount = 1500m }]
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<TipoeCF>34</TipoeCF>", stored.SignedXmlContent!);
            Assert.Contains("<IdentificadorExtranjero>EXTRANJERO</IdentificadorExtranjero>", stored.SignedXmlContent!);
            Assert.DoesNotContain("<RNCComprador>", stored.SignedXmlContent!);
            Assert.Contains("<NCFModificado>E460000000045</NCFModificado>", stored.SignedXmlContent!);

            var validation = new EcfSchemaValidator().Validate(stored.SignedXmlContent!, XsdPath("e-CF 34 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo32_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E320000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-32", EditSequence = "1" },
                TipoComprobante = "E32",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RazonSocialComprador = "Consumidor Final",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100m, MontoItbis = 18m, MontoTotal = 118m },
                Lines = [new CanonicalLineDto { LineNumber = 1, ItemName = "Item", Quantity = 1m, UnitPrice = 100m, Amount = 100m }],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var xml = (await db.EcfDocuments.SingleAsync()).SignedXmlContent!;
            Assert.DoesNotContain("<FechaVencimientoSecuencia>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 32 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task LineWithoutQuantity_IsDeclaredAsOneUnit_BecauseDgiiForbidsZero()
        {
            var (controller, db, _, _) = MakeRealController("E310000000810");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-CANT0", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 500m, MontoItbis = 90m, MontoTotal = 590m },
                Lines =
                [
                    new CanonicalLineDto { LineNumber = 1, ItemName = "Normal", Quantity = 2m, UnitPrice = 200m, Amount = 400m },
                    new CanonicalLineDto { LineNumber = 2, ItemName = "Flete", Quantity = 0m, UnitPrice = 0m, Amount = 100m },
                ],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var xml = (await db.EcfDocuments.SingleAsync()).SignedXmlContent!;
            Assert.DoesNotContain("<CantidadItem>0.00</CantidadItem>", xml);
            Assert.Contains("<CantidadItem>2.00</CantidadItem>", xml);
            Assert.Contains("<CantidadItem>1.00</CantidadItem>", xml);
            Assert.Contains("<PrecioUnitarioItem>100.00</PrecioUnitarioItem>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 31 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task LinesWithZeroAmountOrZeroPrice_AreOmittedFromDgiiXml_AndXmlIsValidAgainstXsd()
        {
            var (controller, db, _, _) = MakeRealController("E310000000810");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-ZERO-LINES", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 400m, MontoItbis = 72m, MontoTotal = 472m },
                Lines =
                [
                    new CanonicalLineDto { LineNumber = 1, ItemName = "Producto Estándar 60x60", Quantity = 2m, UnitPrice = 200m, Amount = 400m },
                    new CanonicalLineDto { LineNumber = 2, ItemName = "Total Bultos", Quantity = 232.00m, UnitPrice = 0.00m, Amount = 0.00m },
                    new CanonicalLineDto { LineNumber = 3, ItemName = "REF-000001", Quantity = 0.00m, UnitPrice = 0.00m, Amount = 0.00m },
                    new CanonicalLineDto { LineNumber = 4, ItemName = "TOTAL METROS", Quantity = 733.92m, UnitPrice = 0.00m, Amount = 0.00m },
                ],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var xml = (await db.EcfDocuments.SingleAsync(d => d.SourceTxnId == "TXN-ZERO-LINES")).SignedXmlContent!;

            Assert.DoesNotContain("<NombreItem>Total Bultos</NombreItem>", xml);
            Assert.DoesNotContain("<NombreItem>REF-000001</NombreItem>", xml);
            Assert.DoesNotContain("<NombreItem>TOTAL METROS</NombreItem>", xml);

            Assert.Contains("<NombreItem>Producto Estándar 60x60</NombreItem>", xml);
            Assert.Contains("<NumeroLinea>1</NumeroLinea>", xml);
            Assert.DoesNotContain("<NumeroLinea>2</NumeroLinea>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 31 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo31_MixedTaxableAndExemptLines_EmitsCorrectIndicadorFacturacionPerLineAndPassesXsd()
        {
            var (controller, db, _, _) = MakeRealController("E310000000811");

            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { Provider = "QuickBooksDesktop", TxnId = "TXN-MIXED-LINES" },
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa de Prueba",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoGravadoTotal = 1178773.90m,
                    MontoExento = 120871.72m,
                    MontoItbis = 212179.30m,
                    MontoTotal = 1511824.92m
                },
                Lines =
                [
                    new CanonicalLineDto { LineNumber = 1, ItemName = "VEHICULO MODELO TEST", Quantity = 14m, UnitPrice = 84198.1357m, Amount = 1178773.90m, IndicadorFacturacion = "1" },
                    new CanonicalLineDto { LineNumber = 2, ItemName = "PRIMERA PLACA", Quantity = 14m, UnitPrice = 8490.8371m, Amount = 118871.72m, IndicadorFacturacion = "4" },
                    new CanonicalLineDto { LineNumber = 3, ItemName = "ENDOSO DE MATRICULA", Quantity = 1m, UnitPrice = 2000.00m, Amount = 2000.00m, IndicadorFacturacion = "4" },
                    new CanonicalLineDto { LineNumber = 4, ItemName = "MODELO TEST AZUL SERIE: TEST0000000000001", Quantity = 0m, UnitPrice = 0m, Amount = 0m },
                ],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var xml = (await db.EcfDocuments.SingleAsync(d => d.SourceTxnId == "TXN-MIXED-LINES")).SignedXmlContent!;

            Assert.DoesNotContain("TEST0000000000001", xml);

            Assert.Contains("<IndicadorFacturacion>1</IndicadorFacturacion>", xml);
            Assert.Contains("<IndicadorFacturacion>4</IndicadorFacturacion>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 31 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo31_WithoutRncComprador_IsRejectedBeforeSpendingASequence()
        {
            var (controller, db, _, _) = MakeRealController("E310000000811");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-SIN-RNC", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RazonSocialComprador = "Cliente Sin RNC",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100m, MontoItbis = 18m, MontoTotal = 118m },
            };

            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.False(await db.EcfDocuments.AnyAsync());
        }

        [Fact]
        public async Task Tipo32_WithNoBuyerDataAtAll_StillEmitsTheRequiredCompradorElement()
        {
            var (controller, db, _, _) = MakeRealController("E320000000802");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-32-SINCOMPRADOR", EditSequence = "1" },
                TipoComprobante = "E32",
                Header = new CanonicalHeaderDto { RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo" },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100m, MontoItbis = 18m, MontoTotal = 118m },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var xml = (await db.EcfDocuments.SingleAsync()).SignedXmlContent!;
            Assert.Contains("<Comprador>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 32 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo34_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E340000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-34", EditSequence = "1" },
                TipoComprobante = "E34",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 50, MontoItbis = 9, MontoTotal = 59 },
                References = new CanonicalReferencesDto
                {
                    CorrectsENcf = "E310000000801",
                    CodigoModificacion = 3,
                    RazonModificacion = "error en precio",
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var validation = new EcfSchemaValidator().Validate(stored.SignedXmlContent!, XsdPath("e-CF 34 v.1.0.xsd"));

            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo41_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E410000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-41", EditSequence = "1" },
                TipoComprobante = "E41",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000005", RazonSocialComprador = "Proveedor Informal SRL",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
                Retention = new CanonicalRetentionDto
                {
                    IndicadorAgenteRetencionoPercepcion = 1,
                    MontoItbisRetenido = 18m,
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var validation = new EcfSchemaValidator().Validate(stored.SignedXmlContent!, XsdPath("e-CF 41 v.1.0.xsd"));

            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task WithoutARealCertificate_DocumentIsMarkedUnsigned_AndNeverSentToDgii()
        {
            var (controller, db, signerSpy, ecfClientMock) = MakeRealController("E310000000808", useFallbackCertificate: true);
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-SIN-CERT", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100m, MontoItbis = 18m, MontoTotal = 118m },
            };

            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Unsigned", stored.State);
            Assert.False(string.IsNullOrEmpty(stored.SignedXmlContent));
            signerSpy.Verify(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            ecfClientMock.Verify(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task WithARealCertificate_DocumentIsStillTransmitted()
        {
            var (controller, db, _, ecfClientMock) = MakeRealController("E310000000809");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-CON-CERT", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100m, MontoItbis = 18m, MontoTotal = 118m },
            };

            await controller.SubmitCanonicalDocument(dto);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Signed", stored.State);
            ecfClientMock.Verify(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task StructurallyInvalidDocument_IsRejected_BeforeReachingDgii_WhenGateIsEnabled()
        {
            var (controller, db, signerSpy, ecfClientMock) = MakeRealController("E410000000900", enableXsdGate: true);

            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-INVALID", EditSequence = "1" },
                TipoComprobante = "E41",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000005", RazonSocialComprador = "Proveedor Informal SRL",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
                Retention = new CanonicalRetentionDto
                {
                    IndicadorAgenteRetencionoPercepcion = 99,
                    MontoItbisRetenido = 18m,
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<BadRequestObjectResult>(result);
            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("SchemaInvalid", stored.State);
            signerSpy.Verify(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            ecfClientMock.Verify(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task SchemaInvalidDocument_CanBeRetried_ReusingTheSameEncf()
        {
            var (controller, db, _, _) = MakeRealController("E410000000902", enableXsdGate: true);
            var badDto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-RECUPERABLE", EditSequence = "1" },
                TipoComprobante = "E41",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000005", RazonSocialComprador = "Proveedor Informal SRL",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100m, MontoItbis = 18m, MontoTotal = 118m },
                Retention = new CanonicalRetentionDto { IndicadorAgenteRetencionoPercepcion = 99, MontoItbisRetenido = 18m },
            };

            Assert.IsType<BadRequestObjectResult>(await controller.SubmitCanonicalDocument(badDto));
            Assert.Equal("SchemaInvalid", (await db.EcfDocuments.SingleAsync()).State);

            badDto.Retention!.IndicadorAgenteRetencionoPercepcion = 1;
            var retry = await controller.SubmitCanonicalDocument(badDto);

            Assert.True(retry is AcceptedResult, (retry as BadRequestObjectResult)?.Value?.ToString() ?? retry.GetType().Name);
            var recovered = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Signed", recovered.State);
            Assert.Equal("E410000000902", recovered.ENcf);
        }

        [Fact]
        public async Task LongItemDescription_PassesXsdValidation_ByTruncatingNombreItemAndAddingDescripcionItem()
        {
            var (controller, db, _, _) = MakeRealController("E310000000821");
            var dtoLongDesc = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-LONG-DESC", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
                Lines = [new CanonicalLineDto { LineNumber = 1, ItemName = new string('A', 120), Quantity = 1m, UnitPrice = 100m, Amount = 100m }],
            };
            var res = await controller.SubmitCanonicalDocument(dtoLongDesc);
            Assert.IsType<AcceptedResult>(res);

            var doc = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<NombreItem>" + new string('A', 80) + "</NombreItem>", doc.XmlContent);
            Assert.Contains("<DescripcionItem>" + new string('A', 120) + "</DescripcionItem>", doc.XmlContent);
        }

        [Fact]
        public async Task NegativeDiscountLine_PassesXsdValidation_ByFoldingIntoLineDiscount()
        {
            var (controller, db, _, _) = MakeRealController("E310000000822");
            var dtoNegLine = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-NEG-LINE", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
                Lines = [
                    new CanonicalLineDto { LineNumber = 1, ItemName = "Item Normal", Quantity = 2m, UnitPrice = 100m, Amount = 200m },
                    new CanonicalLineDto { LineNumber = 2, ItemName = "Descuento 50%", Quantity = 1m, UnitPrice = -100m, Amount = -100m },
                ],
            };
            var res = await controller.SubmitCanonicalDocument(dtoNegLine);
            Assert.IsType<AcceptedResult>(res);

            var doc = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<DescuentoMonto>100.00</DescuentoMonto>", doc.XmlContent);
            Assert.Contains("<TablaSubDescuento>", doc.XmlContent);
            Assert.Contains("<MontoSubDescuento>100.00</MontoSubDescuento>", doc.XmlContent);
            Assert.DoesNotContain("<PrecioUnitarioItem>-", doc.XmlContent);
            Assert.DoesNotContain("<MontoItem>-", doc.XmlContent);
        }

        [Fact]
        public async Task LongCustomerName_PassesXsdValidation_ByTruncatingRazonSocial()
        {
            var (controller, db, _, _) = MakeRealController("E310000000824");
            var dtoLongCust = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-LONG-CUST", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = new string('B', 180),
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
                Lines = [new CanonicalLineDto { LineNumber = 1, ItemName = "Item Normal", Quantity = 1m, UnitPrice = 100m, Amount = 100m }],
            };
            var res = await controller.SubmitCanonicalDocument(dtoLongCust);
            Assert.IsType<AcceptedResult>(res);

            var doc = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<RazonSocialComprador>" + new string('B', 150) + "</RazonSocialComprador>", doc.XmlContent);
        }

        [Fact]
        public async Task Tipo46_WithoutBuyerRnc_GeneratesIdentificadorExtranjero_AndPassesXsd()
        {
            var (controller, db, _, _) = MakeRealController("E460000000028");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-E46-FOREIGN", EditSequence = "1" },
                TipoComprobante = "E46",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001",
                    RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "",
                    RazonSocialComprador = "Foreign Client Corp",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 500, MontoTotal = 500 },
                Lines = [new CanonicalLineDto { LineNumber = 1, ItemName = "Export Item", Quantity = 1m, UnitPrice = 500m, Amount = 500m }],
            };

            var res = await controller.SubmitCanonicalDocument(dto);
            Assert.IsType<AcceptedResult>(res);

            var doc = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<IdentificadorExtranjero>EXTRANJERO</IdentificadorExtranjero>", doc.XmlContent);
            Assert.DoesNotContain("<RNCComprador>", doc.XmlContent);
        }

        [Fact]
        public async Task Tipo33_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E330000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-33", EditSequence = "1" },
                TipoComprobante = "E33",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
                References = new CanonicalReferencesDto
                {
                    CorrectsENcf = "E310000000801",
                    CodigoModificacion = 1,
                    RazonModificacion = "Cargo adicional por mora",
                    FechaNcfModificado = "2026-08-06",
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<TipoeCF>33</TipoeCF>", stored.SignedXmlContent!);
            Assert.Contains("<InformacionReferencia>", stored.SignedXmlContent!);
            Assert.Contains("<NCFModificado>E310000000801</NCFModificado>", stored.SignedXmlContent!);

            var validation = new EcfSchemaValidator().Validate(stored.SignedXmlContent!, XsdPath("e-CF 33 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo33_WithoutReferences_IsRejectedBeforeSpendingASequence()
        {
            var (controller, db, _, _) = MakeRealController("E330000000802");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-33-NOREF", EditSequence = "1" },
                TipoComprobante = "E33",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.False(await db.EcfDocuments.AnyAsync());
        }

        [Fact]
        public async Task Tipo43_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E430000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-43", EditSequence = "1" },
                TipoComprobante = "E43",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 500m, MontoItbis = 0m, MontoTotal = 500m },
                Lines = [new CanonicalLineDto { LineNumber = 1, ItemName = "Café y azúcar para oficina", Quantity = 1m, UnitPrice = 500m, Amount = 500m }],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var xml = stored.SignedXmlContent!;

            Assert.Contains("<TipoeCF>43</TipoeCF>", xml);
            Assert.DoesNotContain("<Comprador>", xml);
            Assert.DoesNotContain("<TipoIngresos>", xml);
            Assert.DoesNotContain("<MontoGravadoTotal>", xml);
            Assert.Contains("<MontoExento>500.00</MontoExento>", xml);
            Assert.Contains("<MontoTotal>500.00</MontoTotal>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 43 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo44_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E440000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-44", EditSequence = "1" },
                TipoComprobante = "E44",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000000", RazonSocialComprador = "Zona Franca del Norte SAS",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 1000m, MontoItbis = 0m, MontoTotal = 1000m },
                Lines = [new CanonicalLineDto { LineNumber = 1, ItemName = "Prendas de vestir zona franca", Quantity = 10m, UnitPrice = 100m, Amount = 1000m }],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var xml = stored.SignedXmlContent!;

            Assert.Contains("<TipoeCF>44</TipoeCF>", xml);
            Assert.Contains("<Comprador>", xml);
            Assert.Contains("<MontoExento>1000.00</MontoExento>", xml);
            Assert.Contains("<MontoTotal>1000.00</MontoTotal>", xml);
            Assert.DoesNotContain("<TotalITBIS>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 44 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo45_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E450000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-45", EditSequence = "1" },
                TipoComprobante = "E45",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "401007421", RazonSocialComprador = "Ministerio de Hacienda",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 1000m, MontoItbis = 180m, MontoTotal = 1180m },
                Lines = [new CanonicalLineDto { LineNumber = 1, ItemName = "Servicio Institucional", Quantity = 1m, UnitPrice = 1000m, Amount = 1000m }],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var xml = stored.SignedXmlContent!;

            Assert.Contains("<TipoeCF>45</TipoeCF>", xml);
            Assert.Contains("<RNCComprador>401007421</RNCComprador>", xml);
            Assert.Contains("<MontoGravadoTotal>1000.00</MontoGravadoTotal>", xml);
            Assert.Contains("<TotalITBIS>180.00</TotalITBIS>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 45 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo45_WithoutRncComprador_IsRejectedBeforeSpendingASequence()
        {
            var (controller, db, _, _) = MakeRealController("E450000000802");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-45-NORNC", EditSequence = "1" },
                TipoComprobante = "E45",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RazonSocialComprador = "Institucion Sin RNC",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 1000m, MontoItbis = 180m, MontoTotal = 1180m },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.False(await db.EcfDocuments.AnyAsync());
        }

        [Fact]
        public async Task Tipo46_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E460000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-46", EditSequence = "1" },
                TipoComprobante = "E46",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "US-987654321", RazonSocialComprador = "Miami Apparel Imports LLC",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 2500m, MontoItbis = 0m, MontoTotal = 2500m },
                Lines = [new CanonicalLineDto { LineNumber = 1, ItemName = "Exportación Lote Textiles", Quantity = 25m, UnitPrice = 100m, Amount = 2500m }],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var xml = stored.SignedXmlContent!;

            Assert.Contains("<TipoeCF>46</TipoeCF>", xml);
            Assert.Contains("<IdentificadorExtranjero>US-987654321</IdentificadorExtranjero>", xml);
            Assert.Contains("<MontoGravadoTotal>2500.00</MontoGravadoTotal>", xml);
            Assert.Contains("<MontoGravadoI3>2500.00</MontoGravadoI3>", xml);
            Assert.Contains("<ITBIS3>0</ITBIS3>", xml);
            Assert.Contains("<TotalITBIS>0.00</TotalITBIS>", xml);
            Assert.Contains("<TotalITBIS3>0.00</TotalITBIS3>", xml);
            Assert.Contains("<MontoTotal>2500.00</MontoTotal>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 46 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo46_WhenLineItemHasTaxRate18_ForcesIndicadorFacturacionTo3_AndPassesXsd()
        {
            var (controller, db, _, _) = MakeRealController("E460000000802");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-46-RULE244", EditSequence = "1" },
                TipoComprobante = "E46",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "US-987654321", RazonSocialComprador = "Miami Apparel Imports LLC",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 176000m, MontoItbis = 0m, MontoTotal = 176000m },
                Lines = [new CanonicalLineDto
                {
                    LineNumber = 1,
                    ItemName = "PRODUCTO EXPORTACION DEMO",
                    Quantity = 200m,
                    UnitPrice = 880m,
                    Amount = 176000m,
                    IndicadorFacturacion = "1"
                }],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync(d => d.ENcf == "E460000000802");
            var xml = stored.SignedXmlContent!;

            Assert.Contains("<IndicadorFacturacion>3</IndicadorFacturacion>", xml);
            Assert.DoesNotContain("<IndicadorFacturacion>1</IndicadorFacturacion>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 46 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo46_WhenLineItemHasTaxRateExempt4_ForcesIndicadorFacturacionTo3_AndPassesXsd()
        {
            var (controller, db, _, _) = MakeRealController("E460000000803");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-46-RULE244-EXEMPT", EditSequence = "1" },
                TipoComprobante = "E46",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "US-987654321", RazonSocialComprador = "Miami Apparel Imports LLC",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 176000m, MontoItbis = 0m, MontoTotal = 176000m },
                Lines = [new CanonicalLineDto
                {
                    LineNumber = 1,
                    ItemName = "PRODUCTO EXPORTACION DEMO",
                    Quantity = 200m,
                    UnitPrice = 880m,
                    Amount = 176000m,
                    IndicadorFacturacion = "4"
                }],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync(d => d.ENcf == "E460000000803");
            var xml = stored.SignedXmlContent!;

            Assert.Contains("<IndicadorFacturacion>3</IndicadorFacturacion>", xml);
            Assert.DoesNotContain("<IndicadorFacturacion>4</IndicadorFacturacion>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 46 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo47_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E470000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-47", EditSequence = "1" },
                TipoComprobante = "E47",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001", RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "DE-123456789", RazonSocialComprador = "Software Cloud GmbH",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 3000m, MontoItbis = 0m, MontoTotal = 3000m },
                Lines = [new CanonicalLineDto { LineNumber = 1, ItemName = "Licencia de Software Extranjero", Quantity = 1m, UnitPrice = 3000m, Amount = 3000m }],
                Retention = new CanonicalRetentionDto
                {
                    IndicadorAgenteRetencionoPercepcion = 1,
                    MontoIsrRetenido = 300m,
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var xml = stored.SignedXmlContent!;

            Assert.Contains("<TipoeCF>47</TipoeCF>", xml);
            Assert.DoesNotContain("<TipoIngresos>", xml);
            Assert.DoesNotContain("<RNCComprador>", xml);
            Assert.Contains("<IdentificadorExtranjero>DE-123456789</IdentificadorExtranjero>", xml);
            Assert.Contains("<MontoExento>3000.00</MontoExento>", xml);
            Assert.Contains("<TotalISRRetencion>300.00</TotalISRRetencion>", xml);
            Assert.Contains("<MontoISRRetenido>300.00</MontoISRRetenido>", xml);
            Assert.DoesNotContain("<MontoITBISRetenido>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 47 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public void Arecf_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var unsignedArecf = @"<?xml version=""1.0"" encoding=""utf-8""?>
<ARECF>
  <DetalleAcusedeRecibo>
    <Version>1.0</Version>
    <RNCEmisor>101000001</RNCEmisor>
    <RNCComprador>130000000</RNCComprador>
    <eNCF>E310000000801</eNCF>
    <Estado>0</Estado>
    <FechaHoraAcuseRecibo>10-09-2026 21:00:00</FechaHoraAcuseRecibo>
  </DetalleAcusedeRecibo>
</ARECF>";

            using var arecfRsa = RSA.Create(2048);
            var arecfReq = new CertificateRequest("CN=130000000, O=Test Comprador, C=DO", arecfRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var arecfCert = arecfReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(arecfCert);
            var signedArecf = signer.SignXml(unsignedArecf, "130000000");

            var validation = new EcfSchemaValidator().Validate(signedArecf, XsdPath("ARECF v1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task SubmitCanonicalDocument_Tipo32_GeneratesXmlPassingDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E320000000101");
            var dto = new CanonicalDocumentDto
            {
                TipoComprobante = "E32",
                SourceReference = new SourceReferenceDto { TxnId = "TXN-32" },
                Header = new CanonicalHeaderDto
                {
                    FechaEmision = "2026-09-10",
                    RncComprador = "101000001",
                    RazonSocialComprador = "Juan Pérez Consumidor",
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 500m,
                    MontoGravadoTotal = 500m,
                    MontoItbis = 90m,
                    MontoTotal = 590m,
                    TaxBuckets = new List<CanonicalTaxBucketDto>
                    {
                        new() { Rate = 18, Base = 500m, Tax = 90m },
                    },
                },
                Lines = new List<CanonicalLineDto>
                {
                    new()
                    {
                        LineNumber = 1,
                        ItemName = "Producto Consumo",
                        Quantity = 1m,
                        UnitPrice = 500m,
                        Amount = 500m,
                    },
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var xml = stored.SignedXmlContent!;

            Assert.Contains("<TipoeCF>32</TipoeCF>", xml);
            Assert.Contains("<TipoIngresos>01</TipoIngresos>", xml);
            Assert.DoesNotContain("<FechaVencimientoSecuencia>", xml);
            Assert.Contains("<MontoTotal>590.00</MontoTotal>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 32 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task SubmitCanonicalDocument_Tipo32_Over250k_WithoutRnc_ReturnsBadRequest()
        {
            var (controller, _, _, _) = MakeRealController("E320000000002");
            var dto = new CanonicalDocumentDto
            {
                TipoComprobante = "E32",
                SourceReference = new SourceReferenceDto { TxnId = "TXN-32-OVER250K-FAIL" },
                Header = new CanonicalHeaderDto
                {
                    FechaEmision = "2026-09-10",
                    RncComprador = "",
                    RazonSocialComprador = "Consumidor Grande",
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 250000m,
                    MontoTotal = 295000m,
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task SubmitCanonicalDocument_Tipo32_Over250k_WithForeignPassport_GeneratesXmlPassingDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E320000000003");
            var dto = new CanonicalDocumentDto
            {
                TipoComprobante = "E32",
                SourceReference = new SourceReferenceDto { TxnId = "TXN-32-PASSPORT" },
                Header = new CanonicalHeaderDto
                {
                    FechaEmision = "10/09/2026",
                    RncComprador = "PASSPORT-A987654",
                    RazonSocialComprador = "Turista Extranjero",
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 260000m,
                    MontoGravadoTotal = 260000m,
                    MontoItbis = 46800m,
                    MontoTotal = 306800m,
                    TaxBuckets = new List<CanonicalTaxBucketDto>
                    {
                        new() { Rate = 18, Base = 260000m, Tax = 46800m },
                    },
                },
                Lines = new List<CanonicalLineDto>
                {
                    new()
                    {
                        LineNumber = 1,
                        ItemName = "Joyería Fina",
                        Quantity = 1m,
                        UnitPrice = 260000m,
                        Amount = 260000m,
                    },
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var xml = stored.SignedXmlContent!;

            Assert.Contains("<IdentificadorExtranjero>PASSPORT-A987654</IdentificadorExtranjero>", xml);
            Assert.Contains("<FechaEmision>10-09-2026</FechaEmision>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 32 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task SubmitCanonicalDocument_Tipo41_WithRetention_EmitsTotalsRetencionAndPassesXsd()
        {
            var (controller, db, _, _) = MakeRealController("E410000000802");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-41-RET-TOTALS" },
                TipoComprobante = "E41",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001",
                    RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000005",
                    RazonSocialComprador = "Proveedor Informal SRL",
                    FechaEmision = "2026-09-10",
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 1000m,
                    MontoGravadoTotal = 1000m,
                    MontoItbis = 180m,
                    MontoTotal = 1180m,
                    TaxBuckets = new List<CanonicalTaxBucketDto>
                    {
                        new() { Rate = 18, Base = 1000m, Tax = 180m },
                    },
                },
                Retention = new CanonicalRetentionDto
                {
                    IndicadorAgenteRetencionoPercepcion = 1,
                    MontoItbisRetenido = 180m,
                    MontoIsrRetenido = 100m,
                },
                Lines = new List<CanonicalLineDto>
                {
                    new()
                    {
                        LineNumber = 1,
                        ItemName = "Servicio Técnico Informal",
                        Quantity = 1m,
                        UnitPrice = 1000m,
                        Amount = 1000m,
                    },
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var xml = stored.SignedXmlContent!;

            Assert.Contains("<TotalITBISRetenido>180.00</TotalITBISRetenido>", xml);
            Assert.Contains("<TotalISRRetencion>100.00</TotalISRRetencion>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 41 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task SubmitCanonicalDocument_Tipo41_WithOnlyIsrRetention_EmitsZeroTotalITBISRetenido_AndPassesXsd()
        {
            var (controller, db, _, _) = MakeRealController("E410000000803");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-41-ISR-ONLY" },
                TipoComprobante = "E41",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001",
                    RazonSocialEmisor = "Empresa Demo",
                    RncComprador = "130000005",
                    RazonSocialComprador = "Proveedor Persona Física",
                    FechaEmision = "2026-10-20",
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 122000m,
                    MontoExento = 122000m,
                    MontoItbis = 0m,
                    MontoTotal = 122000m,
                },
                Retention = new CanonicalRetentionDto
                {
                    IndicadorAgenteRetencionoPercepcion = 1,
                    MontoItbisRetenido = null,
                    MontoIsrRetenido = 3660m,
                },
                Lines = new List<CanonicalLineDto>
                {
                    new()
                    {
                        LineNumber = 1,
                        ItemName = "Transporte y Envios",
                        Quantity = 1m,
                        UnitPrice = 122000m,
                        Amount = 122000m,
                        IndicadorFacturacion = "4",
                    },
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var xml = stored.SignedXmlContent!;

            Assert.Contains("<TotalITBISRetenido>0.00</TotalITBISRetenido>", xml);
            Assert.Contains("<MontoITBISRetenido>0.00</MontoITBISRetenido>", xml);

            Assert.Contains("<TotalISRRetencion>3660.00</TotalISRRetencion>", xml);
            Assert.Contains("<MontoISRRetenido>3660.00</MontoISRRetenido>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 41 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo31_And_Tipo32_WithCorreoComprador_IsValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E310000000301");

            var dto = new CanonicalDocumentDto
            {
                DocumentKind = "Invoice",
                TipoComprobante = "E31",
                SourceReference = new SourceReferenceDto
                {
                    TxnId = "TXN-EMAIL-31",
                    EditSequence = "1",
                },
                Header = new CanonicalHeaderDto
                {
                    RncComprador = "101010101",
                    RazonSocialComprador = "COMPRADOR CORP SRL",
                    CorreoComprador = "facturacion@comprador.com",
                    FechaEmision = "2026-04-15",
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoGravadoTotal = 1000m,
                    MontoExento = 0m,
                    MontoItbis = 180m,
                    MontoTotal = 1180m,
                    TaxBuckets =
                    [
                        new() { Rate = 18, Base = 1000m, Tax = 180m }
                    ],
                },
                Lines =
                [
                    new()
                    {
                        LineNumber = 1,
                        ItemName = "Licencia de Software",
                        Quantity = 1m,
                        UnitPrice = 1000m,
                        Amount = 1000m,
                    },
                ],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            var xml = stored.SignedXmlContent!;

            Assert.Contains("<CorreoComprador>facturacion@comprador.com</CorreoComprador>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 31 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo43_UsdForeignCurrency_Bill_EmitsDopTotals_OtraMoneda_And_PassesDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E430000000014");

            var dto = new CanonicalDocumentDto
            {
                DocumentKind = "Bill",
                TipoComprobante = "E43",
                Ncf = "B1300002709",
                SourceReference = new SourceReferenceDto
                {
                    TxnId = "AE1AE7-1793636542",
                    EditSequence = "B1300002709",
                },
                Header = new CanonicalHeaderDto
                {
                    FechaEmision = "2026-10-30",
                    RazonSocialComprador = "CLIENTE MONEDA EXTRANJERA SRL",
                },
                Currency = new CanonicalCurrencyDto
                {
                    Code = "USD",
                    ExchangeRate = 60.16m,
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 10807.02m,
                    MontoExento = 10807.02m,
                    MontoItbis = 0m,
                    MontoTotal = 10807.02m,
                },
                Lines =
                [
                    new()
                    {
                        LineNumber = 1,
                        ItemName = "6215 · Servicios de Informatica",
                        Quantity = 1m,
                        UnitPrice = 10807.02m,
                        Amount = 10807.02m,
                        IndicadorFacturacion = "4",
                    },
                ],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal(650150.32m, stored.TotalAmount);
            Assert.Equal(0m, stored.ItbisAmount);

            var xml = stored.SignedXmlContent!;
            Assert.Contains("<MontoExento>650150.32</MontoExento>", xml);
            Assert.Contains("<MontoTotal>650150.32</MontoTotal>", xml);
            Assert.Contains("<TipoMoneda>USD</TipoMoneda>", xml);
            Assert.Contains("<TipoCambio>60.16</TipoCambio>", xml);
            Assert.Contains("<MontoExentoOtraMoneda>10807.02</MontoExentoOtraMoneda>", xml);
            Assert.Contains("<MontoTotalOtraMoneda>10807.02</MontoTotalOtraMoneda>", xml);
            Assert.Contains("<PrecioUnitarioItem>650150.32</PrecioUnitarioItem>", xml);
            Assert.Contains("<PrecioOtraMoneda>10807.02</PrecioOtraMoneda>", xml);
            Assert.Contains("<MontoItemOtraMoneda>10807.02</MontoItemOtraMoneda>", xml);
            Assert.Contains("<MontoItem>650150.32</MontoItem>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 43 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo31_UsdForeignCurrency_TaxedAndExemptLines_PassesDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E310000000401");

            var dto = new CanonicalDocumentDto
            {
                DocumentKind = "Invoice",
                TipoComprobante = "E31",
                SourceReference = new SourceReferenceDto
                {
                    TxnId = "TXN-USD-31",
                    EditSequence = "1",
                },
                Header = new CanonicalHeaderDto
                {
                    RncComprador = "101010101",
                    RazonSocialComprador = "COMPRADOR USD SRL",
                    FechaEmision = "2026-10-30",
                },
                Currency = new CanonicalCurrencyDto
                {
                    Code = "USD",
                    ExchangeRate = 60.16m,
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 150m,
                    MontoGravadoTotal = 100m,
                    MontoExento = 50m,
                    MontoItbis = 18m,
                    MontoTotal = 168m,
                    TaxBuckets =
                    [
                        new() { Rate = 18, Base = 100m, Tax = 18m }
                    ],
                },
                Lines =
                [
                    new()
                    {
                        LineNumber = 1,
                        ItemName = "Consultoría Técnica",
                        Quantity = 2m,
                        UnitPrice = 50m,
                        Amount = 100m,
                        IndicadorFacturacion = "1",
                    },
                    new()
                    {
                        LineNumber = 2,
                        ItemName = "Servicio Exento",
                        Quantity = 1m,
                        UnitPrice = 50m,
                        Amount = 50m,
                        IndicadorFacturacion = "4",
                    },
                ],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal(10106.88m, stored.TotalAmount);
            Assert.Equal(1082.88m, stored.ItbisAmount);

            var xml = stored.SignedXmlContent!;
            Assert.Contains("<MontoGravadoTotal>6016.00</MontoGravadoTotal>", xml);
            Assert.Contains("<MontoExento>3008.00</MontoExento>", xml);
            Assert.Contains("<TotalITBIS>1082.88</TotalITBIS>", xml);
            Assert.Contains("<MontoTotal>10106.88</MontoTotal>", xml);
            Assert.Contains("<MontoGravadoTotalOtraMoneda>100.00</MontoGravadoTotalOtraMoneda>", xml);
            Assert.Contains("<MontoExentoOtraMoneda>50.00</MontoExentoOtraMoneda>", xml);
            Assert.Contains("<TotalITBISOtraMoneda>18.00</TotalITBISOtraMoneda>", xml);
            Assert.Contains("<MontoTotalOtraMoneda>168.00</MontoTotalOtraMoneda>", xml);

            var validation = new EcfSchemaValidator().Validate(xml, XsdPath("e-CF 31 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo46_And_Tipo47_UsdForeignCurrency_PassRespectiveDgiiXsds()
        {
            var (c46, db46, _, _) = MakeRealController("E460000000099");
            var dto46 = new CanonicalDocumentDto
            {
                DocumentKind = "Invoice",
                TipoComprobante = "E46",
                SourceReference = new SourceReferenceDto { TxnId = "TXN-USD-46", EditSequence = "1" },
                Header = new CanonicalHeaderDto
                {
                    IdentificadorExtranjero = "EXTRANJERO",
                    RazonSocialComprador = "FOREIGN BUYER LLC",
                    FechaEmision = "2026-10-30",
                },
                Currency = new CanonicalCurrencyDto { Code = "USD", ExchangeRate = 60.16m },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 1000m, MontoGravadoTotal = 1000m, MontoItbis = 0m, MontoTotal = 1000m },
                Lines = [new() { LineNumber = 1, ItemName = "Export Mercancía", Quantity = 10m, UnitPrice = 100m, Amount = 1000m, IndicadorFacturacion = "3" }],
            };

            var res46 = await c46.SubmitCanonicalDocument(dto46);
            Assert.True(res46 is AcceptedResult, (res46 as BadRequestObjectResult)?.Value?.ToString() ?? res46.GetType().Name);
            var xml46 = (await db46.EcfDocuments.SingleAsync()).SignedXmlContent!;
            Assert.Contains("<MontoGravado3OtraMoneda>1000.00</MontoGravado3OtraMoneda>", xml46);
            Assert.Contains("<MontoTotal>60160.00</MontoTotal>", xml46);
            var val46 = new EcfSchemaValidator().Validate(xml46, XsdPath("e-CF 46 v.1.0.xsd"));
            Assert.True(val46.IsValid, string.Join("\n", val46.Errors));

            var (c47, db47, _, _) = MakeRealController("E470000000099");
            var dto47 = new CanonicalDocumentDto
            {
                DocumentKind = "Bill",
                TipoComprobante = "E47",
                SourceReference = new SourceReferenceDto { TxnId = "TXN-USD-47", EditSequence = "1" },
                Header = new CanonicalHeaderDto
                {
                    IdentificadorExtranjero = "US-99887766",
                    RazonSocialComprador = "SAAS VENDOR INC",
                    FechaEmision = "2026-10-30",
                },
                Currency = new CanonicalCurrencyDto { Code = "USD", ExchangeRate = 60.16m },
                Retention = new CanonicalRetentionDto { IndicadorAgenteRetencionoPercepcion = 1, MontoIsrRetenido = 270m },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 1000m, MontoExento = 1000m, MontoTotal = 1000m },
                Lines = [new() { LineNumber = 1, ItemName = "Suscripción Anual Cloud", Quantity = 1m, UnitPrice = 1000m, Amount = 1000m, IndicadorFacturacion = "4" }],
            };

            var res47 = await c47.SubmitCanonicalDocument(dto47);
            Assert.True(res47 is AcceptedResult, (res47 as BadRequestObjectResult)?.Value?.ToString() ?? res47.GetType().Name);
            var xml47 = (await db47.EcfDocuments.SingleAsync()).SignedXmlContent!;
            Assert.Contains("<TotalISRRetencion>16243.20</TotalISRRetencion>", xml47);
            Assert.Contains("<MontoISRRetenido>16243.20</MontoISRRetenido>", xml47);
            Assert.Contains("<MontoExentoOtraMoneda>1000.00</MontoExentoOtraMoneda>", xml47);
            var val47 = new EcfSchemaValidator().Validate(xml47, XsdPath("e-CF 47 v.1.0.xsd"));
            Assert.True(val47.IsValid, string.Join("\n", val47.Errors));
        }

        [Fact]
        public async Task Tipo32_UsdForeignCurrency_Over250kDopEquivalent_RequiresRncComprador()
        {
            var (controller, _, _, _) = MakeRealController("E320000000999");
            var dto = new CanonicalDocumentDto
            {
                DocumentKind = "Invoice",
                TipoComprobante = "E32",
                SourceReference = new SourceReferenceDto { TxnId = "TXN-USD-32-HIGH", EditSequence = "1" },
                Header = new CanonicalHeaderDto
                {
                    RazonSocialComprador = "CONSUMIDOR FINAL",
                    FechaEmision = "2026-10-30",
                },
                Currency = new CanonicalCurrencyDto { Code = "USD", ExchangeRate = 60.16m },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 5000m, MontoExento = 5000m, MontoTotal = 5000m },
                Lines = [new() { LineNumber = 1, ItemName = "Equipo Industrial", Quantity = 1m, UnitPrice = 5000m, Amount = 5000m, IndicadorFacturacion = "4" }],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task InvalidForeignCurrencyOrZeroRate_ReturnsBadRequest()
        {
            var (controller, db, _, _) = MakeRealController("E430000000999");
            var dto = new CanonicalDocumentDto
            {
                DocumentKind = "Bill",
                TipoComprobante = "E43",
                SourceReference = new SourceReferenceDto { TxnId = "TXN-BAD-CURR", EditSequence = "1" },
                Header = new CanonicalHeaderDto { FechaEmision = "2026-10-30" },
                Currency = new CanonicalCurrencyDto { Code = "USD", ExchangeRate = 0m },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100m, MontoExento = 100m, MontoTotal = 100m },
                Lines = [new() { LineNumber = 1, ItemName = "Gasto", Quantity = 1m, UnitPrice = 100m, Amount = 100m }],
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(await db.EcfDocuments.ToListAsync());
        }

        [Fact]
        public async Task Tipo31_WithCodigoModificacion3_ReturnsBadRequestBeforeSequenceAllocation_DgiiRule613()
        {
            var (controller, db, _, _) = MakeRealController("E310000000293");
            var dto = new CanonicalDocumentDto
            {
                DocumentKind = "Invoice",
                TipoComprobante = "E31",
                SourceReference = new SourceReferenceDto { TxnId = "CFB4A-1793645147", EditSequence = "1" },
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001",
                    RazonSocialEmisor = "EMPRESA DEMO SRL",
                    RncComprador = "130000010",
                    RazonSocialComprador = "DISTRIBUIDORA DEMO SA",
                    FechaEmision = "2026-11-02"
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 870472.70m,
                    MontoGravadoTotal = 870472.70m,
                    MontoItbis = 156685.09m,
                    MontoTotal = 1027157.79m
                },
                Lines = [new() { LineNumber = 1, ItemName = "PRODUCTO COMERCIAL A", Quantity = 1m, UnitPrice = 870472.70m, Amount = 870472.70m, IndicadorFacturacion = "1" }],
                References = new CanonicalReferencesDto
                {
                    CorrectsENcf = "B0400022008",
                    CodigoModificacion = 3,
                    RazonModificacion = "Corrección de Montos",
                    FechaNcfModificado = "2026-11-02"
                }
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            var badReq = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("613", badReq.Value?.ToString() ?? string.Empty);
            Assert.Empty(await db.EcfDocuments.ToListAsync());
        }

        [Fact]
        public async Task PreFoldedNetDiscountLines_ReconstructGrossUnitPriceAndPassXsdValidation()
        {
            var (controller31, db31, _, _) = MakeRealController("E310000000907");
            var dto31 = new CanonicalDocumentDto
            {
                DocumentKind = "Invoice",
                TipoComprobante = "E31",
                SourceReference = new SourceReferenceDto { TxnId = "CFB68-1793645512", EditSequence = "1" },
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001",
                    RazonSocialEmisor = "EMPRESA DEMO SRL",
                    RncComprador = "130000011",
                    RazonSocialComprador = "COMERCIAL COMPRADOR SRL",
                    FechaEmision = "2026-11-02"
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 20183.10m,
                    MontoGravadoTotal = 20183.10m,
                    MontoItbis = 3632.96m,
                    MontoTotal = 23816.06m,
                    TaxBuckets = [new() { Rate = 18, Base = 20183.10m, Tax = 3632.96m }]
                },
                Lines =
                [
                    new()
                    {
                        LineNumber = 1,
                        ItemName = "PRODUCTO ALIMENTICIO TIPO A",
                        Quantity = 25m,
                        UnitPrice = 807.324m,
                        Amount = 20183.10m,
                        DiscountAmount = 411.90m,
                        IndicadorFacturacion = "1"
                    }
                ]
            };

            var res31 = await controller31.SubmitCanonicalDocument(dto31);
            Assert.IsType<AcceptedResult>(res31);
            var doc31 = await db31.EcfDocuments.SingleAsync();
            Assert.Contains("<PrecioUnitarioItem>823.80</PrecioUnitarioItem>", doc31.XmlContent);
            Assert.Contains("<DescuentoMonto>411.90</DescuentoMonto>", doc31.XmlContent);
            Assert.Contains("<MontoItem>20183.10</MontoItem>", doc31.XmlContent);
            var val31 = new EcfSchemaValidator().Validate(doc31.SignedXmlContent!, XsdPath("e-CF 31 v.1.0.xsd"));
            Assert.True(val31.IsValid, string.Join("\n", val31.Errors));

            var (controller32, db32, _, _) = MakeRealController("E320000000316");
            var dto32 = new CanonicalDocumentDto
            {
                DocumentKind = "Invoice",
                TipoComprobante = "E32",
                SourceReference = new SourceReferenceDto { TxnId = "CFC46-1793654457", EditSequence = "1" },
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001",
                    RazonSocialEmisor = "EMPRESA DEMO SRL",
                    RazonSocialComprador = "CONSUMIDOR DEMO",
                    FechaEmision = "2026-11-02"
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 246.33m,
                    MontoGravadoTotal = 246.33m,
                    MontoItbis = 44.34m,
                    MontoTotal = 290.67m,
                    TaxBuckets = [new() { Rate = 18, Base = 246.33m, Tax = 44.34m }]
                },
                Lines =
                [
                    new() { LineNumber = 1, ItemName = "PRODUCTO LOTE A", Quantity = 0.1m, UnitPrice = 625.4m, Amount = 62.54m, DiscountAmount = 6.95m, IndicadorFacturacion = "1" },
                    new() { LineNumber = 2, ItemName = "PRODUCTO LOTE B", Quantity = 0.0833m, UnitPrice = 741.42m, Amount = 61.76m, DiscountAmount = 6.86m, IndicadorFacturacion = "1" },
                    new() { LineNumber = 3, ItemName = "PRODUCTO LOTE C", Quantity = 0.16666m, UnitPrice = 732.21m, Amount = 122.03m, DiscountAmount = 13.56m, IndicadorFacturacion = "1" }
                ]
            };

            var res32 = await controller32.SubmitCanonicalDocument(dto32);
            Assert.IsType<AcceptedResult>(res32);
            var doc32 = await db32.EcfDocuments.SingleAsync();
            Assert.Contains("<DescuentoMonto>6.95</DescuentoMonto>", doc32.XmlContent);
            Assert.Contains("<DescuentoMonto>6.86</DescuentoMonto>", doc32.XmlContent);
            Assert.Contains("<DescuentoMonto>13.56</DescuentoMonto>", doc32.XmlContent);
            var val32 = new EcfSchemaValidator().Validate(doc32.SignedXmlContent!, XsdPath("e-CF 32 v.1.0.xsd"));
            Assert.True(val32.IsValid, string.Join("\n", val32.Errors));
        }

        [Fact]
        public async Task Tipo34_CancellingTipo43InDop_AndReissuingTipo43InUsd_PassDgiiXsd()
        {
            var (controller34, db34, _, _) = MakeRealController("E340000000364");
            var dto34 = new CanonicalDocumentDto
            {
                DocumentKind = "CreditNote",
                TipoComprobante = "E34",
                SourceReference = new SourceReferenceDto { TxnId = "NC-ANULA-E430000000014", EditSequence = "1" },
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001",
                    RazonSocialEmisor = "EMPRESA DEMO SRL",
                    RazonSocialComprador = "CLIENTE MONEDA EXTRANJERA SRL",
                    FechaEmision = "2026-11-02"
                },
                Currency = new CanonicalCurrencyDto { Code = "DOP", ExchangeRate = 1m },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 10807.02m,
                    MontoExento = 10807.02m,
                    MontoGravadoTotal = 0m,
                    MontoItbis = 0m,
                    MontoTotal = 10807.02m
                },
                References = new CanonicalReferencesDto
                {
                    CorrectsENcf = "E430000000014",
                    CodigoModificacion = 1,
                    RazonModificacion = "Anulación de comprobante por corrección de moneda",
                    FechaNcfModificado = "2026-10-30"
                },
                Lines =
                [
                    new()
                    {
                        LineNumber = 1,
                        ItemName = "6215 · Servicios de Informatica",
                        Quantity = 1m,
                        UnitPrice = 10807.02m,
                        Amount = 10807.02m,
                        IndicadorFacturacion = "4"
                    }
                ]
            };

            var res34 = await controller34.SubmitCanonicalDocument(dto34);
            Assert.IsType<AcceptedResult>(res34);
            var doc34 = await db34.EcfDocuments.SingleAsync();
            var xml34 = doc34.SignedXmlContent!;

            Assert.Contains("<TipoeCF>34</TipoeCF>", xml34);
            Assert.DoesNotContain("<Comprador>", xml34);
            Assert.DoesNotContain("<TipoIngresos>", xml34);
            Assert.DoesNotContain("<IndicadorMontoGravado>", xml34);
            Assert.DoesNotContain("<TotalITBIS>", xml34);
            Assert.Contains("<MontoExento>10807.02</MontoExento>", xml34);
            Assert.Contains("<MontoTotal>10807.02</MontoTotal>", xml34);
            Assert.Contains("<IndicadorFacturacion>4</IndicadorFacturacion>", xml34);
            Assert.Contains("<NCFModificado>E430000000014</NCFModificado>", xml34);
            Assert.Contains("<CodigoModificacion>1</CodigoModificacion>", xml34);

            var val34 = new EcfSchemaValidator().Validate(xml34, XsdPath("e-CF 34 v.1.0.xsd"));
            Assert.True(val34.IsValid, string.Join("\n", val34.Errors));

            var (controller43, db43, _, _) = MakeRealController("E430000000015");
            var dto43 = new CanonicalDocumentDto
            {
                DocumentKind = "MinorExpense",
                TipoComprobante = "E43",
                SourceReference = new SourceReferenceDto { TxnId = "BILL-B1300002709-REISSUE", EditSequence = "1" },
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101000001",
                    RazonSocialEmisor = "EMPRESA DEMO SRL",
                    FechaEmision = "2026-11-02"
                },
                Currency = new CanonicalCurrencyDto { Code = "USD", ExchangeRate = 60.16m },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 10807.02m,
                    MontoExento = 10807.02m,
                    MontoItbis = 0m,
                    MontoTotal = 10807.02m
                },
                Lines =
                [
                    new()
                    {
                        LineNumber = 1,
                        ItemName = "6215 · Servicios de Informatica",
                        Quantity = 1m,
                        UnitPrice = 10807.02m,
                        Amount = 10807.02m,
                        IndicadorFacturacion = "4"
                    }
                ]
            };

            var res43 = await controller43.SubmitCanonicalDocument(dto43);
            Assert.IsType<AcceptedResult>(res43);
            var doc43 = await db43.EcfDocuments.SingleAsync();
            var xml43 = doc43.SignedXmlContent!;

            Assert.Equal(650150.32m, doc43.TotalAmount);
            Assert.DoesNotContain("<Comprador>", xml43);
            Assert.Contains("<MontoExento>650150.32</MontoExento>", xml43);
            Assert.Contains("<MontoTotal>650150.32</MontoTotal>", xml43);
            Assert.Contains("<TipoMoneda>USD</TipoMoneda>", xml43);
            Assert.Contains("<TipoCambio>60.16</TipoCambio>", xml43);
            Assert.Contains("<MontoExentoOtraMoneda>10807.02</MontoExentoOtraMoneda>", xml43);
            Assert.Contains("<MontoTotalOtraMoneda>10807.02</MontoTotalOtraMoneda>", xml43);

            var val43 = new EcfSchemaValidator().Validate(xml43, XsdPath("e-CF 43 v.1.0.xsd"));
            Assert.True(val43.IsValid, string.Join("\n", val43.Errors));
        }

        [Fact]
        public async Task Tipo34_ModifyingE31_WithDuplicateIdentificadorExtranjero_EmitsRncCompradorAndPassesOfficialXsd()
        {
            var (controller34, db34, _, _) = MakeRealController("E340000000825");
            var dto34 = new CanonicalDocumentDto
            {
                DocumentKind = "CreditNote",
                TipoComprobante = "E34",
                SourceReference = new SourceReferenceDto { TxnId = "TXN-34-1381-XSD", EditSequence = "1" },
                Header = new CanonicalHeaderDto
                {
                    RncComprador = "00100000018",
                    IdentificadorExtranjero = "00100000018",
                    RazonSocialComprador = "COMERCIAL DEMO",
                    FechaEmision = "2026-11-17"
                },
                Totals = new CanonicalTotalsDto
                {
                    MontoSubtotal = 1566400.00m,
                    MontoGravadoTotal = 1566400.00m,
                    MontoItbis = 281952.00m,
                    MontoTotal = 1848352.00m
                },
                Lines =
                [
                    new()
                    {
                        LineNumber = 1,
                        ItemName = "Descuento por Ajuste de precios",
                        Quantity = 1m,
                        UnitPrice = 1566400.00m,
                        Amount = 1566400.00m,
                        IndicadorFacturacion = "1"
                    }
                ],
                References = new CanonicalReferencesDto
                {
                    CorrectsENcf = "E310000000139",
                    CodigoModificacion = 3,
                    FechaNcfModificado = "2026-10-28",
                    RazonModificacion = "Ajuste de precios"
                }
            };

            var res34 = await controller34.SubmitCanonicalDocument(dto34);
            Assert.IsType<AcceptedResult>(res34);
            var doc34 = await db34.EcfDocuments.SingleAsync();
            var xml34 = doc34.SignedXmlContent!;

            Assert.Contains("<RNCComprador>00100000018</RNCComprador>", xml34);
            Assert.DoesNotContain("<IdentificadorExtranjero>", xml34);

            var val34 = new EcfSchemaValidator().Validate(xml34, XsdPath("e-CF 34 v.1.0.xsd"));
            Assert.True(val34.IsValid, string.Join("\n", val34.Errors));
        }
    }
}
