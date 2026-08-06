using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using EcfDgii.Client.Domain.Entities;

namespace EcfDgii.Client.Infrastructure.Persistence.Configurations
{
    public class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            builder.ToTable("users");

            builder.HasKey(u => u.Id)
                .HasName("pk_users");

            builder.Property(u => u.Id)
                .HasColumnName("id");

            builder.Property(u => u.Username)
                .HasColumnName("username")
                .HasMaxLength(100)
                .IsRequired();

            builder.Property(u => u.Email)
                .HasColumnName("email")
                .HasMaxLength(150)
                .IsRequired();

            builder.Property(u => u.PasswordHash)
                .HasColumnName("password_hash")
                .IsRequired();

            builder.Property(u => u.Role)
                .HasColumnName("role")
                .HasMaxLength(50)
                .IsRequired();

            // Auditing Columns
            builder.Property(u => u.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired();

            builder.Property(u => u.CreatedBy)
                .HasColumnName("created_by")
                .HasMaxLength(100);

            builder.Property(u => u.UpdatedAt)
                .HasColumnName("updated_at");

            builder.Property(u => u.UpdatedBy)
                .HasColumnName("updated_by")
                .HasMaxLength(100);

            builder.Property(u => u.DeletedAt)
                .HasColumnName("deleted_at");

            builder.Property(u => u.DeletedBy)
                .HasColumnName("deleted_by")
                .HasMaxLength(100);

            builder.Property(u => u.IsDeleted)
                .HasColumnName("is_deleted")
                .IsRequired();

            // Unique Constraints and Indexes
            builder.HasIndex(u => u.Username)
                .IsUnique()
                .HasDatabaseName("uq_users_username");

            builder.HasIndex(u => u.Email)
                .IsUnique()
                .HasDatabaseName("uq_users_email");

            // Seed initial Admin User.
            // The password this hash used to correspond to ("AdminPassword123!") was printed in
            // plaintext in this file's own history and in the README — anyone with either is
            // effectively public. This migration rotates the hash (see
            // 2026*_RotateSeededAdminPassword.cs); the new plaintext password is not committed
            // anywhere — it was communicated out of band when this rotation was made. Whoever holds
            // it should log in and change it immediately, ideally through a real first-login
            // forced-password-change flow rather than another committed seed.
            builder.HasData(new User
            {
                Id = Guid.Parse("9f3c7e09-e85d-452f-9877-c93d90fcb32d"),
                Username = "admin",
                Email = "admin@ecfdgii.client.com",
                PasswordHash = "$2a$11$TrZocoksYo3ZzpTKy5XdZuw6LBumk7obuD5Viyzo/dTsdAA3ikkDW",
                Role = "Admin",
                CreatedAt = new DateTime(2026, 6, 26, 0, 0, 0, DateTimeKind.Utc),
                CreatedBy = "System",
                IsDeleted = false
            });
        }
    }
}
