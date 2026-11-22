using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPreviewHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Previews_UserId",
                table: "Previews");

            migrationBuilder.AddColumn<long>(
                name: "StartedAtUnixMilliseconds",
                table: "Previews",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PreviewHistoryEntry",
                columns: table => new
                {
                    PreviewId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Step = table.Column<int>(type: "INTEGER", nullable: false),
                    LineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Speaker = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnding = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChoiceText = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreviewHistoryEntry", x => new { x.PreviewId, x.Step });
                    table.ForeignKey(
                        name: "FK_PreviewHistoryEntry_Previews_PreviewId",
                        column: x => x.PreviewId,
                        principalTable: "Previews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Previews_UserId_DialogueId_StartedAtUnixMilliseconds",
                table: "Previews",
                columns: new[] { "UserId", "DialogueId", "StartedAtUnixMilliseconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreviewHistoryEntry");

            migrationBuilder.DropIndex(
                name: "IX_Previews_UserId_DialogueId_StartedAtUnixMilliseconds",
                table: "Previews");

            migrationBuilder.DropColumn(
                name: "StartedAtUnixMilliseconds",
                table: "Previews");

            migrationBuilder.CreateIndex(
                name: "IX_Previews_UserId",
                table: "Previews",
                column: "UserId");
        }
    }
}
