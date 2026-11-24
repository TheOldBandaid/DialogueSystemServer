using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDialogueRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DialogueRevisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DialogueId = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    LineCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ChoiceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DialogueRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DialogueRevisions_Dialogues_DialogueId",
                        column: x => x.DialogueId,
                        principalTable: "Dialogues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DialogueRevisions_DialogueId_Id",
                table: "DialogueRevisions",
                columns: new[] { "DialogueId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DialogueRevisions");
        }
    }
}
