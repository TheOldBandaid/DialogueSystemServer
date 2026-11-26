using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Key",
                table: "Characters",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE Characters SET Key = lower(hex(randomblob(16)));");

            migrationBuilder.CreateIndex(
                name: "IX_Characters_Key",
                table: "Characters",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Characters_Key",
                table: "Characters");

            // Native DROP COLUMN preserves the revision trigger and referencing tables.
            // EF's SQLite table rebuild would silently discard this custom trigger.
            migrationBuilder.Sql("ALTER TABLE Characters DROP COLUMN Key;");
        }
    }
}
