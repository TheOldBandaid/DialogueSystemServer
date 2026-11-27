using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestoreCharacterRevisionTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS Characters_Revision_Update AFTER UPDATE OF Name ON Characters
                BEGIN UPDATE Dialogues SET Revision = Revision + 1
                WHERE Id IN (SELECT DialogueId FROM Lines WHERE SpeakerId = NEW.Id); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
