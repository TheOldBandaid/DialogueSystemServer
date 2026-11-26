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
            // Repair databases where an older rollback rebuilt Characters and lost its trigger.
            migrationBuilder.Sql("""
                CREATE TRIGGER IF NOT EXISTS Characters_Revision_Update AFTER UPDATE OF Name ON Characters
                BEGIN UPDATE Dialogues SET Revision = Revision + 1
                WHERE Id IN (SELECT DialogueId FROM Lines WHERE SpeakerId = NEW.Id); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The trigger belongs to AddGraphRevision and must remain when this repair is reverted.
        }
    }
}
