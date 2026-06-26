using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;

namespace EcfDgii.Client.Infrastructure.Persistence.Repositories
{
    public class EcfDocumentRepository : IEcfDocumentRepository
    {
        private readonly ApplicationDbContext _dbContext;

        public EcfDocumentRepository(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public Task<EcfDocument?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            return _dbContext.EcfDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);
        }

        public Task<EcfDocument?> GetByENcfAsync(string eNcf, CancellationToken ct = default)
        {
            return _dbContext.EcfDocuments.FirstOrDefaultAsync(d => d.ENcf == eNcf, ct);
        }

        public Task<EcfDocument?> GetByTrackIdAsync(string trackId, CancellationToken ct = default)
        {
            return _dbContext.EcfDocuments.FirstOrDefaultAsync(d => d.TrackId == trackId, ct);
        }

        public Task<List<EcfDocument>> GetAllAsync(CancellationToken ct = default)
        {
            return _dbContext.EcfDocuments.ToListAsync(ct);
        }

        public async Task AddAsync(EcfDocument document, CancellationToken ct = default)
        {
            await _dbContext.EcfDocuments.AddAsync(document, ct);
        }

        public void Update(EcfDocument document)
        {
            _dbContext.EcfDocuments.Update(document);
        }
    }
}
