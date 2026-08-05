using System;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Api.Controllers;
using EcfDgii.Client.Application.Documents.Dto;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace EcfDgii.Client.UnitTests.Documents
{
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
            Mock<IEcfXmlSigner> signerMock)
        {
            var controller = new DocumentsController(db, sequenceManagerMock.Object, ecfClientMock.Object, signerMock.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            return controller;
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
        // test project doesn't have set up. The HandleExistingDocumentAsync recovery path in
        // DocumentsController (catch DbUpdateException on insert → re-fetch → delegate) is reasoned
        // correct and covered indirectly by the existing-document tests above, but the DB-level
        // guarantee it depends on is unverified by automated tests today.
    }
}
