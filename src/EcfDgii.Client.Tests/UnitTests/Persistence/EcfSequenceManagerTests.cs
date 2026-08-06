using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EcfDgii.Client.UnitTests.Persistence
{
    /// <summary>
    /// The single irreversible mistake in the whole tipo-34/41 rollout: if GetNextEncfAsync did NOT
    /// discriminate by TipoComprobante, wiring a real credit-memo/bill source into the pipeline would
    /// hand the first Nota de Crédito an e-NCF from the invoice series — a number consumed in the
    /// wrong series before DGII, permanently (unlike every other finding this round, which is fixable
    /// with a code change and a retry). Verified directly against a real ApplicationDbContext
    /// (InMemory), not by reading EcfSequenceManager.cs alone — reading confirms intent, only running
    /// it confirms behavior.
    /// </summary>
    public class EcfSequenceManagerTests
    {
        private static (EcfSequenceManager Manager, ApplicationDbContext Db) MakeManager(string dbName)
        {
            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var db = provider.GetRequiredService<ApplicationDbContext>();
            return (new EcfSequenceManager(scopeFactory), db);
        }

        [Fact]
        public async Task GetNextEncfAsync_DifferentTiposComprobante_GetIndependentSequences_StartingAtOne()
        {
            var (manager, db) = MakeManager(Guid.NewGuid().ToString());

            var firstInvoice = await manager.GetNextEncfAsync("default-tenant", "E31", CancellationToken.None);
            var firstCreditNote = await manager.GetNextEncfAsync("default-tenant", "E34", CancellationToken.None);

            Assert.Equal("E310000000001", firstInvoice);
            Assert.Equal("E340000000001", firstCreditNote); // NOT "E340000000002" — its own series, not continuing E31's count
        }

        [Fact]
        public async Task GetNextEncfAsync_SameTipo_IncrementsWithinItsOwnSeries_UnaffectedByOtherTypes()
        {
            var (manager, db) = MakeManager(Guid.NewGuid().ToString());

            await manager.GetNextEncfAsync("default-tenant", "E31", CancellationToken.None); // E310000000001
            await manager.GetNextEncfAsync("default-tenant", "E34", CancellationToken.None); // E340000000001, interleaved
            var secondInvoice = await manager.GetNextEncfAsync("default-tenant", "E31", CancellationToken.None);
            var secondCreditNote = await manager.GetNextEncfAsync("default-tenant", "E34", CancellationToken.None);

            Assert.Equal("E310000000002", secondInvoice);
            Assert.Equal("E340000000002", secondCreditNote);
        }

        [Fact]
        public async Task GetNextEncfAsync_Tipo41_AlsoGetsItsOwnIndependentSeries()
        {
            var (manager, db) = MakeManager(Guid.NewGuid().ToString());

            await manager.GetNextEncfAsync("default-tenant", "E31", CancellationToken.None);
            await manager.GetNextEncfAsync("default-tenant", "E31", CancellationToken.None);
            var firstCompra = await manager.GetNextEncfAsync("default-tenant", "E41", CancellationToken.None);

            Assert.Equal("E410000000001", firstCompra);
        }

        [Fact]
        public async Task GetNextEncfAsync_ExhaustsExactlyAtRangoHasta_NeverIssuingOneNumberPast()
        {
            // Sister question to the off-by-one fix above: if the START of a range could be mishandled
            // silently, the END deserves the same scrutiny — exceeding an authorized range is worse
            // than wasting its first number. Provision a narrow range directly (bypassing the
            // auto-provision default of 9,999,999,999) to make exhaustion reachable in a test.
            var (manager, db) = MakeManager(Guid.NewGuid().ToString());
            db.Sequences.Add(new EcfDgii.Client.Domain.Entities.EcfSequence
            {
                TenantId = "default-tenant",
                TipoComprobante = "E31",
                Prefix = "E31",
                RangoDesde = 1,
                RangoHasta = 3,
                SecuenciaActual = 0,
                IsActive = true,
            });
            await db.SaveChangesAsync();

            var first = await manager.GetNextEncfAsync("default-tenant", "E31", CancellationToken.None);
            var second = await manager.GetNextEncfAsync("default-tenant", "E31", CancellationToken.None);
            var third = await manager.GetNextEncfAsync("default-tenant", "E31", CancellationToken.None);

            Assert.Equal("E310000000001", first);
            Assert.Equal("E310000000002", second);
            Assert.Equal("E310000000003", third); // the last number actually inside the authorized range

            var act = () => manager.GetNextEncfAsync("default-tenant", "E31", CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(act);

            // Confirms the persisted counter itself never advanced past the authorized limit either —
            // not just that the formatted string looked right. AsNoTracking(): `db` already tracks
            // the entity it Added above with its original SecuenciaActual=0 — without this, EF Core's
            // identity map returns that stale tracked instance instead of the value the manager's own
            // (separately-scoped) DbContext instances actually persisted.
            var sequence = await db.Sequences.AsNoTracking().SingleAsync(s => s.TipoComprobante == "E31");
            Assert.Equal(3, sequence.SecuenciaActual);
        }

        [Fact]
        public async Task GetNextEncfAsync_PersistsOneSequenceRowPerTipoComprobante_InTheRealTable()
        {
            var (manager, db) = MakeManager(Guid.NewGuid().ToString());

            await manager.GetNextEncfAsync("default-tenant", "E31", CancellationToken.None);
            await manager.GetNextEncfAsync("default-tenant", "E34", CancellationToken.None);
            await manager.GetNextEncfAsync("default-tenant", "E41", CancellationToken.None);

            var sequences = await db.Sequences.ToListAsync();
            Assert.Equal(3, sequences.Count);
            Assert.Contains(sequences, s => s.TipoComprobante == "E31" && s.SecuenciaActual == 1);
            Assert.Contains(sequences, s => s.TipoComprobante == "E34" && s.SecuenciaActual == 1);
            Assert.Contains(sequences, s => s.TipoComprobante == "E41" && s.SecuenciaActual == 1);
        }
    }
}
