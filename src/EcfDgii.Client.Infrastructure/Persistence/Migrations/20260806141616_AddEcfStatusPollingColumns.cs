using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EcfDgii.Client.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEcfStatusPollingColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_status_check_at",
                table: "ecf_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "sent_to_dgii_at",
                table: "ecf_documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "status_check_attempts",
                table: "ecf_documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_ecf_documents_state_last_status_check_at",
                table: "ecf_documents",
                columns: new[] { "state", "last_status_check_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ecf_documents_state_last_status_check_at",
                table: "ecf_documents");

            migrationBuilder.DropColumn(
                name: "last_status_check_at",
                table: "ecf_documents");

            migrationBuilder.DropColumn(
                name: "sent_to_dgii_at",
                table: "ecf_documents");

            migrationBuilder.DropColumn(
                name: "status_check_attempts",
                table: "ecf_documents");
        }
    }
}
