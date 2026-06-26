using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using EcfDgii.Client.Domain.Entities;

namespace EcfDgii.Client.Infrastructure.Persistence.Configurations
{
    public class EcfDocumentConfiguration : IEntityTypeConfiguration<EcfDocument>
    {
        public void Configure(EntityTypeBuilder<EcfDocument> builder)
        {
            builder.ToTable("ecf_documents");

            builder.HasKey(e => e.Id)
                .HasName("pk_ecf_documents");

            builder.Property(e => e.Id)
                .HasColumnName("id");

            builder.Property(e => e.ENcf)
                .HasColumnName("e_ncf")
                .HasMaxLength(20)
                .IsRequired();

            builder.Property(e => e.RncEmisor)
                .HasColumnName("rnc_emisor")
                .HasMaxLength(20)
                .IsRequired();

            builder.Property(e => e.RncComprador)
                .HasColumnName("rnc_comprador")
                .HasMaxLength(20);

            builder.Property(e => e.TrackId)
                .HasColumnName("track_id")
                .HasMaxLength(100);

            builder.Property(e => e.State)
                .HasColumnName("state")
                .HasMaxLength(50)
                .IsRequired();

            builder.Property(e => e.TotalAmount)
                .HasColumnName("total_amount")
                .HasPrecision(18, 2)
                .IsRequired();

            builder.Property(e => e.ItbisAmount)
                .HasColumnName("itbis_amount")
                .HasPrecision(18, 2)
                .IsRequired();

            builder.Property(e => e.SecurityCode)
                .HasColumnName("security_code")
                .HasMaxLength(100);

            builder.Property(e => e.XmlContent)
                .HasColumnName("xml_content")
                .IsRequired();

            builder.Property(e => e.ReceiptDate)
                .HasColumnName("receipt_date");

            // Auditing Columns
            builder.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired();

            builder.Property(e => e.CreatedBy)
                .HasColumnName("created_by")
                .HasMaxLength(100);

            builder.Property(e => e.UpdatedAt)
                .HasColumnName("updated_at");

            builder.Property(e => e.UpdatedBy)
                .HasColumnName("updated_by")
                .HasMaxLength(100);

            builder.Property(e => e.DeletedAt)
                .HasColumnName("deleted_at");

            builder.Property(e => e.DeletedBy)
                .HasColumnName("deleted_by")
                .HasMaxLength(100);

            builder.Property(e => e.IsDeleted)
                .HasColumnName("is_deleted")
                .IsRequired();

            // Composite Index (unique) for RncEmisor & ENcf
            builder.HasIndex(e => new { e.RncEmisor, e.ENcf })
                .IsUnique()
                .HasDatabaseName("uq_ecf_documents_rnc_emisor_encf");

            // Other indexes
            builder.HasIndex(e => e.TrackId)
                .HasDatabaseName("ix_ecf_documents_track_id");

            builder.HasIndex(e => e.State)
                .HasDatabaseName("ix_ecf_documents_state");
        }
    }
}
