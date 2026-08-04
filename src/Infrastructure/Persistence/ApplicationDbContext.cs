using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using EcfDgii.Client.Domain.Common;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Application.Common.Interfaces;

namespace EcfDgii.Client.Infrastructure.Persistence
{
    public class ApplicationDbContext : DbContext
    {
        private readonly ICurrentUserService? _currentUserService;

        public ApplicationDbContext(
            DbContextOptions<ApplicationDbContext> options,
            ICurrentUserService? currentUserService = null)
            : base(options)
        {
            _currentUserService = currentUserService;
        }

        public DbSet<User> Users => Set<User>();
        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<EcfDocument> EcfDocuments => Set<EcfDocument>();
        public DbSet<EcfIdempotencyRecord> IdempotencyRecords => Set<EcfIdempotencyRecord>();
        public DbSet<EcfSequence> Sequences => Set<EcfSequence>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<EcfIdempotencyRecord>(entity =>
            {
                entity.ToTable("ecf_idempotency_records");
                entity.HasKey(e => e.Key);
                entity.Property(e => e.Key).HasColumnName("key");
                entity.Property(e => e.CreatedByWorkerKeyId).HasColumnName("created_by_worker_key_id");
                entity.Property(e => e.PayloadHash).HasColumnName("payload_hash");
                entity.Property(e => e.Status).HasColumnName("status");
                entity.Property(e => e.StatusCode).HasColumnName("status_code");
                entity.Property(e => e.ContentType).HasColumnName("content_type");
                entity.Property(e => e.ResponseBody).HasColumnName("response_body");
                entity.Property(e => e.CreatedAt).HasColumnName("created_at");
                entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
                entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
                entity.HasIndex(e => e.ExpiresAt).HasDatabaseName("ix_ecf_idempotency_records_expires_at");
            });

            modelBuilder.Entity<EcfSequence>(entity =>
            {
                entity.ToTable("ecf_sequences");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.TenantId).HasColumnName("tenant_id");
                entity.Property(e => e.TipoComprobante).HasColumnName("tipo_comprobante");
                entity.Property(e => e.Prefix).HasColumnName("prefix");
                entity.Property(e => e.RangoDesde).HasColumnName("rango_desde");
                entity.Property(e => e.RangoHasta).HasColumnName("rango_hasta");
                entity.Property(e => e.SecuenciaActual).HasColumnName("secuencia_actual");
                entity.Property(e => e.FechaVencimiento).HasColumnName("fecha_vencimiento");
                entity.Property(e => e.IsActive).HasColumnName("is_active");
                entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
                entity.HasIndex(e => new { e.TenantId, e.TipoComprobante }).IsUnique().HasDatabaseName("uq_ecf_sequences_tenant_tipo");
            });

            // Apply all configurations from current assembly
            modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

            // Apply global soft delete filter to all entities deriving from AuditableEntity
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (typeof(AuditableEntity).IsAssignableFrom(entityType.ClrType))
                {
                    var parameter = Expression.Parameter(entityType.ClrType, "p");
                    var property = Expression.Property(parameter, nameof(AuditableEntity.IsDeleted));
                    var falseConstant = Expression.Constant(false);
                    var body = Expression.Equal(property, falseConstant);
                    var lambda = Expression.Lambda(body, parameter);

                    modelBuilder.Entity(entityType.ClrType).HasQueryFilter(lambda);
                }
            }
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var username = _currentUserService?.Username ?? "System";

            foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        entry.Entity.CreatedAt = DateTime.UtcNow;
                        entry.Entity.CreatedBy = username;
                        entry.Entity.IsDeleted = false;
                        break;

                    case EntityState.Modified:
                        entry.Entity.UpdatedAt = DateTime.UtcNow;
                        entry.Entity.UpdatedBy = username;
                        break;

                    case EntityState.Deleted:
                        // Intercept hard delete and turn into soft delete
                        entry.State = EntityState.Modified;
                        entry.Entity.IsDeleted = true;
                        entry.Entity.DeletedAt = DateTime.UtcNow;
                        entry.Entity.DeletedBy = username;
                        break;
                }
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
