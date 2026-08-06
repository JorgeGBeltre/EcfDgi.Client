using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Api.Controllers;
using EcfDgii.Client.Application.Documents.Dto;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Configuration;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Infrastructure.Serialization;
using EcfDgii.Client.Shared.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace EcfDgii.Client.UnitTests.Documents
{
    /// <summary>
    /// EF Core's InMemory provider never actually throws on a unique-index violation (see the note
    /// at the bottom of this file), so a real concurrent-insert race can't be reproduced by hitting
    /// the store. This test double throws a crafted DbUpdateException — wrapping a real Npgsql
    /// PostgresException with the exact SqlState/ConstraintName a live Postgres would report — the
    /// first time it sees a tracked EcfDocument insert, and plants a "winner" row at that same
    /// moment to stand in for the concurrent request that (in a real race) committed first.
    /// </summary>
    public class ThrowOnceOnInsertDbContext : ApplicationDbContext
    {
        private readonly string _dbName;
        private readonly DbUpdateException? _exceptionToThrow;
        private readonly EcfDocument? _winnerToPlant;
        private bool _thrown;

        public ThrowOnceOnInsertDbContext(
            DbContextOptions<ApplicationDbContext> options,
            string dbName,
            DbUpdateException? exceptionToThrow,
            EcfDocument? winnerToPlant)
            : base(options)
        {
            _dbName = dbName;
            _exceptionToThrow = exceptionToThrow;
            _winnerToPlant = winnerToPlant;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var isDocumentInsert = ChangeTracker.Entries<EcfDocument>()
                .Any(e => e.State == EntityState.Added);

            if (!_thrown && isDocumentInsert && _exceptionToThrow != null)
            {
                _thrown = true;

                if (_winnerToPlant != null)
                {
                    var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                        .UseInMemoryDatabase(_dbName)
                        .Options;
                    using var winnerContext = new ApplicationDbContext(options);
                    winnerContext.EcfDocuments.Add(_winnerToPlant);
                    await winnerContext.SaveChangesAsync(cancellationToken);
                }

                throw _exceptionToThrow;
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    public class DocumentsControllerTests
    {
        // A minimal well-formed "signed" XML containing the ds:SignatureValue node that
        // EcfSecurityUtils.CalcularCodigoSeguridad requires to compute the security code.
        private const string FakeSignedXml =
            "<ECF xmlns:ds='http://www.w3.org/2000/09/xmldsig#'>" +
            "<ds:Signature><ds:SignatureValue>ZmFrZS1zaWduYXR1cmU=</ds:SignatureValue></ds:Signature>" +
            "</ECF>";

        private static ApplicationDbContext NewDb()
        {
            return NewDbWithName(Guid.NewGuid().ToString());
        }

        private static ApplicationDbContext NewDbWithName(string name)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(name)
                .Options;
            return new ApplicationDbContext(options);
        }

        private static CanonicalDocumentDto MakeDto(string txnId, string editSequence)
        {
            return new CanonicalDocumentDto
            {
                SourceReference = new SourceReferenceDto { TxnId = txnId, EditSequence = editSequence },
                TipoComprobante = "E31",
                Header = new CanonicalHeaderDto { RncEmisor = "101889063", RazonSocialEmisor = "Willy Chic" },
                Totals = new CanonicalTotalsDto { MontoSubtotal = 100, MontoItbis = 18, MontoTotal = 118 }
            };
        }

        private static DocumentsController MakeController(
            ApplicationDbContext db,
            Mock<IEcfSequenceManager> sequenceManagerMock,
            Mock<IEcfClient> ecfClientMock,
            Mock<IEcfXmlSigner> signerMock,
            Mock<IClock>? clockMock = null)
        {
            if (clockMock == null)
            {
                // Default: always "well after" any timestamp SaveChangesAsync stamps with the real
                // system clock, so existing-document age gates never block a test unless it
                // explicitly opts in with its own clockMock.
                clockMock = new Mock<IClock>();
                clockMock.Setup(c => c.UtcNow).Returns(() => DateTimeOffset.UtcNow.AddDays(1));
            }

            var emisorOptions = Options.Create(new EcfEmisorOptions { Rnc = "101889063", RazonSocial = "Willy Chic Dominicana SRL" });
            // XSD gate opt-in, off by default here — these tests predate it and exercise other
            // concerns; EcfDocumentXsdValidationTests.cs exercises the gate itself directly.
            var ecfClientOptions = Options.Create(new EcfClientOptions { ValidateSchemasLocal = false });

            var controller = new DocumentsController(
                db, sequenceManagerMock.Object, ecfClientMock.Object, signerMock.Object,
                NullLogger<DocumentsController>.Instance, clockMock.Object, emisorOptions,
                new EcfSchemaValidator(), ecfClientOptions)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            return controller;
        }

        private static DbUpdateException MakeUniqueViolation(string sqlState, string? constraintName)
        {
            var pgEx = new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", sqlState, constraintName: constraintName);
            return new DbUpdateException("conflict", pgEx);
        }

        [Fact]
        public async Task NewDocument_UsesConfiguredEmisorRnc_RegardlessOfWhatDtoSends()
        {
            // The RNC this API instance signs and transmits under is an instance-level fact — a
            // wrong or missing value on the incoming DTO must never silently produce a validly
            // signed e-CF under someone else's RNC.
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000601");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-601" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            var dto = MakeDto("TXN-WRONGRNC", "1");
            dto.Header.RncEmisor = "999999999"; // deliberately not the configured instance RNC

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(dto);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("101889063", stored.RncEmisor); // configured RNC won, not the DTO's
            signerMock.Verify(s => s.SignXml(It.IsAny<string>(), "101889063"), Times.Once);
        }

        [Fact]
        public async Task NewDocument_UsesConfiguredEmisorRazonSocial_RegardlessOfWhatDtoSends()
        {
            // Same reasoning as the RNC test above, and the same gap: the RNC override was built
            // three rounds ago, but RazonSocialEmisor was never given the same treatment — a caller
            // (or a bug on the ERPConnector side) could put an arbitrary razón social under this
            // instance's own enforced RNC in the signed XML.
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000602");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-602" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                // Pass the pre-signature XML through (so we can inspect the RazonSocialEmisor it
                // carries) while still injecting a real ds:SignatureValue node — CalcularCodigoSeguridad
                // requires one to exist, same as FakeSignedXml above does for the other tests.
                .Returns((string xml, string rnc) => xml.Replace(
                    "</ECF>",
                    "<ds:Signature xmlns:ds='http://www.w3.org/2000/09/xmldsig#'>" +
                    "<ds:SignatureValue>ZmFrZS1zaWduYXR1cmU=</ds:SignatureValue></ds:Signature></ECF>"));

            var dto = MakeDto("TXN-WRONGRAZON", "1");
            dto.Header.RazonSocialEmisor = "Empresa Impostora SRL"; // deliberately not the configured instance value

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(dto);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<RazonSocialEmisor>Willy Chic Dominicana SRL</RazonSocialEmisor>", stored.SignedXmlContent);
            Assert.DoesNotContain("Empresa Impostora", stored.SignedXmlContent);
        }

        // --- Tipo 34 (Nota de Crédito) ---

        [Fact]
        public async Task NewDocument_Tipo34_MissingCorrectsENcf_ReturnsBadRequest_BeforeAllocatingASequence()
        {
            // DGII: NCFModificado is obligatorio (1) for tipo 34 — per
            // "Formato Comprobante Fiscal Electrónico (e-CF) V1.0 (1).md" lines 1105-1136.
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var dto = MakeDto("TXN-NC-1", "1");
            dto.TipoComprobante = "E34";
            dto.References = new CanonicalReferencesDto(); // CorrectsENcf/CodigoModificacion left unset

            var controller = MakeController(db, sequenceManagerMock, new Mock<IEcfClient>(), new Mock<IEcfXmlSigner>());
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<BadRequestObjectResult>(result);
            sequenceManagerMock.Verify(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task NewDocument_Tipo34_MissingCodigoModificacion_ReturnsBadRequest()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var dto = MakeDto("TXN-NC-2", "1");
            dto.TipoComprobante = "E34";
            dto.References = new CanonicalReferencesDto { CorrectsENcf = "E310000000001" };

            var controller = MakeController(db, sequenceManagerMock, new Mock<IEcfClient>(), new Mock<IEcfXmlSigner>());
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task NewDocument_Tipo34_WithValidReferences_EmitsInformacionReferenciaBlock()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), "E34", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E340000000001");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-NC" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>())).Returns(FakeSignedXml);

            var dto = MakeDto("TXN-NC-3", "1");
            dto.TipoComprobante = "E34";
            dto.References = new CanonicalReferencesDto
            {
                CorrectsENcf = "E310000000001",
                CodigoModificacion = 3, // Corrige montos
                RazonModificacion = "error en precio",
            };

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<AcceptedResult>(result);
            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<TipoeCF>34</TipoeCF>", stored.XmlContent);
            Assert.Contains("<NCFModificado>E310000000001</NCFModificado>", stored.XmlContent);
            Assert.Contains("<CodigoModificacion>3</CodigoModificacion>", stored.XmlContent);
            Assert.Contains("<RazonModificacion>error en precio</RazonModificacion>", stored.XmlContent);
        }

        // --- Tipo 41 (Comprobante de Compras) ---

        [Fact]
        public async Task NewDocument_Tipo41_MissingRncComprador_ReturnsBadRequest()
        {
            // DGII: RNCComprador is obligatorio (1) for tipo 41 (the informal vendor's RNC/Cédula) —
            // unlike 31/32/33/34 where it's condicional.
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var dto = MakeDto("TXN-COMPRA-1", "1");
            dto.TipoComprobante = "E41";
            dto.Header.RncComprador = "";
            dto.Retention = new CanonicalRetentionDto { MontoItbisRetenido = 18m };

            var controller = MakeController(db, sequenceManagerMock, new Mock<IEcfClient>(), new Mock<IEcfXmlSigner>());
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task NewDocument_Tipo41_MissingRetention_ReturnsBadRequest()
        {
            // DGII: Retención area is obligatorio (1) only for tipo 41 (and 47) — the buyer withholds
            // ITBIS/ISR on behalf of the informal seller completing the e-CF on its behalf.
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var dto = MakeDto("TXN-COMPRA-2", "1");
            dto.TipoComprobante = "E41";
            dto.Retention = null;

            var controller = MakeController(db, sequenceManagerMock, new Mock<IEcfClient>(), new Mock<IEcfXmlSigner>());
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task NewDocument_Tipo41_EmisorStaysConfiguredIdentity_ComprarorIsTheVendor_RetencionEmitted_NoTipoIngresos()
        {
            // Per the spec, <Emisor><RNCEmisor> must be a DGII-authorized Facturador Electrónico — an
            // informal vendor can never satisfy that, so Emisor stays Willy Chic's own configured
            // identity; the vendor's identity goes in <Comprador>, not a field-swap. TipoIngresos is
            // "No corresponde" (0) for tipo 41 — must be omitted, not emitted as "1" like every other type.
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), "E41", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E410000000001");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-COMPRA" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>())).Returns(FakeSignedXml);

            var dto = MakeDto("TXN-COMPRA-3", "1");
            dto.TipoComprobante = "E41";
            dto.Header.RncComprador = "130000005"; // the informal vendor
            dto.Header.RazonSocialComprador = "Proveedor Informal SRL";
            dto.Retention = new CanonicalRetentionDto
            {
                IndicadorAgenteRetencionoPercepcion = 1,
                MontoItbisRetenido = 18m,
                MontoIsrRetenido = null,
            };

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller.SubmitCanonicalDocument(dto);

            Assert.IsType<AcceptedResult>(result);
            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("101889063", stored.RncEmisor); // Emisor stayed Willy Chic, not the vendor
            Assert.Contains("<RNCEmisor>101889063</RNCEmisor>", stored.XmlContent);
            Assert.Contains("<RNCComprador>130000005</RNCComprador>", stored.XmlContent); // vendor as Comprador
            Assert.DoesNotContain("<TipoIngresos>", stored.XmlContent);
            Assert.Contains("<IndicadorAgenteRetencionoPercepcion>1</IndicadorAgenteRetencionoPercepcion>", stored.XmlContent);
            Assert.Contains("<MontoITBISRetenido>18.00</MontoITBISRetenido>", stored.XmlContent);
            Assert.DoesNotContain("<MontoISRRetenido>", stored.XmlContent); // null, conditional field omitted
        }

        [Fact]
        public async Task NewDocument_Tipo31_StillEmitsTipoIngresos_AndNoRetencionBlock()
        {
            // Regression guard: the tipo-41 branching above must not change behavior for a normal
            // factura (tipo 31), the only path exercised by every other test in this file.
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000701");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-701" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>())).Returns(FakeSignedXml);

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-701", "1"));

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Contains("<TipoIngresos>01</TipoIngresos>", stored.XmlContent); // zero-padded per DGII's TipoIngresosValidationType
            Assert.DoesNotContain("<Retencion>", stored.XmlContent);
            Assert.DoesNotContain("<InformacionReferencia>", stored.XmlContent);
        }

        [Fact]
        public async Task Retry_AfterSigningFailure_ReusesAllocatedEncf_InsteadOfStayingStuck()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000001");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();

            // First attempt: signing fails.
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Throws(new InvalidOperationException("certificate not found"));

            var dto = MakeDto("TXN-1", "10");
            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var firstResult = await controller.SubmitCanonicalDocument(dto);
            Assert.IsType<BadRequestObjectResult>(firstResult);

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("SigningFailed", stored.State);
            Assert.Equal("E310000000001", stored.ENcf);

            // Second attempt (retry): signing now succeeds.
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-1" });

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var secondResult = await controller2.SubmitCanonicalDocument(MakeDto("TXN-1", "10"));

            var accepted = Assert.IsType<AcceptedResult>(secondResult);
            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("SentToDgii", afterRetry.State);
            Assert.Equal("E310000000001", afterRetry.ENcf);

            // The eNCF must never be allocated twice for the same TxnId.
            sequenceManagerMock.Verify(
                s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task Uncertain_Retry_QueriesDgiiFirst_AndResendsOnlyWhenDgiiNeverReceivedIt()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000002");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            // First attempt: transport to DGII throws -> Uncertain. We genuinely don't know if it landed.
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("timeout"));

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-2", "1"));

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Uncertain", stored.State);
            Assert.Equal("E310000000002", stored.ENcf);
            // ReconcileUncertainAsync's "prefer TrackId when we have one" branch is unreachable today
            // only because SignAndSendAsync never sets TrackId before an Uncertain transition. This
            // pins that assumption down: if someone later makes SendEcfAsync's exception path capture
            // a partial TrackId, this test forces a decision instead of leaving the preference silently
            // unimplemented for a case that's now actually reachable.
            Assert.Null(stored.TrackId);

            // Retry: must reconcile with DGII before blindly resending. DGII confirms it never got it.
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync("101889063", "E310000000002", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Codigo = 0, Estado = "No encontrado" });
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-2" });

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller2.SubmitCanonicalDocument(MakeDto("TXN-2", "1"));

            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("SentToDgii", afterRetry.State);
            Assert.Equal("TRACK-2", afterRetry.TrackId);
            Assert.Equal("E310000000002", afterRetry.ENcf);

            ecfClientMock.Verify(
                c => c.ConsultarEstadoAsync("101889063", "E310000000002", null, null, It.IsAny<CancellationToken>()),
                Times.Once);
            sequenceManagerMock.Verify(
                s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task Uncertain_Retry_TooSoonAfterFailure_SkipsReconciliation_StaysUncertain()
        {
            // DGII's status query can lag behind actual receipt. Trusting a "No encontrado" that
            // arrives moments after the transmission failure risks resending something DGII already
            // has. Below the minimum age, reconciliation must not even be attempted.
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000010");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("timeout"));

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-SOON", "1"));

            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Uncertain", stored.State);

            // Retry just 5 seconds later — well inside the reconciliation window.
            var tooSoonClockMock = new Mock<IClock>();
            tooSoonClockMock.Setup(c => c.UtcNow)
                .Returns(() => new DateTimeOffset(stored.UpdatedAt!.Value, TimeSpan.Zero).AddSeconds(5));

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock, tooSoonClockMock);
            var result = await controller2.SubmitCanonicalDocument(MakeDto("TXN-SOON", "1"));

            Assert.IsType<AcceptedResult>(result);
            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Uncertain", afterRetry.State);

            ecfClientMock.Verify(
                c => c.ConsultarEstadoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
            // SendEcfAsync was only ever called once — the original failed attempt. No resend.
            ecfClientMock.Verify(
                c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task Uncertain_Retry_ReconciliationQueryThrows_StaysUncertain_DoesNotResend()
        {
            // If we still can't tell whether DGII has it, the only safe outcome is "stay Uncertain".
            // Falling through to a resend here is exactly the double-transmission risk this whole
            // reconciliation path exists to prevent.
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000011");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("timeout"));

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-DGIIDOWN", "1"));
            Assert.Equal("Uncertain", (await db.EcfDocuments.SingleAsync()).State);

            // Retry (default clock mock is far in the future, so the age gate passes), but DGII
            // itself is unreachable this time.
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("DGII unreachable"));

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller2.SubmitCanonicalDocument(MakeDto("TXN-DGIIDOWN", "1"));

            Assert.IsType<AcceptedResult>(result);
            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("Uncertain", afterRetry.State);
            ecfClientMock.Verify(
                c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task Uncertain_Retry_DoesNotResend_WhenDgiiAlreadyHasIt()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000009");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("timeout"));

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-9", "1"));
            Assert.Equal("Uncertain", (await db.EcfDocuments.SingleAsync()).State);

            // Retry: DGII confirms it DID receive the first transmission. Must not resend a duplicate.
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync("101889063", "E310000000009", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Codigo = 1, Estado = "Aceptado" });

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller2.SubmitCanonicalDocument(MakeDto("TXN-9", "1"));

            Assert.IsType<AcceptedResult>(result);
            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("SentToDgii", afterRetry.State);
            Assert.Equal("E310000000009", afterRetry.ENcf);

            // SendEcfAsync was invoked exactly once — during the first (failed) attempt.
            // The retry must not transmit a second, duplicate e-CF once DGII confirms it has one.
            ecfClientMock.Verify(
                c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task SequenceAllocated_Retry_ReusesEncf_InsteadOfSilentlyReturningUnsentDocument()
        {
            // Simulates a crash right after the eNCF was allocated and persisted, but before
            // signing ever started (the process never reached SignAndSendAsync).
            var db = NewDb();
            var crashedDoc = new EcfDocument
            {
                TenantId = "default-tenant",
                SourceTxnId = "TXN-5",
                EditSequence = "1",
                ENcf = "E310000000005",
                RncEmisor = "101889063",
                XmlContent = "<ECF><IdDoc><eNCF>E310000000005</eNCF></IdDoc></ECF>",
                State = "SequenceAllocated"
            };
            db.EcfDocuments.Add(crashedDoc);
            await db.SaveChangesAsync();

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-5" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller.SubmitCanonicalDocument(MakeDto("TXN-5", "1"));

            // Must actually sign and send — not silently report the never-transmitted document as done.
            Assert.IsType<AcceptedResult>(result);
            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("SentToDgii", afterRetry.State);
            Assert.Equal("TRACK-5", afterRetry.TrackId);
            Assert.Equal("E310000000005", afterRetry.ENcf);

            signerMock.Verify(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            ecfClientMock.Verify(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            sequenceManagerMock.Verify(
                s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task SigningFailed_EditSequenceChanged_RetriesWithNewContent_ReusesEncf_NotConflict()
        {
            // Nothing ever left the process on the first attempt, so an edited invoice is safe
            // to retry under the same eNCF — unlike a document that was already transmitted.
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000006");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Throws(new InvalidOperationException("certificate not found"));

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-6", "1"));
            Assert.Equal("SigningFailed", (await db.EcfDocuments.SingleAsync()).State);

            // Retry with an edited invoice (different totals + EditSequence).
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-6" });

            var editedDto = MakeDto("TXN-6", "2");
            editedDto.Totals = new CanonicalTotalsDto { MontoSubtotal = 200, MontoItbis = 36, MontoTotal = 236 };

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller2.SubmitCanonicalDocument(editedDto);

            Assert.IsType<AcceptedResult>(result);
            var afterRetry = await db.EcfDocuments.SingleAsync();
            Assert.Equal("SentToDgii", afterRetry.State);
            Assert.Equal("E310000000006", afterRetry.ENcf);
            Assert.Equal("2", afterRetry.EditSequence);
            Assert.Equal(236, afterRetry.TotalAmount);

            sequenceManagerMock.Verify(
                s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task SameTxnIdDifferentEditSequence_ReturnsConflict_InsteadOfStaleCachedResult()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000003");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-3" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var first = await controller.SubmitCanonicalDocument(MakeDto("TXN-3", "1"));
            Assert.IsType<AcceptedResult>(first);

            // The QuickBooks invoice was edited after the e-CF was already sent (EditSequence changed).
            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var second = await controller2.SubmitCanonicalDocument(MakeDto("TXN-3", "2"));

            Assert.IsType<ConflictObjectResult>(second);

            // Must not silently reuse the stale document as if nothing changed.
            var stored = await db.EcfDocuments.SingleAsync();
            Assert.Equal("SentToDgii", stored.State);
        }

        [Fact]
        public async Task SameTxnIdSameEditSequence_ReturnsCachedResult_WithoutReprocessing()
        {
            var db = NewDb();
            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync("default-tenant", "E31", It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000004");
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRACK-4" });
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string xml, string rnc) => FakeSignedXml);

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            await controller.SubmitCanonicalDocument(MakeDto("TXN-4", "5"));

            var controller2 = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var second = await controller2.SubmitCanonicalDocument(MakeDto("TXN-4", "5"));

            Assert.IsType<AcceptedResult>(second);
            sequenceManagerMock.Verify(
                s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
            ecfClientMock.Verify(
                c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // NOTE: a test proving uq_ecf_documents_tenant_source_txn actually rejects a concurrent
        // duplicate insert is intentionally not included here. EF Core's InMemory provider does not
        // enforce unique indexes at all (confirmed empirically: even two Adds in a single
        // SaveChangesAsync call on one context do not throw) — Microsoft documents InMemory as not
        // providing relational-integrity guarantees. Verifying this constraint for real requires a
        // relational provider (SQLite in-memory or a real Postgres via Testcontainers), which this
        // test project doesn't have set up.
        //
        // The tests below instead craft the exact DbUpdateException(PostgresException) shape a real
        // Postgres would throw and drive DocumentsController's recovery logic against it directly —
        // see ThrowOnceOnInsertDbContext above.

        [Fact]
        public async Task ConcurrentInsert_TenantTxnUniqueViolation_RecoversWithWinnersDocument()
        {
            var dbName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options;

            var winner = new EcfDocument
            {
                TenantId = "default-tenant",
                SourceTxnId = "TXN-RACE",
                EditSequence = "1",
                ENcf = "E310000000201",
                RncEmisor = "101889063",
                TrackId = "TRACK-WINNER",
                XmlContent = "<ECF/>",
                State = "SentToDgii"
            };

            var db = new ThrowOnceOnInsertDbContext(
                options, dbName,
                MakeUniqueViolation("23505", "uq_ecf_documents_tenant_source_txn"),
                winner);

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000202"); // our own, ultimately-wasted allocation
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);
            var result = await controller.SubmitCanonicalDocument(MakeDto("TXN-RACE", "1"));

            // Must surface the winner's already-committed document, not crash and not create a
            // second document for the same invoice.
            var accepted = Assert.IsType<AcceptedResult>(result);
            var value = Assert.IsAssignableFrom<object>(accepted.Value);
            var eNcfProperty = value.GetType().GetProperty("eNcf");
            Assert.Equal("E310000000201", eNcfProperty!.GetValue(value));

            Assert.Equal(1, await db.EcfDocuments.CountAsync());
            // Signing/sending must never be attempted for our losing attempt.
            signerMock.Verify(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            ecfClientMock.Verify(c => c.SendEcfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ConcurrentInsert_UnrelatedUniqueViolation_PropagatesInsteadOfBeingTreatedAsTxnRace()
        {
            // A different unique constraint (e.g. an eNCF collision) is a real bug, not a benign
            // TxnId race. Swallowing it as "just refetch and continue" would hide the actual error
            // and, worse, could re-run SignAndSendAsync against a document that has nothing to do
            // with this failure.
            var dbName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options;
            var db = new ThrowOnceOnInsertDbContext(
                options, dbName,
                MakeUniqueViolation("23505", "uq_ecf_documents_rnc_emisor_encf"),
                winnerToPlant: null);

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000301");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);

            await Assert.ThrowsAsync<DbUpdateException>(
                () => controller.SubmitCanonicalDocument(MakeDto("TXN-OTHER", "1")));
        }

        [Fact]
        public async Task ConcurrentInsert_NonUniqueViolation_Propagates()
        {
            // A generic write failure (FK violation, timeout, etc.) must never be reinterpreted as
            // "someone else already has this TxnId".
            var dbName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options;
            var db = new ThrowOnceOnInsertDbContext(
                options, dbName,
                MakeUniqueViolation("23503", null), // foreign_key_violation, not unique_violation
                winnerToPlant: null);

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000401");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);

            await Assert.ThrowsAsync<DbUpdateException>(
                () => controller.SubmitCanonicalDocument(MakeDto("TXN-FK", "1")));
        }

        [Fact]
        public async Task ConcurrentInsert_MatchingViolationButNoWinnerFound_ThrowsClearError_NotNullReference()
        {
            // Defends the "impossible" branch: the constraint fired, but re-querying for the
            // supposed winner finds nothing. Must fail loudly and specifically, not with a raw NRE
            // three layers down from a silently-lost error.
            var dbName = Guid.NewGuid().ToString();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(dbName).Options;
            var db = new ThrowOnceOnInsertDbContext(
                options, dbName,
                MakeUniqueViolation("23505", "uq_ecf_documents_tenant_source_txn"),
                winnerToPlant: null); // nothing actually committed — the "impossible" case

            var sequenceManagerMock = new Mock<IEcfSequenceManager>();
            sequenceManagerMock.Setup(s => s.GetNextEncfAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("E310000000501");
            var ecfClientMock = new Mock<IEcfClient>();
            var signerMock = new Mock<IEcfXmlSigner>();

            var controller = MakeController(db, sequenceManagerMock, ecfClientMock, signerMock);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.SubmitCanonicalDocument(MakeDto("TXN-GHOST", "1")));
            Assert.Contains("TXN-GHOST", ex.Message);
        }
    }
}
