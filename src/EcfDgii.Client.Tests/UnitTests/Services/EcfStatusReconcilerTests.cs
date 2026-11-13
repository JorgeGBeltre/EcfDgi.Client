using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Api.Services;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Shared.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace EcfDgii.Client.UnitTests.Services
{
    public class EcfStatusReconcilerTests
    {
        private static readonly DateTime FixedNow = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

        private static readonly EcfStatusPollingOptions Options = new(
            PollingInterval: TimeSpan.FromMinutes(15),
            MinDocumentAge: TimeSpan.FromMinutes(2),
            MaxPollingWindow: TimeSpan.FromHours(72));

        private static ApplicationDbContext NewDb() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        private static EcfDocument MakeSentDocument(DateTime sentAt, DateTime? lastCheck = null, string state = "Signed") =>
            new()
            {
                RncEmisor = "101000001",
                ENcf = "E310000000001",
                RncComprador = "130000000",
                SecurityCode = "ABC123",
                TenantId = "default-tenant",
                SourceTxnId = "TXN-1",
                EditSequence = "1",
                State = state,
                SentToDgiiAt = sentAt,
                LastStatusCheckAt = lastCheck,
                XmlContent = "<ECF/>",
            };

        private static Mock<IClock> MakeClockMock(DateTime now)
        {
            var mock = new Mock<IClock>();
            mock.Setup(c => c.UtcNow).Returns(now);
            return mock;
        }

        private static EcfStatusReconciler MakeReconciler(
            ApplicationDbContext db, Mock<IEcfClient> ecfClientMock, Mock<IClock>? clockMock = null) =>
            new(db, ecfClientMock.Object, (clockMock ?? MakeClockMock(FixedNow)).Object, Options,
                NullLogger<EcfStatusReconciler>.Instance);

        [Fact]
        public async Task ReconcileAsync_DocumentTooFresh_IsSkipped_NotQueriedYet()
        {
            using var db = NewDb();
            var doc = MakeSentDocument(sentAt: FixedNow.AddMinutes(-1));
            db.EcfDocuments.Add(doc);
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            ecfClientMock.Verify(c => c.ConsultarEstadoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.Equal("Signed",db.EcfDocuments.Single().State);
        }

        [Fact]
        public async Task ReconcileAsync_DgiiSaysAceptado_TransitionsToAcceptedByDgii()
        {
            using var db = NewDb();
            var doc = MakeSentDocument(sentAt: FixedNow.AddHours(-1));
            db.EcfDocuments.Add(doc);
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(
                    "101000001", "E310000000001", "130000000", "ABC123", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Estado = "Aceptado" });

            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            var updated = db.EcfDocuments.Single();
            Assert.Equal("AcceptedByDgii", updated.State);
            Assert.Equal(FixedNow, updated.LastStatusCheckAt);
            Assert.Equal(1, updated.StatusCheckAttempts);
        }

        [Fact]
        public async Task ReconcileAsync_DgiiSaysRechazado_TransitionsToRejectedByDgii()
        {
            using var db = NewDb();
            var doc = MakeSentDocument(sentAt: FixedNow.AddHours(-1));
            db.EcfDocuments.Add(doc);
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Estado = "Rechazado" });

            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            Assert.Equal("RejectedByDgii", db.EcfDocuments.Single().State);
        }

        [Fact]
        public async Task ReconcileAsync_DgiiSaysAceptadoCondicional_TransitionsToAcceptedByDgii()
        {
            using var db = NewDb();
            var doc = MakeSentDocument(sentAt: FixedNow.AddHours(-1));
            db.EcfDocuments.Add(doc);
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Estado = "Aceptado condicional" });

            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            Assert.Equal("AcceptedByDgii", db.EcfDocuments.Single().State);
        }

        [Fact]
        public async Task ReconcileAsync_DgiiSaysNoEncontrado_StaysSigned_ButBumpsAttempts_ForNextPass()
        {
            using var db = NewDb();
            var doc = MakeSentDocument(sentAt: FixedNow.AddHours(-1));
            db.EcfDocuments.Add(doc);
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Estado = "No encontrado" });

            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            var updated = db.EcfDocuments.Single();
            Assert.Equal("Signed",updated.State);
            Assert.Equal(1, updated.StatusCheckAttempts);
            Assert.Equal(FixedNow, updated.LastStatusCheckAt);
        }

        [Fact]
        public async Task ReconcileAsync_PolledRecently_IsSkipped_UntilPollingIntervalElapses()
        {
            using var db = NewDb();
            var doc = MakeSentDocument(sentAt: FixedNow.AddHours(-1), lastCheck: FixedNow.AddMinutes(-5));
            db.EcfDocuments.Add(doc);
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            ecfClientMock.Verify(c => c.ConsultarEstadoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ReconcileAsync_ExceedsMaxPollingWindow_EscalatesToRequiresManualReview_WithoutCallingDgiiAgain()
        {
            using var db = NewDb();
            var doc = MakeSentDocument(sentAt: FixedNow.AddHours(-73), lastCheck: FixedNow.AddHours(-1));
            db.EcfDocuments.Add(doc);
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            Assert.Equal("RequiresManualReview", db.EcfDocuments.Single().State);
            ecfClientMock.Verify(c => c.ConsultarEstadoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ReconcileAsync_DgiiCallThrows_LeavesStateUnchanged_ButBumpsAttemptsAndLastCheck_SoItRetriesNextPass()
        {
            using var db = NewDb();
            var doc = MakeSentDocument(sentAt: FixedNow.AddHours(-1));
            db.EcfDocuments.Add(doc);
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("DGII unreachable"));

            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            var updated = db.EcfDocuments.Single();
            Assert.Equal("Signed",updated.State);
            Assert.Equal(1, updated.StatusCheckAttempts);
            Assert.Equal(FixedNow, updated.LastStatusCheckAt);
        }

        [Fact]
        public async Task ReconcileAsync_StillPicksUpRowsWrittenUnderTheOldSentToDgiiName()
        {
            using var db = NewDb();
            db.EcfDocuments.Add(MakeSentDocument(sentAt: FixedNow.AddHours(-1), state: "SentToDgii"));
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Estado = "Aceptado" });

            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            Assert.Equal("AcceptedByDgii", db.EcfDocuments.Single().State);
        }

        [Fact]
        public async Task ReconcileAsync_IgnoresDocumentsInStatesItDoesNotOwn()
        {
            using var db = NewDb();
            db.EcfDocuments.Add(MakeSentDocument(sentAt: FixedNow.AddHours(-1), state: "RejectedByDgii"));
            db.EcfDocuments.Add(MakeSentDocument(sentAt: FixedNow.AddHours(-1), state: "AcceptedByDgii"));
            db.EcfDocuments.Add(MakeSentDocument(sentAt: FixedNow.AddHours(-1), state: "Received"));
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            ecfClientMock.Verify(c => c.ConsultarEstadoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ReconcileAsync_SweepsUncertainDocuments_AndReconcilesThem()
        {
            using var db = NewDb();
            db.EcfDocuments.Add(MakeSentDocument(sentAt: FixedNow.AddHours(-1), state: "Uncertain"));
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Estado = "Aceptado" });

            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            Assert.Equal("AcceptedByDgii", db.EcfDocuments.Single().State);
        }

        [Fact]
        public async Task ReconcileAsync_ReturnsTheNumberOfDocumentsItActuallyProcessed()
        {
            using var db = NewDb();
            db.EcfDocuments.Add(MakeSentDocument(sentAt: FixedNow.AddHours(-1)));
            db.EcfDocuments.Add(MakeSentDocument(sentAt: FixedNow.AddMinutes(-1)));
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Estado = "No encontrado" });

            var processed = await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            Assert.Equal(1, processed);
        }

        [Fact]
        public async Task ReconcileAsync_UncertainRfceAndDgiiSaysNoEncontrado_RetransmitsSignedRfceVerbatim_AndTransitionsToAcceptedByDgii()
        {
            using var db = NewDb();
            const string exactSignedRfce = "<RFCE><Encabezado><Emisor><FechaEmision>04-11-2026</FechaEmision></Emisor><Comprador><RazonSocialComprador>COMERCIO DEMO</RazonSocialComprador></Comprador><CodigoSeguridadeCF>DmY123</CodigoSeguridadeCF></Encabezado></RFCE>";
            var doc = new EcfDocument
            {
                RncEmisor = "101000001",
                ENcf = "E320000000190",
                SecurityCode = "DmY123",
                TenantId = "sede-principal-101000001",
                Ambiente = "Produccion",
                SourceTxnId = "TXN-DEMO-0190",
                EditSequence = "1",
                TotalAmount = 1343.74m,
                ItbisAmount = 204.98m,
                State = "Uncertain",
                XmlContent = "<ECF/>",
                SignedXmlContent = "<ECF><Signed/></ECF>",
                SignedRfceContent = exactSignedRfce
            };
            db.EcfDocuments.Add(doc);
            await db.SaveChangesAsync();

            var evalNow = DateTime.UtcNow.AddMinutes(10);
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(
                    "101000001", "E320000000190", null, "DmY123", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Estado = "No encontrado" });
            ecfClientMock.Setup(c => c.SendRfceAsync(
                    exactSignedRfce, "101000001E320000000190.xml", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RfceRecepcionResponse { Codigo = 1, Estado = "Aceptado" });

            await MakeReconciler(db, ecfClientMock, MakeClockMock(evalNow)).ReconcileAsync(CancellationToken.None);

            var updated = db.EcfDocuments.Single();
            Assert.Equal("AcceptedByDgii", updated.State);
            Assert.Equal("DmY123", updated.SecurityCode);
            Assert.Null(updated.TrackId);
            ecfClientMock.Verify(c => c.SendRfceAsync(exactSignedRfce, "101000001E320000000190.xml", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ReconcileAsync_UncertainStandardEcfAndDgiiSaysNoEncontrado_RetransmitsSignedXmlVerbatim_AndPersistsTrackId()
        {
            using var db = NewDb();
            const string exactSignedXml = "<ECF><SignedStandard/></ECF>";
            var doc = new EcfDocument
            {
                RncEmisor = "101000001",
                ENcf = "E340000000075",
                RncComprador = "130000000",
                SecurityCode = "XyZ987",
                TenantId = "sede-principal-101000001",
                Ambiente = "Produccion",
                SourceTxnId = "TXN-DEMO-0075",
                EditSequence = "1",
                TotalAmount = 1500.00m,
                ItbisAmount = 228.81m,
                State = "Uncertain",
                XmlContent = "<ECF/>",
                SignedXmlContent = exactSignedXml
            };
            db.EcfDocuments.Add(doc);
            await db.SaveChangesAsync();

            var evalNow = DateTime.UtcNow.AddMinutes(10);
            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(
                    "101000001", "E340000000075", "130000000", "XyZ987", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Estado = "No encontrado" });
            ecfClientMock.Setup(c => c.SendEcfAsync(
                    exactSignedXml, "101000001E340000000075.xml", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EcfRecepcionResponse { TrackId = "TRK-PROD-475" });

            await MakeReconciler(db, ecfClientMock, MakeClockMock(evalNow)).ReconcileAsync(CancellationToken.None);

            var updated = db.EcfDocuments.Single();
            Assert.Equal("Signed", updated.State);
            Assert.Equal("TRK-PROD-475", updated.TrackId);
            Assert.Equal("XyZ987", updated.SecurityCode);
            ecfClientMock.Verify(c => c.SendEcfAsync(exactSignedXml, "101000001E340000000075.xml", It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
