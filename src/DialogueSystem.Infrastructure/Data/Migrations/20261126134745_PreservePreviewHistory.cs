using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PreservePreviewHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Previews_Lines_DialogueId_CurrentLineId",
                table: "Previews");

            migrationBuilder.DropIndex(
                name: "IX_Previews_DialogueId_CurrentLineId",
                table: "Previews");

            migrationBuilder.CreateIndex(
                name: "IX_Previews_DialogueId",
                table: "Previews",
                column: "DialogueId");

            migrationBuilder.AddForeignKey(
                name: "FK_Previews_Dialogues_DialogueId",
                table: "Previews",
                column: "DialogueId",
                principalTable: "Dialogues",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Previews_Dialogues_DialogueId",
                table: "Previews");

            migrationBuilder.DropIndex(
                name: "IX_Previews_DialogueId",
                table: "Previews");

            migrationBuilder.CreateIndex(
                name: "IX_Previews_DialogueId_CurrentLineId",
                table: "Previews",
                columns: new[] { "DialogueId", "CurrentLineId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Previews_Lines_DialogueId_CurrentLineId",
                table: "Previews",
                columns: new[] { "DialogueId", "CurrentLineId" },
                principalTable: "Lines",
                principalColumns: new[] { "DialogueId", "Id" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
