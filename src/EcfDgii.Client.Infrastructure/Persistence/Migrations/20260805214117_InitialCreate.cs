using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EcfDgii.Client.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    rnc = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ecf_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ncf = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    e_ncf = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rnc_emisor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rnc_comprador = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    source_txn_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    edit_sequence = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    document_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    track_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    state = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    itbis_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    security_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    xml_content = table.Column<string>(type: "text", nullable: false),
                    signed_xml_content = table.Column<string>(type: "text", nullable: true),
                    dgii_response_xml = table.Column<string>(type: "text", nullable: true),
                    receipt_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ecf_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ecf_idempotency_records",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    created_by_worker_key_id = table.Column<string>(type: "text", nullable: false),
                    payload_hash = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ecf_idempotency_records", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "ecf_sequences",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    tipo_comprobante = table.Column<string>(type: "text", nullable: false),
                    prefix = table.Column<string>(type: "text", nullable: false),
                    rango_desde = table.Column<long>(type: "bigint", nullable: false),
                    rango_hasta = table.Column<long>(type: "bigint", nullable: false),
                    secuencia_actual = table.Column<long>(type: "bigint", nullable: false),
                    fecha_vencimiento = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ecf_sequences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.InsertData(
                table: "customers",
                columns: new[] { "id", "created_at", "created_by", "deleted_at", "deleted_by", "email", "is_deleted", "name", "rnc", "updated_at", "updated_by" },
                values: new object[] { new Guid("f98f6d61-d24f-4a0b-967b-1d7c0f135b5a"), new DateTime(2026, 6, 26, 0, 0, 0, 0, DateTimeKind.Utc), "System", null, null, "consumidorfinal@ecfdgii.client.com", false, "Consumidor Final Genérico", "22400013743", null, null });

            migrationBuilder.InsertData(
                table: "users",
                columns: new[] { "id", "created_at", "created_by", "deleted_at", "deleted_by", "email", "is_deleted", "password_hash", "role", "updated_at", "updated_by", "username" },
                values: new object[] { new Guid("9f3c7e09-e85d-452f-9877-c93d90fcb32d"), new DateTime(2026, 6, 26, 0, 0, 0, 0, DateTimeKind.Utc), "System", null, null, "admin@ecfdgii.client.com", false, "$2a$11$yHgpsPOsooH4yxAXvMiRXO.mA22AwAaRY.eb69RmF3v1JZBmu3T56", "Admin", null, null, "admin" });

            migrationBuilder.CreateIndex(
                name: "ix_customers_rnc",
                table: "customers",
                column: "rnc");

            migrationBuilder.CreateIndex(
                name: "ix_ecf_documents_state",
                table: "ecf_documents",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_ecf_documents_track_id",
                table: "ecf_documents",
                column: "track_id");

            migrationBuilder.CreateIndex(
                name: "uq_ecf_documents_rnc_emisor_encf",
                table: "ecf_documents",
                columns: new[] { "rnc_emisor", "e_ncf" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_ecf_documents_tenant_source_txn",
                table: "ecf_documents",
                columns: new[] { "tenant_id", "source_txn_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ecf_idempotency_records_expires_at",
                table: "ecf_idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "uq_ecf_sequences_tenant_tipo",
                table: "ecf_sequences",
                columns: new[] { "tenant_id", "tipo_comprobante" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_users_username",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropTable(
                name: "ecf_documents");

            migrationBuilder.DropTable(
                name: "ecf_idempotency_records");

            migrationBuilder.DropTable(
                name: "ecf_sequences");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
