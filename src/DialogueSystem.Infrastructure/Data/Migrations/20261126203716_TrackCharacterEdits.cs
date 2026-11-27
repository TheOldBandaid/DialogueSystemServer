using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DialogueSystem.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackCharacterEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "Characters",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
            migrationBuilder.Sql("""
                CREATE TRIGGER Characters_EditRevision_Update AFTER UPDATE OF Name ON Characters
                BEGIN UPDATE Characters SET Revision = Revision + 1 WHERE Id = NEW.Id; END;
                CREATE TRIGGER Lines_CharacterRevision_Insert AFTER INSERT ON Lines
                BEGIN UPDATE Characters SET Revision = Revision + 1 WHERE Id = NEW.SpeakerId; END;
                CREATE TRIGGER Lines_CharacterRevision_Update AFTER UPDATE ON Lines
                BEGIN UPDATE Characters SET Revision = Revision + 1 WHERE Id IN (OLD.SpeakerId, NEW.SpeakerId); END;
                CREATE TRIGGER Lines_CharacterRevision_Delete AFTER DELETE ON Lines
                BEGIN UPDATE Characters SET Revision = Revision + 1 WHERE Id = OLD.SpeakerId; END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER Characters_EditRevision_Update;
                DROP TRIGGER Lines_CharacterRevision_Insert;
                DROP TRIGGER Lines_CharacterRevision_Update;
                DROP TRIGGER Lines_CharacterRevision_Delete;
                ALTER TABLE Characters DROP COLUMN Revision;
                """);
        }
    }
}
