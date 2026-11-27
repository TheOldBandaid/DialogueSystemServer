using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackDialogueActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ActivityRevision",
                table: "Dialogues",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
            foreach (var table in new[] { "Previews", "DialogueRevisions" })
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER {table}_Activity_Insert AFTER INSERT ON {table}
                    BEGIN UPDATE Dialogues SET ActivityRevision = ActivityRevision + 1 WHERE Id = NEW.DialogueId; END;
                    CREATE TRIGGER {table}_Activity_Update AFTER UPDATE ON {table}
                    BEGIN UPDATE Dialogues SET ActivityRevision = ActivityRevision + 1 WHERE Id IN (OLD.DialogueId, NEW.DialogueId); END;
                    CREATE TRIGGER {table}_Activity_Delete AFTER DELETE ON {table}
                    BEGIN UPDATE Dialogues SET ActivityRevision = ActivityRevision + 1 WHERE Id = OLD.DialogueId; END;
                    """);
            }
            foreach (var action in new[] { "Insert", "Update", "Delete" })
            {
                var row = action == "Delete" ? "OLD" : "NEW";
                migrationBuilder.Sql($"""
                    CREATE TRIGGER History_Activity_{action} AFTER {action.ToUpperInvariant()} ON PreviewHistoryEntry
                    BEGIN UPDATE Dialogues SET ActivityRevision = ActivityRevision + 1
                    WHERE Id = (SELECT DialogueId FROM Previews WHERE Id = {row}.PreviewId); END;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "Previews", "DialogueRevisions", "History" })
                foreach (var action in new[] { "Insert", "Update", "Delete" })
                    migrationBuilder.Sql($"DROP TRIGGER {table}_Activity_{action};");
            migrationBuilder.Sql("ALTER TABLE Dialogues DROP COLUMN ActivityRevision;");
        }
    }
}
