using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackCharacterOrigins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginKey",
                table: "Characters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Characters_OriginKey_Name",
                table: "Characters",
                columns: new[] { "OriginKey", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Characters_OriginKey_Name",
                table: "Characters");

            migrationBuilder.Sql("ALTER TABLE Characters DROP COLUMN OriginKey;");
        }
    }
}
