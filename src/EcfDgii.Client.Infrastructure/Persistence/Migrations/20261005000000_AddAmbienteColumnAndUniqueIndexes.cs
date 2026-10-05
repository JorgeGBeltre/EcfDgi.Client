using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EcfDgii.Client.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAmbienteColumnAndUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ambiente",
                table: "ecf_documents",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            // Drop old non-ambiente indexes if they exist
            migrationBuilder.DropIndex(
                name: "uq_ecf_documents_rnc_emisor_encf",
                table: "ecf_documents");

            migrationBuilder.DropIndex(
                name: "uq_ecf_documents_tenant_source_txn",
                table: "ecf_documents");

            // Create new composite unique indexes with ambiente
            migrationBuilder.CreateIndex(
                name: "uq_ecf_documents_rnc_emisor_encf",
                table: "ecf_documents",
                columns: new[] { "rnc_emisor", "e_ncf", "ambiente" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_ecf_documents_tenant_source_txn",
                table: "ecf_documents",
                columns: new[] { "tenant_id", "source_txn_id", "ambiente" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_ecf_documents_tenant_source_txn",
                table: "ecf_documents");

            migrationBuilder.DropIndex(
                name: "uq_ecf_documents_rnc_emisor_encf",
                table: "ecf_documents");

            migrationBuilder.CreateIndex(
                name: "uq_ecf_documents_tenant_source_txn",
                table: "ecf_documents",
                columns: new[] { "tenant_id", "source_txn_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_ecf_documents_rnc_emisor_encf",
                table: "ecf_documents",
                columns: new[] { "rnc_emisor", "e_ncf" },
                unique: true);

            migrationBuilder.DropColumn(
                name: "ambiente",
                table: "ecf_documents");
        }
    }
}
