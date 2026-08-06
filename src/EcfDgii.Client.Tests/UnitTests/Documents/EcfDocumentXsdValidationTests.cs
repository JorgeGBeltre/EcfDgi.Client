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
    /// This file now ALSO exercises the real pre-signature validation gate DocumentsController runs
    /// when EcfClientOptions:ValidateSchemasLocal/XsdDirectoryPath are configured (previously that
    /// capability existed only in EcfClient.SendEcfAsync — post-signature, and silently never
    /// activated because nothing ever set XsdDirectoryPath). The Tipo31/34/41 tests above additionally
    /// still directly call EcfSchemaValidator against the stored XML as a second, independent check.
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

        private static string XsdDir() =>
            Path.Combine(FindRepoRoot(), "Documentación Técnica (XSD)");

        private static string XsdPath(string fileName) =>
            Path.Combine(XsdDir(), fileName);

        private static ApplicationDbContext NewDb() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        private static (DocumentsController Controller, ApplicationDbContext Db, Mock<IEcfXmlSignerSpy> SignerSpy, Mock<IEcfClient> EcfClientMock) MakeRealController(
            string nextEncf, bool enableXsdGate = true)
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
            var realSigner = new EcfXmlSigner(cert); // real signer — ValidateCertificateSn's self-signed-cert bypass covers this

            // Spies on the real signer so a test can assert the pre-signature XSD gate short-circuits
            // BEFORE ever calling it — a structurally-broken document must never reach signing/DGII.
            var signerSpyMock = new Mock<IEcfXmlSignerSpy> { CallBase = false };
            signerSpyMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => realSigner.SignXml(xml, rnc));
            signerSpyMock.Setup(s => s.ValidateCertificateSn(It.IsAny<string>())).Returns(true);

            var clockMock = new Mock<IClock>();
            clockMock.Setup(c => c.UtcNow).Returns(() => DateTimeOffset.UtcNow.AddDays(1));
            var emisorOptions = Options.Create(new EcfEmisorOptions { Rnc = "101889063", RazonSocial = "WILLY CHIC DOMINICANA SRL" });

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

        /// <summary>Lets Moq spy on IEcfXmlSigner calls while delegating to a real signer underneath.</summary>
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
        public async Task Tipo31_WithIsoFechaEmision_AsErpConnectorActuallySendsIt_IsNormalizedAndValidAgainstDgiiXsd()
        {
            // Every other test in this file leaves Header.FechaEmision null, which falls through to
            // BuildXmlFromCanonical's own dd-MM-yyyy default — so the whole suite was green while the
            // ONE format a real caller actually sends was never exercised. ERPConnector's
            // ApiSubmitStage builds this field as IssueDate.ToString("yyyy-MM-dd") (ISO 8601), which
            // does not match DGII's FechaValidationType pattern (dd-MM-yyyy) at all.
            //
            // ISO is the right thing for a jurisdiction-neutral connector to send; converting it into
            // DGII's format is this service's job, not the connector's.
            var (controller, db, _, _) = MakeRealController("E310000000802");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-31-ISO", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101889063", RazonSocialEmisor = "Willy Chic",
                    RncComprador = "130000000", RazonSocialComprador = "Cliente de Prueba",
                    FechaEmision = "2026-08-06",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
            };

            var result = await controller.SubmitCanonicalDocument(dto);
            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);

            var stored = await db.EcfDocuments.SingleAsync();

            // Not just "schema-valid": the same calendar day, day-first. A month/day swap (08-06-2026)
            // would also satisfy the XSD pattern while filing the document under the wrong date.
            Assert.Contains("<FechaEmision>06-08-2026</FechaEmision>", stored.SignedXmlContent!);

            var validation = new EcfSchemaValidator().Validate(stored.SignedXmlContent!, XsdPath("e-CF 31 v.1.0.xsd"));
            Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        }

        [Fact]
        public async Task Tipo31_WithFechaEmisionAlreadyInDgiiFormat_PassesThroughUnchanged()
        {
            // Regression guard, not a driver — this already passed before NormalizeFechaDgii existed.
            // It exists to pin the one "improvement" that would silently corrupt data: relaxing the
            // normalizer to a lenient DateTime.TryParse. Under InvariantCulture that reads 06-08-2026
            // month-first as 8 June and re-emits 08-06-2026 — still schema-valid, wrong calendar day.
            var (controller, db, _, _) = MakeRealController("E310000000803");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-31-DDMM", EditSequence = "1" },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101889063", RazonSocialEmisor = "Willy Chic",
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
            // FechaNCFModificado is the same FechaValidationType as FechaEmision (e-CF 34 XSD line
            // 414), reached by the same "caller-supplied string written into the XML verbatim" path —
            // the identical defect, one field over. No live caller populates it today (ApiSubmitStage
            // sends only CorrectsENcf/CodigoModificacion/RazonModificacion), so this was latent rather
            // than broken in production, and it stays latent only until credit notes are wired up.
            var (controller, db, _, _) = MakeRealController("E340000000802");
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-34-ISO", EditSequence = "1" },
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
        public async Task Tipo34_GeneratedXml_IsValidAgainstDgiiXsd()
        {
            var (controller, db, _, _) = MakeRealController("E340000000801");
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
            var (controller, db, _, _) = MakeRealController("E410000000801");
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

        [Fact]
        public async Task StructurallyInvalidDocument_IsRejected_BeforeReachingDgii_WhenGateIsEnabled()
        {
            // Validates the SIGNED xml (see DocumentsController.SignAndSendAsync's own comment on
            // why: the real XSDs require a signature slot, so a pre-signature check would always
            // fail regardless of content) — signing still happens (it's a cheap, local operation),
            // but the real DGII round-trip must never happen for a document that can't pass schema.
            var (controller, db, signerSpy, ecfClientMock) = MakeRealController("E410000000900", enableXsdGate: true);

            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-INVALID", EditSequence = "1" },
                TipoComprobante = "E41",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101889063", RazonSocialEmisor = "Willy Chic",
                    RncComprador = "130000005", RazonSocialComprador = "Proveedor Informal SRL",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
                Retention = new CanonicalRetentionDto
                {
                    // 99 is not a valid IndicadorAgenteRetencionoPercepcionType value (1="R"/2="P") —
                    // a genuinely schema-invalid document that still passes SubmitCanonicalDocument's
                    // own presence-only guard (which only checks Retention is non-null).
                    IndicadorAgenteRetencionoPercepcion = 99,
                    MontoItbisRetenido = 18m,
                },
            };

            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<BadRequestObjectResult>(result);
            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("SchemaInvalid", stored.State);
            signerSpy.Verify(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()), Times.Once); // signing IS cheap/local, still happens
            ecfClientMock.Verify(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never); // DGII round-trip must not happen
        }

        [Fact]
        public async Task StructurallyInvalidDocument_StillSigned_WhenGateIsDisabled()
        {
            // Regression guard: ValidateSchemasLocal/XsdDirectoryPath are opt-in (matches EcfClient's
            // own pre-existing behavior) — a deployment that hasn't configured them must behave
            // exactly as before this round, not suddenly start rejecting documents.
            var (controller, db, signerSpy, _) = MakeRealController("E410000000901", enableXsdGate: false);
            var dto = new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = "TXN-XSD-GATEOFF", EditSequence = "1" },
                TipoComprobante = "E41",
                Header = new CanonicalHeaderDto
                {
                    RncEmisor = "101889063", RazonSocialEmisor = "Willy Chic",
                    RncComprador = "130000005", RazonSocialComprador = "Proveedor Informal SRL",
                },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 },
                Retention = new CanonicalRetentionDto { IndicadorAgenteRetencionoPercepcion = 99, MontoItbisRetenido = 18m },
            };

            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.True(result is AcceptedResult, (result as BadRequestObjectResult)?.Value?.ToString() ?? result.GetType().Name);
            signerSpy.Verify(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }
    }
}
