using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using EcfDgii.Client.Domain.Entities;

namespace EcfDgii.Client.Infrastructure.Persistence.Configurations
{
    public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
    {
        public void Configure(EntityTypeBuilder<Customer> builder)
        {
            builder.ToTable("customers");

            builder.HasKey(c => c.Id)
                .HasName("pk_customers");

            builder.Property(c => c.Id)
                .HasColumnName("id");

            builder.Property(c => c.Name)
                .HasColumnName("name")
                .HasMaxLength(200)
                .IsRequired();

            builder.Property(c => c.Email)
                .HasColumnName("email")
                .HasMaxLength(150);

            builder.Property(c => c.Rnc)
                .HasColumnName("rnc")
                .HasMaxLength(20)
                .IsRequired();

            // Auditing Columns
            builder.Property(c => c.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired();

            builder.Property(c => c.CreatedBy)
                .HasColumnName("created_by")
                .HasMaxLength(100);

            builder.Property(c => c.UpdatedAt)
                .HasColumnName("updated_at");

            builder.Property(c => c.UpdatedBy)
                .HasColumnName("updated_by")
                .HasMaxLength(100);

            builder.Property(c => c.DeletedAt)
                .HasColumnName("deleted_at");

            builder.Property(c => c.DeletedBy)
                .HasColumnName("deleted_by")
                .HasMaxLength(100);

            builder.Property(c => c.IsDeleted)
                .HasColumnName("is_deleted")
                .IsRequired();

            // Indexes
            builder.HasIndex(c => c.Rnc)
                .HasDatabaseName("ix_customers_rnc");

            // Seed initial data
            builder.HasData(new Customer
            {
                Id = Guid.Parse("f98f6d61-d24f-4a0b-967b-1d7c0f135b5a"),
                Name = "Consumidor Final Genérico",
                Email = "consumidorfinal@ecfdgii.client.com",
                Rnc = "22400013743", // Test consumer RNC
                CreatedAt = new DateTime(2026, 6, 26, 0, 0, 0, DateTimeKind.Utc),
                CreatedBy = "System",
                IsDeleted = false
            });
        }
    }
}
