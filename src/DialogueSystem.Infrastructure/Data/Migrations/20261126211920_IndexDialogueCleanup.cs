using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class IndexDialogueCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Previews_DialogueId",
                table: "Previews");

            migrationBuilder.DropIndex(
                name: "IX_Previews_UserId_DialogueId_StartedAtUnixMilliseconds",
                table: "Previews");

            migrationBuilder.CreateIndex(
                name: "IX_Previews_DialogueId_StartedAtUnixMilliseconds_Id",
                table: "Previews",
                columns: new[] { "DialogueId", "StartedAtUnixMilliseconds", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Previews_UserId_DialogueId_StartedAtUnixMilliseconds_Id",
                table: "Previews",
                columns: new[] { "UserId", "DialogueId", "StartedAtUnixMilliseconds", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DialogueRevisions_DialogueId_Author_Id",
                table: "DialogueRevisions",
                columns: new[] { "DialogueId", "Author", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Previews_DialogueId_StartedAtUnixMilliseconds_Id",
                table: "Previews");

            migrationBuilder.DropIndex(
                name: "IX_Previews_UserId_DialogueId_StartedAtUnixMilliseconds_Id",
                table: "Previews");

            migrationBuilder.DropIndex(
                name: "IX_DialogueRevisions_DialogueId_Author_Id",
                table: "DialogueRevisions");

            migrationBuilder.CreateIndex(
                name: "IX_Previews_DialogueId",
                table: "Previews",
                column: "DialogueId");

            migrationBuilder.CreateIndex(
                name: "IX_Previews_UserId_DialogueId_StartedAtUnixMilliseconds",
                table: "Previews",
                columns: new[] { "UserId", "DialogueId", "StartedAtUnixMilliseconds" });
        }
    }
}
