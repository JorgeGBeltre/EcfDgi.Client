using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EcfDgii.Client.Infrastructure.Persistence.Migrations
{
    public partial class AddSignedRfceContentColumn : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "signed_rfce_content",
                table: "ecf_documents",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "signed_rfce_content",
                table: "ecf_documents");
        }
    }
}
