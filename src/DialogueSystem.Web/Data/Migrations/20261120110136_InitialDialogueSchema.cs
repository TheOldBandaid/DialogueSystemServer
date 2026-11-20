using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialDialogueSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Characters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Characters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Choices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DialogueId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceLineId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetLineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Choices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Dialogues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    StartLineId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dialogues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Lines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DialogueId = table.Column<int>(type: "INTEGER", nullable: false),
                    SpeakerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnding = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsStoryBranch = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lines", x => x.Id);
                    table.UniqueConstraint("AK_Lines_DialogueId_Id", x => new { x.DialogueId, x.Id });
                    table.ForeignKey(
                        name: "FK_Lines_Characters_SpeakerId",
                        column: x => x.SpeakerId,
                        principalTable: "Characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Lines_Dialogues_DialogueId",
                        column: x => x.DialogueId,
                        principalTable: "Dialogues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Choices_DialogueId_SourceLineId_Position",
                table: "Choices",
                columns: new[] { "DialogueId", "SourceLineId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Choices_DialogueId_TargetLineId",
                table: "Choices",
                columns: new[] { "DialogueId", "TargetLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_Dialogues_Id_StartLineId",
                table: "Dialogues",
                columns: new[] { "Id", "StartLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_Lines_SpeakerId",
                table: "Lines",
                column: "SpeakerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Choices_Lines_DialogueId_SourceLineId",
                table: "Choices",
                columns: new[] { "DialogueId", "SourceLineId" },
                principalTable: "Lines",
                principalColumns: new[] { "DialogueId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Choices_Lines_DialogueId_TargetLineId",
                table: "Choices",
                columns: new[] { "DialogueId", "TargetLineId" },
                principalTable: "Lines",
                principalColumns: new[] { "DialogueId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Dialogues_Lines_Id_StartLineId",
                table: "Dialogues",
                columns: new[] { "Id", "StartLineId" },
                principalTable: "Lines",
                principalColumns: new[] { "DialogueId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Dialogues_Lines_Id_StartLineId",
                table: "Dialogues");

            migrationBuilder.DropTable(
                name: "Choices");

            migrationBuilder.DropTable(
                name: "Lines");

            migrationBuilder.DropTable(
                name: "Characters");

            migrationBuilder.DropTable(
                name: "Dialogues");
        }
    }
}
