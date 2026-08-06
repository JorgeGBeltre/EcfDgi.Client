using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EcfDgii.Client.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RotateSeededAdminPasswordHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "users",
                keyColumn: "id",
                keyValue: new Guid("9f3c7e09-e85d-452f-9877-c93d90fcb32d"),
                column: "password_hash",
                value: "$2a$11$TrZocoksYo3ZzpTKy5XdZuw6LBumk7obuD5Viyzo/dTsdAA3ikkDW");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "users",
                keyColumn: "id",
                keyValue: new Guid("9f3c7e09-e85d-452f-9877-c93d90fcb32d"),
                column: "password_hash",
                value: "$2a$11$yHgpsPOsooH4yxAXvMiRXO.mA22AwAaRY.eb69RmF3v1JZBmu3T56");
        }
    }
}
