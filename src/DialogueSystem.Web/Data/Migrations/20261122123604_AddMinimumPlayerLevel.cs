using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMinimumPlayerLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinimumPlayerLevel",
                table: "Dialogues",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinimumPlayerLevel",
                table: "Dialogues");
        }
    }
}
