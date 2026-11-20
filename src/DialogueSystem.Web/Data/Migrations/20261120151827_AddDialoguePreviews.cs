using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDialoguePreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Previews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    DialogueId = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentLineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Step = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Previews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Previews_Accounts_UserId",
                        column: x => x.UserId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Previews_Lines_DialogueId_CurrentLineId",
                        columns: x => new { x.DialogueId, x.CurrentLineId },
                        principalTable: "Lines",
                        principalColumns: new[] { "DialogueId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Previews_DialogueId_CurrentLineId",
                table: "Previews",
                columns: new[] { "DialogueId", "CurrentLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_Previews_UserId",
                table: "Previews",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Previews");
        }
    }
}
