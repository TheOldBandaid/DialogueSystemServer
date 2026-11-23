using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountSessionVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SessionVersion",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionVersion",
                table: "Accounts");
        }
    }
}
