using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGraphRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Dialogues",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            foreach (var table in new[] { "Lines", "Choices" })
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER {table}_Revision_Insert AFTER INSERT ON {table}
                    BEGIN UPDATE Dialogues SET Revision = Revision + 1 WHERE Id = NEW.DialogueId; END;
                    """);
                migrationBuilder.Sql($"""
                    CREATE TRIGGER {table}_Revision_Update AFTER UPDATE ON {table}
                    BEGIN UPDATE Dialogues SET Revision = Revision + 1 WHERE Id IN (OLD.DialogueId, NEW.DialogueId); END;
                    """);
                migrationBuilder.Sql($"""
                    CREATE TRIGGER {table}_Revision_Delete AFTER DELETE ON {table}
                    BEGIN UPDATE Dialogues SET Revision = Revision + 1 WHERE Id = OLD.DialogueId; END;
                    """);
            }
            migrationBuilder.Sql("""
                CREATE TRIGGER Dialogues_Revision_Update
                AFTER UPDATE OF Title, StartLineId, MinimumPlayerLevel ON Dialogues
                BEGIN UPDATE Dialogues SET Revision = Revision + 1 WHERE Id = NEW.Id; END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER Characters_Revision_Update AFTER UPDATE OF Name ON Characters
                BEGIN UPDATE Dialogues SET Revision = Revision + 1
                WHERE Id IN (SELECT DialogueId FROM Lines WHERE SpeakerId = NEW.Id); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "Lines", "Choices" })
                foreach (var action in new[] { "Insert", "Update", "Delete" })
                    migrationBuilder.Sql($"DROP TRIGGER {table}_Revision_{action};");
            migrationBuilder.Sql("DROP TRIGGER Dialogues_Revision_Update;");
            migrationBuilder.Sql("DROP TRIGGER Characters_Revision_Update;");
            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Dialogues");
        }
    }
}
