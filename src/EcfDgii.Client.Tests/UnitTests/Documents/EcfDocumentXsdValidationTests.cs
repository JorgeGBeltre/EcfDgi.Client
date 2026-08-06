using System;
using System.IO;
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
    /// <summary>
    /// Validates the ACTUAL generated e-CF XML — signed with a real EcfXmlSigner (not a mock), so the
    /// ds:Signature element lands exactly where DocumentsController.SignAndSendAsync really puts it
    /// (appended as the last child of the root &lt;ECF&gt; element, per EcfXmlSigner.SignXml) — against
    /// DGII's own published XSD schemas (checked into this repo under "Documentación Técnica (XSD)").
    ///
    /// An xs:sequence-based schema is ORDER-sensitive: if InformacionReferencia or Retencion (added
    /// this round for tipo 34/41; their placement was inferred from the spec's field-level
    /// obligatoriedad tables, not a worked XML example) are in the wrong position, this fails
    /// immediately with a precise line/element error — the only way to convert that inference into a
    /// verified fact without spending a real submission attempt against Certificación.
    ///
    /// Deliberately does NOT wire EcfClient.ValidateSchemasLocal/XsdDirectoryPath end-to-end (that
    /// capability already exists in EcfClient.SendEcfAsync but was never configured — its own
    /// separate finding, see memory) — this test calls EcfSchemaValidator directly against the exact
    /// XML DocumentsController stores, which is the more direct verification for this round's actual
    /// question (is the new structure valid?), not a substitute for turning that config on for real.
    /// </summary>
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

        private static string XsdPath(string fileName) =>
            Path.Combine(FindRepoRoot(), "Documentación Técnica (XSD)", fileName);

        private static ApplicationDbContext NewDb() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        private static (DocumentsController Controller, ApplicationDbContext Db) MakeRealController(string nextEncf)
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(nextEncf);
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-XSD" });

            // Export/reload rather than hand the ephemeral cert straight to EcfXmlSigner: the
            // in-memory cert's private-key handle is tied to the RSA instance's lifetime, which is
            // disposed when this factory method returns — well before the test actually signs a
            // document. Reloading from exported PFX bytes gives the signer its own independent handle.
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=101889063, O=WILLY CHIC DOMINICANA SRL", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var ephemeralCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var certBytes = ephemeralCert.Export(X509ContentType.Pfx, "test-pass");
            var cert = new X509Certificate2(certBytes, "test-pass", X509KeyStorageFlags.Exportable);
            var signer = new EcfXmlSigner(cert); // real signer — ValidateCertificateSn's self-signed-cert bypass covers this

            var clockMock = new Mock<IClock>();
            clockMock.Setup(c => c.UtcNow).Returns(() => DateTimeOffset.UtcNow.AddDays(1));
            var emisorOptions = Options.Create(new EcfEmisorOptions { Rnc = "101889063", RazonSocial = "WILLY CHIC DOMINICANA SRL" });

            var controller = new DocumentsController(
                db, sequenceManagerMock.Object, ecfClientMock.Object, signer,
                NullLogger<DocumentsController>.Instance, clockMock.Object, emisorOptions)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            return (controller, db);
        }

        [Fact]
        public async Task Tipo31_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db) = MakeRealController("E310000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-31", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101889063", RazonSocialEmisor = "Willy Chic",
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
        public async Task Tipo34_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db) = MakeRealController("E340000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-34", EditSequence = "1" },
                TipoComprobante = "E34",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101889063", RazonSocialEmisor = "Willy Chic",
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
            var (controller, db) = MakeRealController("E410000000801");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-41", EditSequence = "1" },
                TipoComprobante = "E41",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101889063", RazonSocialEmisor = "Willy Chic",
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
    }
}
