using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Entities;

namespace EcfDgii.Client.Domain.Interfaces
{
    public interface IEcfDocumentRepository
    {
        Task<EcfDocument?> GetByIdAsync(Guid id, CancellationToken ct = default);
        Task<EcfDocument?> GetByENcfAsync(string eNcf, CancellationToken ct = default);
        Task<EcfDocument?> GetByTrackIdAsync(string trackId, CancellationToken ct = default);
        Task<List<EcfDocument>> GetAllAsync(CancellationToken ct = default);
        Task AddAsync(EcfDocument document, CancellationToken ct = default);
        void Update(EcfDocument document);
    }
}
