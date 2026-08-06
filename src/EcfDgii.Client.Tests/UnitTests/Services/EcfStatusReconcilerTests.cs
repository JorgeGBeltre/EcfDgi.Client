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
    /// <summary>
    /// EcfStatusReconciler closes the ⑤/⑥ gap: nothing else in the codebase ever polls DGII for what
    /// happened to a document AFTER it reached "SentToDgii" — that state was previously fully
    /// terminal. Without this, an e-CF DGII accepts on receipt and later rejects on verification (a
    /// real, documented DGII outcome — "Rechazado" per the DGII_md service description) stays marked
    /// Sent forever, and the mandatory acknowledgment window elapses unnoticed.
    /// </summary>
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

        private static EcfDocument MakeSentDocument(DateTime sentAt, DateTime? lastCheck = null, string state = "SentToDgii") =>
            new()
            {
                RncEmisor = "101889063",
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
            // Matches DocumentsController's own MinimumUncertainAgeBeforeReconciliation precedent:
            // DGII's own status query can lag behind actual receipt, so polling immediately after
            // send would just waste a call and see stale "No encontrado" noise.
            using var db = NewDb();
            var doc = MakeSentDocument(sentAt: FixedNow.AddMinutes(-1)); // younger than MinDocumentAge (2 min)
            db.EcfDocuments.Add(doc);
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            ecfClientMock.Verify(c => c.ConsultarEstadoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.Equal("SentToDgii", db.EcfDocuments.Single().State);
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
                    "101889063", "E310000000001", "130000000", "ABC123", It.IsAny<CancellationToken>()))
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
            // The exact scenario the user flagged: accepted-on-receipt, rejected-on-verification —
            // this must not stay silently marked Sent.
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
        public async Task ReconcileAsync_DgiiSaysNoEncontrado_StaysSentToDgii_ButBumpsAttempts_ForNextPass()
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
            Assert.Equal("SentToDgii", updated.State);
            Assert.Equal(1, updated.StatusCheckAttempts);
            Assert.Equal(FixedNow, updated.LastStatusCheckAt);
        }

        [Fact]
        public async Task ReconcileAsync_PolledRecently_IsSkipped_UntilPollingIntervalElapses()
        {
            using var db = NewDb();
            var doc = MakeSentDocument(sentAt: FixedNow.AddHours(-1), lastCheck: FixedNow.AddMinutes(-5)); // < 15min PollingInterval
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
            var doc = MakeSentDocument(sentAt: FixedNow.AddHours(-73), lastCheck: FixedNow.AddHours(-1)); // > 72h MaxPollingWindow
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
            Assert.Equal("SentToDgii", updated.State);
            Assert.Equal(1, updated.StatusCheckAttempts);
            Assert.Equal(FixedNow, updated.LastStatusCheckAt);
        }

        [Fact]
        public async Task ReconcileAsync_IgnoresDocumentsNotInSentToDgiiState()
        {
            using var db = NewDb();
            db.EcfDocuments.Add(MakeSentDocument(sentAt: FixedNow.AddHours(-1), state: "RejectedByDgii"));
            db.EcfDocuments.Add(MakeSentDocument(sentAt: FixedNow.AddHours(-1), state: "AcceptedByDgii"));
            db.EcfDocuments.Add(MakeSentDocument(sentAt: FixedNow.AddHours(-1), state: "Uncertain"));
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            ecfClientMock.Verify(c => c.ConsultarEstadoAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ReconcileAsync_ReturnsTheNumberOfDocumentsItActuallyProcessed()
        {
            using var db = NewDb();
            db.EcfDocuments.Add(MakeSentDocument(sentAt: FixedNow.AddHours(-1))); // due
            db.EcfDocuments.Add(MakeSentDocument(sentAt: FixedNow.AddMinutes(-1))); // too fresh, skipped
            await db.SaveChangesAsync();

            var ecfClientMock = new Mock<IEcfClient>();
            ecfClientMock.Setup(c => c.ConsultarEstadoAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ConsultaEstadoResponse { Estado = "No encontrado" });

            var processed = await MakeReconciler(db, ecfClientMock).ReconcileAsync(CancellationToken.None);

            Assert.Equal(1, processed);
        }
    }
}
