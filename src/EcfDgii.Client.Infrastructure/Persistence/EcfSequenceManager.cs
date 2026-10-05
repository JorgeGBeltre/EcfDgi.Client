using System;
using System.Collections.Generic;
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
        private static readonly HashSet<string> ValidTiposComprobante = new(StringComparer.OrdinalIgnoreCase)
        {
            "E31", "E32", "E33", "E34", "E41", "E43", "E44", "E45", "E46", "E47",
            "31", "32", "33", "34", "41", "43", "44", "45", "46", "47"
        };

        private static readonly SemaphoreSlim _sequenceLock = new(1, 1);
        private readonly IServiceScopeFactory _scopeFactory;

        public EcfSequenceManager(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async Task<string> GetNextEncfAsync(string tenantId, string tipoComprobante, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(tipoComprobante) || !ValidTiposComprobante.Contains(tipoComprobante))
            {
                throw new ArgumentException($"Tipo de comprobante '{tipoComprobante}' no es válido ni soportado por DGII.", nameof(tipoComprobante));
            }

            await _sequenceLock.WaitAsync(cancellationToken);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(cancellationToken)
                    : null;

                if (db.Database.IsRelational())
                {
                    var lockKey = $"{tenantId}:{tipoComprobante}";
                    await db.Database.ExecuteSqlRawAsync(
                        "SELECT pg_advisory_xact_lock(hashtext({0}))",
                        new object[] { lockKey },
                        cancellationToken);
                }

                var sequence = await db.Sequences
                    .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.TipoComprobante == tipoComprobante, cancellationToken);

                if (sequence == null)
                {
                    // Fallback check between PreCertificacion and TestEcf, or Certificacion and CertEcf
                    string? altTenantId = null;
                    if (tenantId.EndsWith(":PreCertificacion", StringComparison.OrdinalIgnoreCase))
                        altTenantId = tenantId[..^":PreCertificacion".Length] + ":TestEcf";
                    else if (tenantId.EndsWith(":TestEcf", StringComparison.OrdinalIgnoreCase))
                        altTenantId = tenantId[..^":TestEcf".Length] + ":PreCertificacion";
                    else if (tenantId.EndsWith(":Certificacion", StringComparison.OrdinalIgnoreCase))
                        altTenantId = tenantId[..^":Certificacion".Length] + ":CertEcf";
                    else if (tenantId.EndsWith(":CertEcf", StringComparison.OrdinalIgnoreCase))
                        altTenantId = tenantId[..^":CertEcf".Length] + ":Certificacion";

                    if (altTenantId != null)
                    {
                        sequence = await db.Sequences
                            .FirstOrDefaultAsync(s => s.TenantId == altTenantId && s.TipoComprobante == tipoComprobante, cancellationToken);
                    }
                }

                if (sequence != null && !sequence.IsActive)
                {
                    throw new InvalidOperationException(
                        $"El rango de secuencias e-NCF para el tenant '{sequence.TenantId}' y tipo '{tipoComprobante}' se encuentra inactivo. Debe activar o registrar un rango vigente autorizado por la DGII.");
                }

                if (sequence == null)
                {
                    // HIGH-043, HIGH-044, HIGH-045: Rehusar inventar rangos 1..9,999,999,999 arbitrarios no autorizados por DGII.
                    // Solo permitir en fixtures unitarios en memoria para "default-tenant".
                    var isInMemory = !db.Database.IsRelational();
                    if (tenantId == "default-tenant" && isInMemory)
                    {
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
                            FechaVencimiento = DateTimeOffset.UtcNow.AddYears(1),
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                        try
                        {
                            db.Sequences.Add(sequence);
                            await db.SaveChangesAsync(cancellationToken);
                        }
                        catch (DbUpdateException)
                        {
                            sequence = await db.Sequences
                                .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.TipoComprobante == tipoComprobante, cancellationToken);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"No existe un rango de secuencias e-NCF autorizado por la DGII para el tenant '{tenantId}' y tipo '{tipoComprobante}'. Debe registrar un rango autorizado antes de emitir comprobantes.");
                    }
                }

                if (sequence == null || !sequence.IsActive)
                {
                    throw new InvalidOperationException(
                        $"No existe un rango de secuencias e-NCF activo para el tenant '{tenantId}' y tipo '{tipoComprobante}'.");
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

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return sequence.GetNextEncfFormatted();
            }
            finally
            {
                _sequenceLock.Release();
            }
        }
    }
}
