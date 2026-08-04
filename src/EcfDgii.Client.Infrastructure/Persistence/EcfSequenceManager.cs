using System;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EcfDgii.Client.Infrastructure.Persistence
{
    public interface IEcfSequenceManager
    {
        Task<string> GetNextEncfAsync(string tenantId, string tipoComprobante, CancellationToken cancellationToken = default);
    }

    public class EcfSequenceManager : IEcfSequenceManager
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public EcfSequenceManager(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async Task<string> GetNextEncfAsync(string tenantId, string tipoComprobante, CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var sequence = await db.Sequences
                .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.TipoComprobante == tipoComprobante && s.IsActive, cancellationToken);

            if (sequence == null)
            {
                // Auto-provision sequence range if not existing for default tenant / type
                var prefix = tipoComprobante.StartsWith("E", StringComparison.OrdinalIgnoreCase) ? tipoComprobante : $"E{tipoComprobante}";
                sequence = new EcfSequence
                {
                    TenantId = tenantId,
                    TipoComprobante = tipoComprobante,
                    Prefix = prefix,
                    RangoDesde = 1,
                    RangoHasta = 9999999999,
                    SecuenciaActual = 0,
                    IsActive = true,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.Sequences.Add(sequence);
                await db.SaveChangesAsync(cancellationToken);
            }

            if (sequence.FechaVencimiento.HasValue && sequence.FechaVencimiento.Value < DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException($"eNCF authorization range for type '{tipoComprobante}' expired on {sequence.FechaVencimiento.Value:yyyy-MM-dd}.");
            }

            if (sequence.SecuenciaActual >= sequence.RangoHasta)
            {
                throw new InvalidOperationException($"eNCF sequence range exhausted for type '{tipoComprobante}'. Max allowed: {sequence.RangoHasta}.");
            }

            sequence.SecuenciaActual++;
            sequence.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            return sequence.GetNextEncfFormatted();
        }
    }
}
